using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SmartBench.Models;
using SmartBench.Services;

namespace SmartBench.Views;

public partial class CommunicationView : UserControl, IDisposable
{
    private const int MinimumModbusIntervalMilliseconds = 100;
    private const int MaximumIntervalMilliseconds = 3_600_000;

    private SerialPort? _serialPort;
    private SerialPort? _modbusPort;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _udpCancellation;
    private Encoding _serialEncoding = Encoding.UTF8;
    private Decoder _serialDecoder = Encoding.UTF8.GetDecoder();
    private readonly DispatcherTimer _serialAutoSendTimer = new();

    private readonly SemaphoreSlim _modbusTransactionLock = new(1, 1);
    private readonly object _modbusResponseGate = new();
    private readonly List<byte> _modbusResponseBuffer = [];
    private readonly Dictionary<ModbusRegisterKey, string> _modbusAliases = [];
    private TaskCompletionSource<byte[]>? _modbusResponseWaiter;
    private ModbusRequestContext? _pendingModbusRequest;
    private CancellationTokenSource? _modbusPollingCancellation;
    private byte? _displayedModbusFunction;
    private bool _disposed;

    public ObservableCollection<ModbusRegisterItem> ModbusRegisters { get; } = [];

    public CommunicationView()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
        DataContext = this;
        _serialAutoSendTimer.Tick += (_, _) => SendSerialPayload(showErrors: false);
        RefreshPorts();
        RefreshModbusAddressHint();
        AppendLog(SerialLogBox, "系统", "请选择端口并打开串口。");
        AppendLog(ModbusLogBox, "系统", "支持 FC03、FC04 读取与 FC06、FC16 写入。读取会校验站号、功能码、帧长和 CRC16。");
        AppendLog(UdpLogBox, "系统", "监听默认绑定本机接口。请勿将调试端口直接暴露在公网。");
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var selectedSerial = SerialPortSelector.SelectedItem as string;
        var selectedModbus = ModbusPortSelector.SelectedItem as string;
        var ports = SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        SerialPortSelector.ItemsSource = ports;
        ModbusPortSelector.ItemsSource = ports;
        SerialPortSelector.SelectedItem = ports.Contains(selectedSerial) ? selectedSerial : ports.FirstOrDefault();
        ModbusPortSelector.SelectedItem = ports.Contains(selectedModbus) ? selectedModbus : ports.FirstOrDefault();
    }

    private void SerialConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serialPort?.IsOpen == true)
        {
            CloseSerial();
            return;
        }

        try
        {
            var portName = RequiredPort(SerialPortSelector);
            if (_modbusPort?.IsOpen == true && string.Equals(_modbusPort.PortName, portName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{portName} 正被 Modbus RTU 占用。请先关闭 Modbus 串口。");

            _serialPort = new SerialPort(portName, ComboNumber(SerialBaudSelector), ParseParity(SerialParity), ComboNumber(SerialDataBits), ParseStopBits(SerialStopBits))
            {
                Encoding = GetSelectedEncoding(),
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            _serialEncoding = _serialPort.Encoding;
            _serialDecoder = _serialEncoding.GetDecoder();
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.Open();
            SerialConnectButton.Content = "关闭串口";
            AppendLog(SerialLogBox, "系统", $"已打开 {portName}。");
        }
        catch (Exception exception)
        {
            CloseSerial();
            ShowCommunicationError("无法打开串口", exception);
        }
    }

    private void SerialSendButton_Click(object sender, RoutedEventArgs e) => SendSerialPayload(showErrors: true);

    private bool SendSerialPayload(bool showErrors)
    {
        if (_serialPort?.IsOpen != true)
        {
            if (showErrors)
                ShowCommunicationError("串口未打开", new InvalidOperationException("请先选择端口并点击“打开串口”。"));
            else
                AppendLog(SerialLogBox, "警告", "循环发送已停止：串口未打开。");
            SerialAutoSend.IsChecked = false;
            return false;
        }

        try
        {
            var payload = SerialSendAsHex.IsChecked == true
                ? HexCodec.Parse(SerialSendBox.Text)
                : _serialEncoding.GetBytes(SerialSendBox.Text + SelectedLineEnding());
            _serialPort.Write(payload, 0, payload.Length);
            AppendLog(SerialLogBox, "TX", FormatPayload(payload, _serialEncoding));
            return true;
        }
        catch (Exception exception)
        {
            if (showErrors) ShowCommunicationError("串口发送失败", exception);
            else AppendLog(SerialLogBox, "错误", $"循环发送已停止：{exception.Message}");
            SerialAutoSend.IsChecked = false;
            return false;
        }
    }

    private void SerialAutoSend_Checked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SerialAutoIntervalBox.Text, out var interval) || interval is < 20 or > MaximumIntervalMilliseconds)
        {
            SerialAutoSend.IsChecked = false;
            ShowCommunicationError("循环发送间隔无效", new InvalidOperationException("间隔需在 20 到 3,600,000 毫秒之间。"));
            return;
        }

        _serialAutoSendTimer.Interval = TimeSpan.FromMilliseconds(interval);
        _serialAutoSendTimer.Start();
        AppendLog(SerialLogBox, "系统", $"已开启循环发送：每 {interval} ms 发送一次。");
    }

    private void SerialAutoSend_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_serialAutoSendTimer.IsEnabled) return;
        _serialAutoSendTimer.Stop();
        AppendLog(SerialLogBox, "系统", "已停止循环发送。");
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var port = (SerialPort)sender;
            var length = port.BytesToRead;
            if (length == 0) return;
            var buffer = new byte[length];
            port.Read(buffer, 0, length);
            AppendLog(SerialLogBox, "RX", $"HEX  {HexCodec.Format(buffer)}\nTXT  {DecodeSerialChunk(buffer)}");
        }
        catch (Exception exception)
        {
            AppendLog(SerialLogBox, "错误", exception.Message);
        }
    }

    private void ClearSerialLog_Click(object sender, RoutedEventArgs e) => SerialLogBox.Clear();
    private void SaveSerialLog_Click(object sender, RoutedEventArgs e) => SaveLog(SerialLogBox.Text, "串口日志");

    private void ModbusConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modbusPort?.IsOpen == true)
        {
            CloseModbus();
            return;
        }

        try
        {
            var portName = RequiredPort(ModbusPortSelector);
            if (_serialPort?.IsOpen == true && string.Equals(_serialPort.PortName, portName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{portName} 正被 RS232 串口占用。请先关闭 RS232 串口。");

            _modbusPort = new SerialPort(portName, ComboNumber(ModbusBaudSelector), ParseParity(ModbusParity), ComboNumber(ModbusDataBits), ParseStopBits(ModbusStopBits))
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            _modbusPort.DataReceived += ModbusPort_DataReceived;
            _modbusPort.Open();
            ModbusConnectButton.Content = "关闭串口";
            ModbusStatusText.Text = $"已连接 {portName} · {_modbusPort.BaudRate} bps";
            AppendLog(ModbusLogBox, "系统", $"已打开 {portName}：{_modbusPort.DataBits}{_modbusPort.Parity.ToString()[0]}{(int)_modbusPort.StopBits}，{_modbusPort.BaudRate} bps。");
        }
        catch (Exception exception)
        {
            CloseModbus();
            ShowCommunicationError("无法打开 Modbus 串口", exception);
        }
    }

    private async void ModbusSendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var operation = BuildModbusOperation();
            var timeout = ReadTimeout();
            SetModbusStatus("正在执行单次请求…");
            var response = await ExecuteModbusRequestAsync(operation.Request, operation.Context, timeout, CancellationToken.None);
            ApplyModbusResponse(operation, response);
            SetModbusStatus("单次请求完成");
        }
        catch (OperationCanceledException)
        {
            SetModbusStatus("请求已停止");
        }
        catch (Exception exception)
        {
            MarkModbusRows("请求失败");
            SetModbusStatus("请求失败");
            ShowCommunicationError("Modbus 请求失败", exception);
        }
    }

    private async void WriteSelectedRegister_Click(object sender, RoutedEventArgs e)
    {
        if (ModbusRegisterGrid.SelectedItem is not ModbusRegisterItem item)
        {
            ShowCommunicationError("未选择寄存器", new InvalidOperationException("请先在数据窗口中选择要写入的一行。"));
            return;
        }
        if (_displayedModbusFunction != 0x03)
        {
            ShowCommunicationError("当前数据不可写", new InvalidOperationException("输入寄存器（04）为只读。请先读取保持寄存器（03）。"));
            return;
        }

        try
        {
            var context = new ModbusRequestContext(item.Station, 0x06, item.Address, 1);
            var response = await ExecuteModbusRequestAsync(ModbusRtu.WriteSingleRegister(item.Station, item.Address, item.Value), context, ReadTimeout(), CancellationToken.None);
            EnsureWriteResponse(response, context);
            item.State = "写入成功";
            item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            AppendLog(ModbusLogBox, "系统", $"已写入 {item.DeviceAddress}：{item.Value}。");
            SetModbusStatus($"已写入 {item.DeviceAddress}");
        }
        catch (Exception exception)
        {
            item.State = "写入失败";
            SetModbusStatus("写入失败");
            ShowCommunicationError("写寄存器失败", exception);
        }
    }

    private async void ModbusAutoPoll_Checked(object sender, RoutedEventArgs e)
    {
        if (_modbusPort?.IsOpen != true)
        {
            ModbusAutoPoll.IsChecked = false;
            ShowCommunicationError("Modbus 串口未打开", new InvalidOperationException("请先打开 Modbus 串口，再启动循环读取。"));
            return;
        }
        if (ModbusFunctionSelector.SelectedIndex is not (0 or 1))
        {
            ModbusAutoPoll.IsChecked = false;
            ShowCommunicationError("当前功能不支持轮询", new InvalidOperationException("循环读取仅支持功能码 03 和 04。"));
            return;
        }

        try
        {
            _ = ReadPollInterval();
            _ = ReadTimeout();
            _modbusPollingCancellation?.Cancel();
            _modbusPollingCancellation?.Dispose();
            _modbusPollingCancellation = new CancellationTokenSource();
            await PollModbusAsync(_modbusPollingCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetModbusStatus("循环读取已停止");
        }
        catch (Exception exception)
        {
            ModbusAutoPoll.IsChecked = false;
            SetModbusStatus("循环读取未启动");
            ShowCommunicationError("无法启动循环读取", exception);
        }
    }

    private void ModbusAutoPoll_Unchecked(object sender, RoutedEventArgs e)
    {
        _modbusPollingCancellation?.Cancel();
        SetModbusStatus(_modbusPort?.IsOpen == true ? "循环读取已停止" : "未连接");
    }

    private async Task PollModbusAsync(CancellationToken cancellationToken)
    {
        AppendLog(ModbusLogBox, "系统", "循环读取已启动。同一时间只会有一个 Modbus 请求在途。");
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var operation = BuildModbusOperation(requireReadFunction: true);
                var response = await ExecuteModbusRequestAsync(operation.Request, operation.Context, ReadTimeout(), cancellationToken);
                ApplyModbusResponse(operation, response);
                SetModbusStatus($"循环读取正常 · {DateTime.Now:HH:mm:ss}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                MarkModbusRows(exception is TimeoutException ? "超时" : "读取错误");
                AppendLog(ModbusLogBox, "警告", $"循环读取失败：{exception.Message}");
                SetModbusStatus(exception is TimeoutException ? "等待响应超时" : "循环读取错误");
            }

            var elapsed = DateTime.UtcNow - startedAt;
            var delay = Math.Max(0, ReadPollInterval() - (int)elapsed.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private ModbusOperation BuildModbusOperation(bool requireReadFunction = false)
    {
        if (_modbusPort?.IsOpen != true)
            throw new InvalidOperationException("请先打开 Modbus 串口。");

        if (!byte.TryParse(ModbusStationBox.Text.Trim(), out var station) || station is 0 or > 247)
            throw new InvalidOperationException("从站地址需为 1 到 247 的数字。地址 0 为广播地址，不在此处使用。");

        if (!ushort.TryParse(ModbusAddressBox.Text.Trim(), out var address))
            throw new InvalidOperationException("寄存器地址需为 0 到 65,535 的数字。");
        var function = ModbusFunctionSelector.SelectedIndex switch
        {
            0 => (byte)0x03,
            1 => (byte)0x04,
            2 => (byte)0x06,
            3 => (byte)0x10,
            _ => throw new InvalidOperationException("请选择 Modbus 功能码。")
        };
        if (requireReadFunction && function is not (0x03 or 0x04))
            throw new InvalidOperationException("循环读取仅支持功能码 03 和 04。");

        byte[] frame;
        ushort quantity;
        switch (function)
        {
            case 0x03:
                if (!ushort.TryParse(ModbusValueBox.Text.Trim(), out var readQuantity) || readQuantity is 0 or > 125)
                    throw new InvalidOperationException("寄存器数量需为 1 到 125 的数字。");
                quantity = readQuantity;
                frame = ModbusRtu.ReadHoldingRegisters(station, address, quantity);
                break;
            case 0x04:
                if (!ushort.TryParse(ModbusValueBox.Text.Trim(), out readQuantity) || readQuantity is 0 or > 125)
                    throw new InvalidOperationException("寄存器数量需为 1 到 125 的数字。");
                quantity = readQuantity;
                frame = ModbusRtu.ReadInputRegisters(station, address, quantity);
                break;
            case 0x06:
                if (!ushort.TryParse(ModbusValueBox.Text.Trim(), out var writeValue))
                    throw new InvalidOperationException("写入数值需为 0 到 65,535 的数字。");
                quantity = 1;
                frame = ModbusRtu.WriteSingleRegister(station, address, writeValue);
                break;
            case 0x10:
                var values = ParseRegisterValues(ModbusValueBox.Text);
                quantity = checked((ushort)values.Length);
                frame = ModbusRtu.WriteMultipleRegisters(station, address, values);
                break;
            default:
                throw new InvalidOperationException("不支持的 Modbus 功能码。");
        }

        RefreshModbusAddressHint(address, function);
        return new ModbusOperation(frame, new ModbusRequestContext(station, function, address, quantity));
    }

    private async Task<byte[]> ExecuteModbusRequestAsync(byte[] request, ModbusRequestContext context, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        await _modbusTransactionLock.WaitAsync(cancellationToken);
        TaskCompletionSource<byte[]>? waiter = null;
        try
        {
            var port = _modbusPort;
            if (port?.IsOpen != true) throw new InvalidOperationException("Modbus 串口已关闭。");

            waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_modbusResponseGate)
            {
                _modbusResponseBuffer.Clear();
                _pendingModbusRequest = context;
                _modbusResponseWaiter = waiter;
            }

            await Task.Run(() => port.Write(request, 0, request.Length), cancellationToken);
            AppendLog(ModbusLogBox, "TX", HexCodec.Format(request));

            var delay = Task.Delay(timeoutMilliseconds, cancellationToken);
            var completed = await Task.WhenAny(waiter.Task, delay);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != waiter.Task)
                throw new TimeoutException($"在 {timeoutMilliseconds} ms 内未收到匹配的 Modbus 响应。");

            return await waiter.Task;
        }
        finally
        {
            lock (_modbusResponseGate)
            {
                if (ReferenceEquals(_modbusResponseWaiter, waiter))
                {
                    _modbusResponseWaiter = null;
                    _pendingModbusRequest = null;
                    _modbusResponseBuffer.Clear();
                }
            }
            _modbusTransactionLock.Release();
        }
    }

    private void ModbusPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var port = (SerialPort)sender;
            var length = port.BytesToRead;
            if (length == 0) return;
            var buffer = new byte[length];
            port.Read(buffer, 0, length);
            AppendLog(ModbusLogBox, "RX", HexCodec.Format(buffer));
            ProcessModbusResponseChunk(buffer);
        }
        catch (Exception exception)
        {
            AppendLog(ModbusLogBox, "错误", exception.Message);
        }
    }

    private void ProcessModbusResponseChunk(byte[] chunk)
    {
        TaskCompletionSource<byte[]>? completedWaiter = null;
        byte[]? completeFrame = null;
        Exception? parseError = null;

        lock (_modbusResponseGate)
        {
            if (_pendingModbusRequest is null || _modbusResponseWaiter is null) return;
            _modbusResponseBuffer.AddRange(chunk);

            while (_modbusResponseBuffer.Count >= 2)
            {
                var pending = _pendingModbusRequest;
                if (pending is null) return;
                var expectedFunction = pending.Function;
                var start = FindModbusFrameStart(pending);
                if (start < 0)
                {
                    _modbusResponseBuffer.RemoveRange(0, Math.Max(0, _modbusResponseBuffer.Count - 1));
                    return;
                }
                if (start > 0) _modbusResponseBuffer.RemoveRange(0, start);
                if (_modbusResponseBuffer.Count < 3) return;

                var function = _modbusResponseBuffer[1];
                var frameLength = function == (byte)(expectedFunction | 0x80)
                    ? 5
                    : expectedFunction is 0x03 or 0x04
                        ? _modbusResponseBuffer[2] + 5
                        : 8;
                if (_modbusResponseBuffer.Count < frameLength) return;

                var candidate = _modbusResponseBuffer.Take(frameLength).ToArray();
                if (!ModbusRtu.HasValidCrc(candidate))
                {
                    _modbusResponseBuffer.RemoveAt(0);
                    AppendLog(ModbusLogBox, "警告", "收到 CRC 无效的候选帧，正在重新同步。");
                    continue;
                }

                _modbusResponseBuffer.RemoveRange(0, frameLength);
                completedWaiter = _modbusResponseWaiter;
                completeFrame = candidate;
                _pendingModbusRequest = null;
                _modbusResponseWaiter = null;

                if (function == expectedFunction && expectedFunction is 0x03 or 0x04 && candidate[2] != pending.Quantity * 2)
                    parseError = new FormatException($"响应字节数 {candidate[2]} 与请求数量 {pending.Quantity} 不一致。");
                break;
            }
        }

        if (completedWaiter is null || completeFrame is null) return;
        if (parseError is not null) completedWaiter.TrySetException(parseError);
        else completedWaiter.TrySetResult(completeFrame);
    }

    private int FindModbusFrameStart(ModbusRequestContext pending)
    {
        for (var index = 0; index < _modbusResponseBuffer.Count - 1; index++)
        {
            if (_modbusResponseBuffer[index] != pending.Station) continue;
            var function = _modbusResponseBuffer[index + 1];
            if (function == pending.Function || function == (byte)(pending.Function | 0x80)) return index;
        }
        return -1;
    }

    private void ApplyModbusResponse(ModbusOperation operation, byte[] response)
    {
        var context = operation.Context;
        if (response[0] != context.Station)
            throw new FormatException("响应从站地址与请求不匹配。");
        if (response[1] == (byte)(context.Function | 0x80))
            throw new InvalidOperationException(ModbusRtu.DescribeException(response[2]));
        if (response[1] != context.Function)
            throw new FormatException("响应功能码与请求不匹配。");

        if (context.Function is 0x03 or 0x04)
        {
            var values = ModbusRtu.ParseRegisterResponse(response);
            if (values.Length != context.Quantity)
                throw new FormatException($"响应寄存器数 {values.Length} 与请求数量 {context.Quantity} 不一致。");
            PopulateRegisterWindow(context, values);
            return;
        }

        EnsureWriteResponse(response, context);
        AppendLog(ModbusLogBox, "系统", "写入响应已确认。");
    }

    private static void EnsureWriteResponse(byte[] response, ModbusRequestContext context)
    {
        if (!ModbusRtu.HasValidCrc(response)) throw new FormatException("写入响应 CRC 校验失败。");
        if (response.Length != 8 || response[0] != context.Station || response[1] != context.Function)
            throw new FormatException("写入响应与请求不匹配。");
    }

    private void PopulateRegisterWindow(ModbusRequestContext context, IReadOnlyList<ushort> values)
    {
        CaptureCurrentAliases();
        ModbusRegisters.Clear();
        var now = DateTime.Now.ToString("HH:mm:ss");
        for (var index = 0; index < values.Count; index++)
        {
            var address = checked((ushort)(context.Address + index));
            var key = new ModbusRegisterKey(context.Station, context.Function, address);
            ModbusRegisters.Add(new ModbusRegisterItem
            {
                Station = context.Station,
                Address = address,
                DeviceAddress = FormatDeviceAddress(context.Function, address),
                Alias = _modbusAliases.GetValueOrDefault(key, string.Empty),
                Value = values[index],
                State = "正常",
                UpdatedAt = now
            });
        }

        _displayedModbusFunction = context.Function;
        RefreshModbusAddressHint(context.Address, context.Function);
    }

    private void CaptureCurrentAliases()
    {
        if (_displayedModbusFunction is not (0x03 or 0x04)) return;
        foreach (var item in ModbusRegisters)
            _modbusAliases[new ModbusRegisterKey(item.Station, _displayedModbusFunction.Value, item.Address)] = item.Alias.Trim();
    }

    private void MarkModbusRows(string state)
    {
        foreach (var item in ModbusRegisters)
        {
            item.State = state;
            item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private void ClearModbusData_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentAliases();
        ModbusRegisters.Clear();
        _displayedModbusFunction = null;
    }

    private void ClearModbusLog_Click(object sender, RoutedEventArgs e) => ModbusLogBox.Clear();
    private void SaveModbusLog_Click(object sender, RoutedEventArgs e) => SaveLog(ModbusLogBox.Text, "Modbus日志");

    private void ModbusFunctionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModbusValueCaption is null || ModbusValueBox is null) return;
        (ModbusValueCaption.Text, ModbusValueBox.Text) = ModbusFunctionSelector.SelectedIndex switch
        {
            0 or 1 => ("寄存器数量", "10"),
            2 => ("写入数值", "0"),
            3 => ("多个数值（逗号分隔）", "0, 0"),
            _ => ("参数", string.Empty)
        };
        if (ModbusAutoPoll is not null && ModbusFunctionSelector.SelectedIndex is not (0 or 1))
            ModbusAutoPoll.IsChecked = false;
        RefreshModbusAddressHint();
    }

    private async void UdpStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_udpClient is not null)
        {
            await CloseUdpAsync();
            return;
        }

        try
        {
            if (!int.TryParse(UdpLocalPortBox.Text.Trim(), out var localPort) || localPort is < 1 or > 65535)
                throw new InvalidOperationException("本地端口需为 1 到 65535 的数字。");
            var bindAddress = IPAddress.Parse(ComboText(UdpBindSelector));
            _udpClient = new UdpClient(new IPEndPoint(bindAddress, localPort));
            _udpCancellation = new CancellationTokenSource();
            UdpStartButton.Content = "停止监听";
            AppendLog(UdpLogBox, "系统", $"开始在 {bindAddress}:{localPort} 监听。");
            _ = ReceiveUdpAsync(_udpClient, _udpCancellation.Token);
        }
        catch (Exception exception)
        {
            await CloseUdpAsync();
            ShowCommunicationError("无法启动 UDP 监听", exception);
        }
    }

    private async Task ReceiveUdpAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken);
                if (result.Buffer.Length > 65_507)
                {
                    AppendLog(UdpLogBox, "警告", "收到超出允许范围的 UDP 报文，已忽略。");
                    continue;
                }
                AppendLog(UdpLogBox, $"RX {result.RemoteEndPoint}", FormatPayload(result.Buffer, Encoding.UTF8));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { AppendLog(UdpLogBox, "错误", exception.Message); }
    }

    private async void UdpSendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = UdpHostBox.Text.Trim();
            if (host.Length == 0)
                throw new InvalidOperationException("请填写目标主机地址或主机名。");
            if (!int.TryParse(UdpRemotePortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("目标端口需为 1 到 65535 的数字。");
            var payload = UdpSendAsHex.IsChecked == true ? HexCodec.Parse(UdpSendBox.Text) : Encoding.UTF8.GetBytes(UdpSendBox.Text);
            if (payload.Length > 65_507) throw new InvalidOperationException("UDP 单个数据报不能超过 65,507 字节。");
            using var senderClient = new UdpClient { EnableBroadcast = UdpBroadcast.IsChecked == true };
            await senderClient.SendAsync(payload, payload.Length, host, port);
            AppendLog(UdpLogBox, "TX", $"{host}:{port}  {FormatPayload(payload, Encoding.UTF8)}");
        }
        catch (Exception exception)
        {
            ShowCommunicationError("UDP 发送失败", exception);
        }
    }

    private void ClearUdpLog_Click(object sender, RoutedEventArgs e) => UdpLogBox.Clear();
    private void SaveUdpLog_Click(object sender, RoutedEventArgs e) => SaveLog(UdpLogBox.Text, "UDP日志");

    private async Task CloseUdpAsync()
    {
        _udpCancellation?.Cancel();
        _udpClient?.Dispose();
        _udpClient = null;
        _udpCancellation?.Dispose();
        _udpCancellation = null;
        UdpStartButton.Content = "开始监听";
        await Task.CompletedTask;
    }

    private void CloseSerial()
    {
        _serialAutoSendTimer.Stop();
        if (SerialAutoSend is not null) SerialAutoSend.IsChecked = false;
        if (_serialPort is not null)
        {
            _serialPort.DataReceived -= SerialPort_DataReceived;
            if (_serialPort.IsOpen) _serialPort.Close();
            _serialPort.Dispose();
            _serialPort = null;
        }
        SerialConnectButton.Content = "打开串口";
    }

    private void CloseModbus()
    {
        _modbusPollingCancellation?.Cancel();
        _modbusPollingCancellation?.Dispose();
        _modbusPollingCancellation = null;
        if (ModbusAutoPoll is not null) ModbusAutoPoll.IsChecked = false;

        lock (_modbusResponseGate)
        {
            _modbusResponseWaiter?.TrySetCanceled();
            _modbusResponseWaiter = null;
            _pendingModbusRequest = null;
            _modbusResponseBuffer.Clear();
        }

        if (_modbusPort is not null)
        {
            _modbusPort.DataReceived -= ModbusPort_DataReceived;
            if (_modbusPort.IsOpen) _modbusPort.Close();
            _modbusPort.Dispose();
            _modbusPort = null;
        }
        ModbusConnectButton.Content = "打开串口";
        SetModbusStatus("未连接");
    }

    private int ReadPollInterval()
    {
        if (!int.TryParse(ModbusPollIntervalBox.Text, out var interval) || interval is < MinimumModbusIntervalMilliseconds or > MaximumIntervalMilliseconds)
            throw new InvalidOperationException($"轮询周期需在 {MinimumModbusIntervalMilliseconds} 到 {MaximumIntervalMilliseconds:N0} ms 之间。");
        return interval;
    }

    private int ReadTimeout()
    {
        if (!int.TryParse(ModbusTimeoutBox.Text, out var timeout) || timeout is < 50 or > MaximumIntervalMilliseconds)
            throw new InvalidOperationException("响应超时需在 50 到 3,600,000 ms 之间。");
        return timeout;
    }

    private void RefreshModbusAddressHint()
    {
        if (ModbusAddressHint is null) return;
        if (!ushort.TryParse(ModbusAddressBox?.Text, out var address)) address = 0;
        var function = ModbusFunctionSelector?.SelectedIndex == 1 ? (byte)0x04 : (byte)0x03;
        RefreshModbusAddressHint(address, function);
    }

    private void RefreshModbusAddressHint(ushort address, byte function)
    {
        if (ModbusAddressHint is null) return;
        var deviceAddress = FormatDeviceAddress(function is 0x04 ? (byte)0x04 : (byte)0x03, address);
        ModbusAddressHint.Text = $"{deviceAddress} · 设备序号 {address + 1}";
    }

    private static string FormatDeviceAddress(byte function, ushort address) =>
        function == 0x04 ? $"IR{30_001 + address}" : $"HR{40_001 + address}";

    private void SetModbusStatus(string text) => Dispatcher.InvokeAsync(() => ModbusStatusText.Text = text);

    private static string RequiredPort(ComboBox selector) => selector.SelectedItem as string ?? throw new InvalidOperationException("当前未发现可用串口。请检查设备连接后刷新。");
    private static int ComboNumber(ComboBox selector) => int.Parse(((ComboBoxItem)selector.SelectedItem).Content.ToString()!);
    private static string ComboText(ComboBox selector) => ((ComboBoxItem)selector.SelectedItem).Content.ToString()!;
    private static Parity ParseParity(ComboBox selector) => Enum.Parse<Parity>(((ComboBoxItem)selector.SelectedItem).Content.ToString()!);
    private static StopBits ParseStopBits(ComboBox selector) => ((ComboBoxItem)selector.SelectedItem).Content.ToString() == "2" ? StopBits.Two : StopBits.One;
    private static string FormatPayload(byte[] payload, Encoding encoding) => $"HEX  {HexCodec.Format(payload)}\nTXT  {encoding.GetString(payload)}";

    private Encoding GetSelectedEncoding() => ComboText(SerialEncodingSelector) switch
    {
        "GBK" => Encoding.GetEncoding(936),
        "ASCII" => Encoding.ASCII,
        _ => Encoding.UTF8
    };

    private string SelectedLineEnding() => ComboText(SerialLineEndingSelector) switch
    {
        "CRLF" => "\r\n",
        "LF" => "\n",
        "CR" => "\r",
        _ => string.Empty
    };

    private string DecodeSerialChunk(byte[] payload)
    {
        var characters = new char[_serialDecoder.GetCharCount(payload, 0, payload.Length)];
        _serialDecoder.GetChars(payload, 0, payload.Length, characters, 0);
        return new string(characters);
    }

    private static ushort[] ParseRegisterValues(string input)
    {
        var tokens = input.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new FormatException("请输入至少一个寄存器数值。");
        return tokens.Select(token => ushort.Parse(token)).ToArray();
    }

    private void AppendLog(TextBox target, string direction, string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            const int maximumLogLength = 48_000;
            target.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {direction,-10} {message}\n");
            if (target.Text.Length > maximumLogLength) target.Text = target.Text[^maximumLogLength..];
            target.ScrollToEnd();
        });
    }

    private static void ShowCommunicationError(string title, Exception exception) =>
        MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void SaveLog(string content, string prefix)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存通讯日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, content, Encoding.UTF8); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "无法保存日志", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serialAutoSendTimer.Stop();
        CloseSerial();
        CloseModbus();
        _udpCancellation?.Cancel();
        _udpClient?.Dispose();
        _udpCancellation?.Dispose();
        _udpCancellation = null;
    }

    private sealed record ModbusOperation(byte[] Request, ModbusRequestContext Context);
    private sealed record ModbusRequestContext(byte Station, byte Function, ushort Address, ushort Quantity);
    private readonly record struct ModbusRegisterKey(byte Station, byte Function, ushort Address);
}
