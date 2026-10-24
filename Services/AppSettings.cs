using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmartBench.Services;

public sealed class AppSettings
{
    public bool AutoFormatNotReady { get; set; }

    // RS232 串口
    public string SerialPort { get; set; } = string.Empty;
    public string SerialBaud { get; set; } = "115200";
    public string SerialDataBits { get; set; } = "8";
    public string SerialParity { get; set; } = "None";
    public string SerialStopBits { get; set; } = "1";
    public string SerialEncoding { get; set; } = "UTF-8";
    public string SerialLineEnding { get; set; } = "不追加";
    public string SerialAutoInterval { get; set; } = "1000";
    public bool SerialAutoSendEnabled { get; set; }

    // Modbus RTU
    public string ModbusPort { get; set; } = string.Empty;
    public string ModbusBaud { get; set; } = "9600";
    public string ModbusDataBits { get; set; } = "8";
    public string ModbusParity { get; set; } = "Even";
    public string ModbusStopBits { get; set; } = "1";
    public string ModbusStation { get; set; } = "1";
    public int ModbusFunctionIndex { get; set; }
    public string ModbusAddress { get; set; } = "0";
    public string ModbusValue { get; set; } = "10";
    public string ModbusPollInterval { get; set; } = "1000";
    public string ModbusTimeout { get; set; } = "1000";

    // UDP
    public string UdpLocalPort { get; set; } = "5000";
    public string UdpBindAddress { get; set; } = "127.0.0.1";
    public string UdpRemoteHost { get; set; } = "127.0.0.1";
    public string UdpRemotePort { get; set; } = "5001";
    public bool UdpBroadcast { get; set; }

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartBench");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时回退到默认值。
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            // 不转义非 ASCII 字符，配置文件保持可读。
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            // 写入 UTF-8 BOM：记事本、PowerShell 等按系统 ANSI 代码页读取的第三方工具
            // 才能正确识别中文，否则会显示乱码甚至误判文件损坏。
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, options), new UTF8Encoding(true));
        }
        catch
        {
            // 配置写入失败不阻断主流程。
        }
    }
}
