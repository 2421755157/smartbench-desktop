using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SmartBench.Models;
using SmartBench.Services;

namespace SmartBench.Views;

public partial class UsbTransferView : UserControl, IDisposable
{
    private const uint SHFMT_ID_DEFAULT = 0xFFFF;
    private const uint SHFMT_CANCEL = 0xFFFFFFFE;
    private const uint SHFMT_NOFORMAT = 0xFFFFFFFD;
    private const uint SHFMT_ERROR = 0xFFFFFFFF;

    [DllImport("shell32.dll")]
    private static extern uint SHFormatDrive(IntPtr hwnd, uint drive, uint fmtId, uint options);

    public ObservableCollection<UsbDriveItem> Drives { get; } = [];
    public ObservableCollection<TransferFileItem> TransferFiles { get; } = [];

    private readonly DispatcherTimer _driveRefreshTimer;
    private readonly DispatcherTimer _taskTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly Stopwatch _taskStopwatch = new();
    private readonly HashSet<string> _uploadedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _autoFormatAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transferFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transferDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScannedDirectory> _transferDirectories = [];
    private readonly Queue<string> _pendingUploadPaths = new();
    private readonly object _uploadGate = new();
    private CancellationTokenSource? _transferCancellation;
    private readonly AppSettings _settings;
    private long _copiedBytes;
    private long _totalBytes;
    private int _formatDone;
    private int _formatTotal;
    private bool _isFormatting;
    private bool _isTransferring;
    private bool _isLoadingFiles;
    private bool _disposed;

    public UsbTransferView()
    {
        InitializeComponent();
        DataContext = this;
        _settings = AppSettings.Load();
        AutoFormatCheck.IsChecked = _settings.AutoFormatNotReady;
        RefreshDrives();
        _driveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _driveRefreshTimer.Tick += (_, _) => { if (!_isTransferring && !_isFormatting) RefreshDrives(); };
        _driveRefreshTimer.Start();
        _taskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _taskTimer.Tick += (_, _) => UpdateTaskTimeText();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += (_, _) => { if (_isTransferring) RefreshTransferProgress(); };
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        var discovered = new List<UsbDriveItem>();
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Removable).OrderBy(drive => drive.Name).Take(10))
        {
            try
            {
                if (!drive.IsReady)
                {
                    discovered.Add(new UsbDriveItem
                    {
                        RootPath = drive.Name,
                        Identity = drive.Name,
                        Label = "可移动设备",
                        State = "需格式化或不可读取",
                        FreeBytes = 0,
                        IsReady = false
                    });
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "未命名 U 盘" : drive.VolumeLabel;
                discovered.Add(new UsbDriveItem
                {
                    RootPath = drive.Name,
                    Identity = VolumeIdentity.Get(drive.Name),
                    Label = label,
                    State = "可用",
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true
                });
            }
            catch (IOException)
            {
                discovered.Add(new UsbDriveItem
                {
                    RootPath = drive.Name,
                    Identity = drive.Name,
                    Label = "可移动设备",
                    State = "需格式化或不可读取",
                    FreeBytes = 0,
                    IsReady = false
                });
            }
            catch (UnauthorizedAccessException)
            {
                discovered.Add(new UsbDriveItem
                {
                    RootPath = drive.Name,
                    Identity = drive.Name,
                    Label = "可移动设备",
                    State = "访问受限",
                    FreeBytes = 0,
                    IsReady = false
                });
            }
        }

        ApplyDriveDiff(discovered);
        PruneAutoFormatAttempts();

        DriveSummary.Text = discovered.Count switch
        {
            0 => "未发现可用的可移动设备",
            >= 10 => "已显示前 10 个可移动设备",
            _ => $"已识别 {discovered.Count} 个可移动设备"
        };
        var notReadyCount = discovered.Count(device => !device.IsReady);
        UsbHintText.Text = notReadyCount == 0
            ? "所有设备状态正常。勾选设备与文件后点击“批量下发”即可开始复制。"
            : $"检测到 {notReadyCount} 个设备需要格式化（该状态下无法下发）：勾选它们后点击“批量格式化”即可并行处理，格式化完成会自动变为“可用”；也可开启“插入需格式化 U 盘时自动格式化”由软件代劳。";

        if (AutoFormatCheck.IsChecked == true && !_isTransferring && !_isFormatting)
        {
            var pending = discovered
                .Where(device => !device.IsReady)
                .Where(device =>
                {
                    _autoFormatAttempts.TryGetValue(device.RootPath, out var attempts);
                    return attempts < 3;
                })
                .ToArray();
            if (pending.Length > 0) _ = AutoFormatPendingAsync(pending);
        }
    }

    /// <summary>
    /// 增量刷新设备列表：只更新属性与增删条目，不重建集合。
    /// 之前每 3 秒 Clear + Add 会让 DataGrid 丢失滚动位置与勾选状态，用户点复选框的瞬间行对象被替换，
    /// 勾选状态随之丢失；增量更新后行对象保持不变，勾选与滚动都能稳定保留。
    /// </summary>
    private void ApplyDriveDiff(IReadOnlyList<UsbDriveItem> discovered)
    {
        var existingByRoot = new Dictionary<string, UsbDriveItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Drives) existingByRoot[item.RootPath] = item;

        var finalItems = new List<UsbDriveItem>(discovered.Count);
        foreach (var device in discovered)
        {
            if (!existingByRoot.TryGetValue(device.RootPath, out var existing))
            {
                finalItems.Add(device);
                continue;
            }

            // 盘符被别的设备复用时（例如拔掉 U 盘 A 插上 U 盘 B 占用同一盘符），不能把勾选状态带过去。
            var identityChanged = !string.Equals(existing.Identity, device.Identity, StringComparison.OrdinalIgnoreCase);
            var wasSelected = existing.IsSelected;
            existing.ApplyFrom(device);
            existing.IsSelected = identityChanged ? false : wasSelected;
            finalItems.Add(existing);
        }

        // 用 Move / Insert / Remove 做真正的增量操作，避免 Clear + Add 重建行容器
        // （重建会丢失滚动位置，并让 DataGrid 重新生成所有行视觉）。
        for (var index = Drives.Count - 1; index >= 0; index--)
        {
            if (!finalItems.Contains(Drives[index])) Drives.RemoveAt(index);
        }

        for (var index = 0; index < finalItems.Count; index++)
        {
            var desired = finalItems[index];
            var currentIndex = IndexOfItem(Drives, desired);
            if (currentIndex < 0) Drives.Insert(index, desired);
            else if (currentIndex != index) Drives.Move(currentIndex, index);
        }
    }

    private static int IndexOfItem(ObservableCollection<UsbDriveItem> items, UsbDriveItem target)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target)) return index;
        }
        return -1;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要完整下发的文件夹" };
        if (dialog.ShowDialog() == true) await AddUploadPathsAsync([dialog.FolderName]);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要下发的文件",
            Filter = "所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true) await AddUploadPathsAsync(dialog.FileNames);
    }

    private void SelectAllDrives_Click(object sender, RoutedEventArgs e)
    {
        foreach (var drive in Drives) drive.IsSelected = true;
    }

    private void SettingCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoFormatNotReady = AutoFormatCheck.IsChecked == true;
        _settings.Save();
    }

    private void ClearDriveSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var drive in Drives) drive.IsSelected = false;
    }

    private void FormatDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsbDriveItem drive }) return;
        if (_isTransferring || _isFormatting)
        {
            MessageBox.Show("有任务正在执行，不能格式化设备。", "格式化 U 盘", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var driveLetter = char.ToUpperInvariant(drive.RootPath.FirstOrDefault());
        if (driveLetter is < 'A' or > 'Z')
        {
            MessageBox.Show($"无法识别设备盘符：{drive.RootPath}", "格式化 U 盘", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var stateHint = drive.IsReady
            ? $"设备：{drive.RootPath}（{drive.Label}）"
            : $"设备：{drive.RootPath}（当前不可读取，可能需要格式化）";
        var approval = MessageBox.Show(
            $"{stateHint}\n\n格式化将清除该 U 盘上的全部数据，且无法恢复！\n确认继续吗？",
            "格式化 U 盘", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (approval != MessageBoxResult.Yes) return;

        var owner = Window.GetWindow(this);
        var hwnd = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var result = SHFormatDrive(hwnd, (uint)(driveLetter - 'A'), SHFMT_ID_DEFAULT, 0);
        RefreshDrives();
        if (result == SHFMT_CANCEL) return;
        if (result is SHFMT_ERROR or SHFMT_NOFORMAT)
        {
            MessageBox.Show(result == SHFMT_NOFORMAT
                ? $"{drive.RootPath} 无法完成格式化，设备可能已损坏或被写保护。"
                : "格式化过程发生错误，请重试或更换设备。", "格式化 U 盘", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TransferStatus.Text = $"{drive.RootPath} 格式化完成。";
    }

    private async void BatchFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring || _isFormatting)
        {
            MessageBox.Show("有任务正在执行，请等待完成后再试。", "批量格式化", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targets = Drives.Where(drive => drive.IsSelected).ToArray();
        var autoSelected = false;
        if (targets.Length == 0 && Drives.Count > 0)
        {
            targets = Drives.ToArray();
            autoSelected = true;
        }
        if (targets.Length == 0)
        {
            MessageBox.Show("当前没有已识别的可移动设备。", "批量格式化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var listing = string.Join("\n", targets.Select(target => target.IsReady
            ? $"  {target.RootPath}（{target.Label}，{UsbDriveItem.FormatSize(target.FreeBytes)} 可用）"
            : $"  {target.RootPath}（需格式化或不可读取）"));
        var approval = MessageBox.Show(
            (autoSelected ? $"未勾选任何设备，已自动全选。\n\n" : "") +
            $"将对以下 {targets.Length} 个 U 盘执行快速格式化：\n\n{listing}\n\n" +
            "文件系统默认 FAT32（容量大于 32GB 时使用 exFAT；容量无法读取时也使用 FAT32）。\n\n" +
            "格式化将清除这些 U 盘上的全部数据，且无法恢复！确认继续吗？",
            "批量格式化确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (approval != MessageBoxResult.Yes) return;

        var failures = await RunBatchFormatCoreAsync(targets, "批量格式化");
        var resultMessage = failures.Count == 0
            ? $"全部 {targets.Length} 个 U 盘已格式化完成。请在系统中安全弹出后再拔出。"
            : $"已完成 {targets.Length - failures.Count} 个设备；以下设备格式化失败：\n\n{string.Join("\n", failures)}";
        MessageBox.Show(resultMessage, failures.Count == 0 ? "批量格式化完成" : "批量格式化结果", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task<List<string>> RunBatchFormatCoreAsync(IReadOnlyList<UsbDriveItem> targets, string label)
    {
        _isFormatting = true;
        _formatTotal = targets.Count;
        _formatDone = 0;
        TransferProgress.Maximum = targets.Count;
        TransferProgress.Value = 0;
        StartTransferButton.IsEnabled = false;
        _taskStopwatch.Restart();
        _taskTimer.Start();
        UpdateTaskTimeText();
        List<string> failures;

        try
        {
            failures = await Task.Run(async () =>
            {
                var failureList = new List<string>();
                var syncLock = new object();
                var tasks = targets.Select(async (target, index) =>
                {
                    try
                    {
                        if (index > 0) await Task.Delay(index * 400);
                        await FormatRemovableAsync(target.RootPath);
                    }
                    catch (Exception exception)
                    {
                        lock (syncLock) failureList.Add($"{target.RootPath}：{exception.Message}");
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref _formatDone);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            TransferStatus.Text = $"正在格式化（{done}/{targets.Count}，并行执行）…";
                            TransferProgress.Value = done;
                        });
                    }
                }).ToArray();
                await Task.WhenAll(tasks);
                return failureList;
            });

            TransferProgress.Value = targets.Count;
            TransferStatus.Text = failures.Count == 0
                ? $"{label}完成：{targets.Count} 个设备全部成功。"
                : $"{label}完成 {targets.Count - failures.Count}/{targets.Count} 个设备";
        }
        finally
        {
            _taskTimer.Stop();
            _taskStopwatch.Stop();
            _formatTotal = 0;
            _isFormatting = false;
            TransferProgress.Maximum = 100;
            TransferProgress.Value = 0;
            TransferTimeText.Text = $"上次任务用时 {_taskStopwatch.Elapsed:hh\\:mm\\:ss}";
            StartTransferButton.IsEnabled = true;
            RefreshDrives();
        }
        return failures;
    }

    private async Task AutoFormatPendingAsync(UsbDriveItem[] targets)
    {
        try
        {
            foreach (var target in targets)
            {
                _autoFormatAttempts[target.RootPath] = _autoFormatAttempts.GetValueOrDefault(target.RootPath) + 1;
            }

            var failures = await RunBatchFormatCoreAsync(targets, "自动格式化");
            foreach (var target in targets)
            {
                if (!failures.Any(failure => failure.StartsWith(target.RootPath, StringComparison.OrdinalIgnoreCase)))
                    _autoFormatAttempts.Remove(target.RootPath);
            }
            TransferStatus.Text = failures.Count == 0
                ? $"自动格式化完成：{string.Join("、", targets.Select(target => target.RootPath))}，设备已可读取。"
                : $"自动格式化完成 {targets.Length - failures.Count}/{targets.Length} 个设备；失败：{string.Join("；", failures)}（失败设备将在下轮自动重试，最多 3 次）";
        }
        catch (Exception exception)
        {
            TransferStatus.Text = $"自动格式化未完成：{exception.Message}";
        }
    }

    /// <summary>
    /// 清理已拔出设备的自动格式化计数。
    /// 计数原本按盘符记录，U 盘拔出再插入后计数仍是 3，该盘永远不再被自动格式化；
    /// 这里在设备消失时清掉计数，重新插入即可重新尝试。
    /// </summary>
    private void PruneAutoFormatAttempts()
    {
        if (_autoFormatAttempts.Count == 0) return;
        var present = new HashSet<string>(Drives.Select(drive => drive.RootPath), StringComparer.OrdinalIgnoreCase);
        foreach (var root in _autoFormatAttempts.Keys.Where(root => !present.Contains(root)).ToArray())
            _autoFormatAttempts.Remove(root);
    }

    private static async Task FormatRemovableAsync(string rootPath)
    {
        var letter = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (letter.Length != 2 || letter[1] != ':')
            throw new InvalidOperationException($"无法识别的盘符：{rootPath}");

        var totalBytes = GetVolumeCapacityBytes(letter);
        var fileSystem = totalBytes > 32L * 1024 * 1024 * 1024 ? "exFAT" : "FAT32";

        var startInfo = new ProcessStartInfo("format.com", $"{letter} /FS:{fileSystem} /Q /V: /Y /X")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 format.com。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await outputTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileSystem} 快速格式化失败{(string.IsNullOrWhiteSpace(output) ? "" : $"：{output.Split('\n').LastOrDefault(line => !string.IsNullOrWhiteSpace(line))}")}");
    }

    private static long GetVolumeCapacityBytes(string letter)
    {
        try { return new DriveInfo(letter).TotalSize; }
        catch { }

        try
        {
            using var volumes = new ManagementObjectSearcher($"SELECT Size FROM Win32_LogicalDisk WHERE DeviceID='{letter}'").Get();
            foreach (var volume in volumes)
            {
                if (ulong.TryParse(volume["Size"]?.ToString(), out var size) && size > 0) return (long)size;
            }
        }
        catch { }

        try
        {
            var deviceId = GetDiskDriveDeviceId(letter + "\\");
            if (deviceId is not null)
            {
                using var disks = new ManagementObjectSearcher("SELECT PNPDeviceID, Size FROM Win32_DiskDrive").Get();
                foreach (var disk in disks)
                {
                    if (string.Equals(disk["PNPDeviceID"]?.ToString(), deviceId, StringComparison.OrdinalIgnoreCase)
                        && ulong.TryParse(disk["Size"]?.ToString(), out var size) && size > 0)
                        return (long)size;
                }
            }
        }
        catch { }

        return -1;
    }

    private static string? GetDiskDriveDeviceId(string rootPath)
    {
        var letter = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var partitions = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} WHERE AssocClass = Win32_LogicalDiskToPartition").Get();
        foreach (var partition in partitions)
        {
            var partitionId = partition["DeviceID"]?.ToString();
            if (string.IsNullOrWhiteSpace(partitionId)) continue;
            using var disks = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition").Get();
            foreach (var disk in disks)
            {
                var pnpDeviceId = disk["PNPDeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(pnpDeviceId)) return pnpDeviceId;
            }
        }
        return null;
    }

    private void UpdateTaskTimeText()
    {
        var elapsed = _taskStopwatch.Elapsed;
        if (_formatTotal > 0)
        {
            var doneCount = Math.Min(_formatDone, _formatTotal);
            var remainingText = "估算中…";
            if (doneCount > 0)
            {
                var average = TimeSpan.FromTicks(elapsed.Ticks / doneCount);
                remainingText = $"剩余约 {TimeSpan.FromTicks(average.Ticks * (_formatTotal - doneCount)):hh\\:mm\\:ss}";
            }
            TransferTimeText.Text = $"已用 {elapsed:hh\\:mm\\:ss} · {remainingText} · 已完成 {doneCount}/{_formatTotal} 台";
            return;
        }

        var totalBytes = Interlocked.Read(ref _totalBytes);
        var copiedBytes = Interlocked.Read(ref _copiedBytes);
        var remaining = "…";
        if (totalBytes > 0 && copiedBytes > 0 && elapsed.TotalSeconds > 0)
        {
            var rate = copiedBytes / elapsed.TotalSeconds;
            if (rate > 0 && copiedBytes < totalBytes)
                remaining = $"剩余约 {TimeSpan.FromSeconds(Math.Max(0, (totalBytes - copiedBytes) / rate)):hh\\:mm\\:ss}";
            else if (copiedBytes >= totalBytes)
                remaining = "即将完成";
        }
        TransferTimeText.Text = $"已用 {elapsed:hh\\:mm\\:ss} · {remaining}";
    }

    private void DropSurface_DragEnter(object sender, DragEventArgs e)
    {
        UpdateDropAppearance(e.Data.GetDataPresent(DataFormats.FileDrop));
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropSurface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropSurface_DragLeave(object sender, DragEventArgs e) => UpdateDropAppearance(false);

    private async void DropSurface_Drop(object sender, DragEventArgs e)
    {
        UpdateDropAppearance(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddUploadPathsAsync(paths);
    }

    private void UpdateDropAppearance(bool highlighted)
    {
        DropSurface.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(highlighted ? "#E7F0FF" : "#F5F8FF")!);
        DropSurface.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(highlighted ? "#246BFD" : "#AFC7FF")!);
    }

    private async Task AddUploadPathsAsync(IEnumerable<string> rawPaths)
    {
        if (_isTransferring) return;
        var paths = rawPaths.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;

        // 扫描大目录很慢，用户很可能在扫描期间又拖了一批内容进来。
        // 之前直接 return 会把第二批内容静默丢掉，这里改成排队合并处理。
        bool shouldDrain;
        lock (_uploadGate)
        {
            foreach (var path in paths) _pendingUploadPaths.Enqueue(path);
            shouldDrain = !_isLoadingFiles;
            if (shouldDrain) _isLoadingFiles = true;
        }

        if (!shouldDrain) return;

        try
        {
            while (true)
            {
                string[] batch;
                lock (_uploadGate)
                {
                    if (_pendingUploadPaths.Count == 0)
                    {
                        // 与入队操作在同一把锁内判定，避免路径在"判空"与"置位"之间被丢弃。
                        _isLoadingFiles = false;
                        break;
                    }
                    batch = _pendingUploadPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    _pendingUploadPaths.Clear();
                }
                await ProcessUploadPathsAsync(batch);
            }
        }
        catch
        {
            lock (_uploadGate) _isLoadingFiles = false;
            throw;
        }
    }

    private async Task ProcessUploadPathsAsync(string[] paths)
    {
        TransferSummary.Text = "正在读取文件与目录结构…";
        var scan = await Task.Run(() => ScanUploadPaths(paths));
        var addedFiles = 0;
        var addedDirectories = 0;

        foreach (var root in scan.Roots)
        {
            _uploadedRoots.Add(root);
        }

        foreach (var directory in scan.Directories)
        {
            if (!_transferDirectoryPaths.Add(directory.DestinationRelativePath)) continue;
            _transferDirectories.Add(directory);
            addedDirectories++;
        }

        foreach (var file in scan.Files)
        {
            if (!_transferFilePaths.Add(file.FullPath)) continue;
            TransferFiles.Add(new TransferFileItem
            {
                FullPath = file.FullPath,
                Name = Path.GetFileName(file.FullPath),
                DestinationRelativePath = file.DestinationRelativePath,
                SizeBytes = file.SizeBytes
            });
            addedFiles++;
        }

        UpdateTransferSummary();
        TransferProgress.Value = 0;
        TransferTimeText.Text = "";
    }

    private static ScanResult ScanUploadPaths(IEnumerable<string> paths)
    {
        var roots = new List<string>();
        var directories = new List<ScannedDirectory>();
        var files = new List<ScannedFile>();
        var options = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var fullDirectory = Path.GetFullPath(path);
                    roots.Add(fullDirectory);
                    var rootName = DestinationRootName(fullDirectory);
                    directories.Add(new ScannedDirectory(Path.Combine(rootName)));

                    foreach (var directory in Directory.EnumerateDirectories(fullDirectory, "*", options))
                    {
                        var relative = Path.GetRelativePath(fullDirectory, directory);
                        directories.Add(new ScannedDirectory(Path.Combine(rootName, relative)));
                    }
                    foreach (var file in Directory.EnumerateFiles(fullDirectory, "*", options))
                    {
                        var relative = Path.GetRelativePath(fullDirectory, file);
                        TryAddScannedFile(files, file, Path.Combine(rootName, relative));
                    }
                }
                else if (File.Exists(path))
                {
                    var fullFile = Path.GetFullPath(path);
                    roots.Add(fullFile);
                    TryAddScannedFile(files, fullFile, Path.GetFileName(fullFile));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return new ScanResult(roots, directories, files);
    }

    private static string DestinationRootName(string fullDirectory)
    {
        var name = Path.GetFileName(fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "根目录" : name;
    }

    private static void TryAddScannedFile(ICollection<ScannedFile> items, string fullPath, string relativePath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            items.Add(new ScannedFile(file.FullName, relativePath, file.Length));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ClearUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
        TransferFiles.Clear();
        _uploadedRoots.Clear();
        _transferFilePaths.Clear();
        _transferDirectoryPaths.Clear();
        _transferDirectories.Clear();
        TransferSummary.Text = "拖入文件或文件夹，支持任意格式并保留目录层级";
        TransferStatus.Text = "等待选择设备与内容";
        TransferProgress.Value = 0;
        TransferTimeText.Text = "";
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        var shouldSelect = TransferFiles.Any(file => !file.IsSelected);
        foreach (var file in TransferFiles) file.IsSelected = shouldSelect;
    }

    private void RemoveSelectedFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
        var removal = TransferFiles.Where(file => file.IsSelected).ToArray();
        foreach (var file in removal)
        {
            TransferFiles.Remove(file);
            _transferFilePaths.Remove(file.FullPath);
        }
        UpdateTransferSummary();
        TransferStatus.Text = removal.Length == 0 ? "没有选中的文件" : $"已移除 {removal.Length} 个文件；目录结构仍会保留。";
    }

    private void UpdateTransferSummary()
    {
        var totalBytes = TransferFiles.Sum(file => file.SizeBytes);
        TransferSummary.Text = TransferFiles.Count == 0 && _transferDirectories.Count == 0
            ? "拖入文件或文件夹，支持任意格式并保留目录层级"
            : $"已载入 {TransferFiles.Count} 个文件、{_transferDirectories.Count} 个文件夹 · 合计 {UsbDriveItem.FormatSize(totalBytes)}";
    }

    private async void StartTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring || _isFormatting) return;
        var targets = Drives.Where(drive => drive.IsSelected && drive.IsReady).ToArray();
        var files = TransferFiles.Where(file => file.IsSelected).ToArray();
        var directories = _transferDirectories.ToArray();
        if (targets.Length == 0 || (files.Length == 0 && directories.Length == 0))
        {
            MessageBox.Show("请至少选择一个可写入的 U 盘，以及一个文件或文件夹。", "批量下发", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytesPerDrive = files.Sum(file => file.SizeBytes);
        var constrainedDrive = targets.FirstOrDefault(target => target.FreeBytes < bytesPerDrive);
        if (constrainedDrive is not null)
        {
            MessageBox.Show($"{constrainedDrive.RootPath} 可用空间不足，需要至少 {UsbDriveItem.FormatSize(bytesPerDrive)}。", "空间不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var approval = MessageBox.Show(
            $"确认将 {files.Length} 个文件、{directories.Length} 个文件夹复制到 {targets.Length} 个 U 盘？\n\n文件夹会按原名称和完整层级（包含空目录）复制到 U 盘根目录；单个文件也直接复制到根目录。若目标已有同名文件或文件夹，会跳过该项且不会创建自动改名副本。",
            "确认批量下发", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (approval != MessageBoxResult.Yes) return;

        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        StartTransferButton.IsEnabled = false;
        CancelTransferButton.IsEnabled = true;
        TransferProgress.Value = 0;
        var totalBytes = bytesPerDrive * targets.Length;
        var cancellationToken = _transferCancellation.Token;
        _copiedBytes = 0;
        _totalBytes = totalBytes;
        _progressCompleted = 0;
        _progressTotal = totalBytes;
        _progressStatus = $"并行下发中 · 已复制 0 / {UsbDriveItem.FormatSize(totalBytes)}";
        _taskStopwatch.Restart();
        _taskTimer.Start();
        _progressTimer.Start();
        UpdateTaskTimeText();

        try
        {
            var outcome = await Task.Run(async () =>
            {
                var failures = new List<string>();
                var skippedItems = new List<string>();
                var syncLock = new object();
                var completedDevices = 0;
                var tasks = targets.Select(async target =>
                {
                    try
                    {
                        ValidateTargetForWrite(target, bytesPerDrive);
                        ValidateSourceIsNotTarget(target);
                        var destinationRoot = target.RootPath;
                        Directory.CreateDirectory(destinationRoot);
                        var blockedFolderRoots = FindExistingFolderRoots(destinationRoot, directories);

                        foreach (var directory in directories)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (IsInBlockedFolderRoot(directory.DestinationRelativePath, blockedFolderRoots)) continue;
                            Directory.CreateDirectory(BuildSafeDestinationPath(destinationRoot, directory.DestinationRelativePath));
                        }

                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (IsInBlockedFolderRoot(file.DestinationRelativePath, blockedFolderRoots))
                            {
                                lock (syncLock) skippedItems.Add($"{target.RootPath}：文件夹“{TopLevelSegment(file.DestinationRelativePath)}”已存在，跳过其中内容。");
                                continue;
                            }
                            var destinationPath = BuildSafeDestinationPath(destinationRoot, file.DestinationRelativePath);
                            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                            {
                                lock (syncLock) skippedItems.Add($"{target.RootPath}：同名项“{file.DestinationRelativePath}”已存在，已跳过。");
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                            await CopyFileSafelyAsync(file.FullPath, destinationPath, count =>
                            {
                                var completed = Interlocked.Add(ref _copiedBytes, count);
                                ReportTransfer($"并行下发中 · 已复制 {UsbDriveItem.FormatSize(completed)} / {UsbDriveItem.FormatSize(totalBytes)}", completed, totalBytes);
                            }, cancellationToken);
                        }
                        Interlocked.Increment(ref completedDevices);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        lock (syncLock) failures.Add($"{target.RootPath}：{exception.Message}");
                        ReportTransfer($"并行下发中 · 已复制 {UsbDriveItem.FormatSize(Interlocked.Read(ref _copiedBytes))} / {UsbDriveItem.FormatSize(totalBytes)}（{target.RootPath} 失败）", Interlocked.Read(ref _copiedBytes), totalBytes);
                    }
                }).ToArray();

                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) { }
                catch { /* 单设备失败已在上方记录，其余设备继续 */ }

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                return new TransferOutcome(completedDevices, failures, skippedItems);
            });

            RefreshTransferProgress();
            TransferProgress.Value = totalBytes == 0 ? 100 : Math.Min(100, 100d * Interlocked.Read(ref _copiedBytes) / totalBytes);
            TransferStatus.Text = outcome.Failures.Count == 0
                ? outcome.SkippedItems.Count == 0
                    ? $"已完成：{outcome.CompletedDevices} 个设备 · {files.Length} 个文件 · {directories.Length} 个文件夹"
                    : $"已完成 {outcome.CompletedDevices} 个设备；{outcome.SkippedItems.Count} 项同名内容已跳过"
                : $"完成 {outcome.CompletedDevices} 个设备，{outcome.Failures.Count} 个设备失败";
            var resultMessage = outcome.Failures.Count == 0
                ? outcome.SkippedItems.Count == 0
                    ? "所有选定设备已完成复制。请在系统中安全弹出设备后再拔出。"
                    : $"所有选定设备已完成复制；以下 {outcome.SkippedItems.Count} 项因目标存在同名内容而跳过，未创建改名副本：\n\n{string.Join("\n", outcome.SkippedItems.Take(20))}"
                : $"已完成 {outcome.CompletedDevices} 个设备；以下设备未完成：\n\n{string.Join("\n", outcome.Failures)}";
            MessageBox.Show(resultMessage, outcome.Failures.Count == 0 ? "批量下发完成" : "批量下发结果", MessageBoxButton.OK, outcome.Failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RefreshDrives();
        }
        catch (OperationCanceledException)
        {
            TransferStatus.Text = "任务已取消；未完成的临时文件已清理。";
        }
        catch (Exception exception)
        {
            TransferStatus.Text = "任务未完成，请检查设备连接状态。";
            MessageBox.Show(exception.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transferCancellation?.Dispose();
            _transferCancellation = null;
            _isTransferring = false;
            _taskTimer.Stop();
            _progressTimer.Stop();
            _taskStopwatch.Stop();
            TransferProgress.Value = 0;
            TransferTimeText.Text = $"上次任务用时 {_taskStopwatch.Elapsed:hh\\:mm\\:ss}";
            StartTransferButton.IsEnabled = true;
            CancelTransferButton.IsEnabled = false;
        }
    }

    private void CancelTransferButton_Click(object sender, RoutedEventArgs e) => _transferCancellation?.Cancel();

    private void ValidateSourceIsNotTarget(UsbDriveItem target)
    {
        var targetRoot = Path.GetFullPath(target.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (_uploadedRoots.Any(source => Path.GetFullPath(source).StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("待下发源内容位于当前目标 U 盘上，已跳过以避免自我复制。");
    }

    private static void ValidateTargetForWrite(UsbDriveItem target, long requiredBytes)
    {
        var drive = new DriveInfo(target.RootPath);
        if (!drive.IsReady) throw new IOException($"{target.RootPath} 已不可读取。");
        if (!string.Equals(VolumeIdentity.Get(target.RootPath), target.Identity, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{target.RootPath} 的设备身份已改变，已拒绝写入。");
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"{target.RootPath} 可用空间不足。");
    }

    private static HashSet<string> FindExistingFolderRoots(string destinationRoot, IEnumerable<ScannedDirectory> directories)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in directories.Select(directory => TopLevelSegment(directory.DestinationRelativePath)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var targetPath = BuildSafeDestinationPath(destinationRoot, root);
            if (File.Exists(targetPath) || Directory.Exists(targetPath)) blocked.Add(root);
        }
        return blocked;
    }

    private static bool IsInBlockedFolderRoot(string relativePath, ISet<string> blockedRoots) =>
        blockedRoots.Contains(TopLevelSegment(relativePath));

    private static string TopLevelSegment(string relativePath) =>
        relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        ?? throw new InvalidOperationException("下发路径不包含有效名称。");

    private static string BuildSafeDestinationPath(string destinationRoot, string relativePath)
    {
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .Select(segment => string.Concat(segment.Where(character => !Path.GetInvalidFileNameChars().Contains(character))))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length == 0) throw new InvalidOperationException("下发路径不包含可写入的名称。");

        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标路径超出 U 盘下发目录，已拒绝写入。");
        return candidate;
    }

    private async Task CopyFileSafelyAsync(string source, string destinationPath, Action<int> onBytesCopied, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException($"目标位置已有同名项：{Path.GetFileName(destinationPath)}。为避免覆盖或创建改名副本，已跳过该文件。");

        var target = destinationPath;
        var temporary = target + $".{Guid.NewGuid():N}.partial";
        const int bufferSize = 128 * 1024;
        try
        {
            await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var destinationStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[bufferSize];
                int read;
                while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    onBytesCopied(read);
                }
                await destinationStream.FlushAsync(cancellationToken);
                destinationStream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private void ReportTransfer(string status, long completed, long total)
    {
        // 后台线程只写原子字段，UI 刷新统一交给计时器。
        // 之前每 128KB 就 Dispatcher.InvokeAsync 一次，10 盘并行时会产生上万次 UI 投递，界面直接卡死。
        Volatile.Write(ref _progressStatus, status);
        Interlocked.Exchange(ref _progressCompleted, completed);
        Interlocked.Exchange(ref _progressTotal, total);
    }

    private string _progressStatus = string.Empty;
    private long _progressCompleted;
    private long _progressTotal;

    private void RefreshTransferProgress()
    {
        var completed = Interlocked.Read(ref _progressCompleted);
        var total = Interlocked.Read(ref _progressTotal);
        if (total <= 0) return;
        var status = Volatile.Read(ref _progressStatus);
        if (!string.IsNullOrEmpty(status)) TransferStatus.Text = status;
        TransferProgress.Value = Math.Min(100, 100d * completed / total);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _driveRefreshTimer.Stop();
        _taskTimer.Stop();
        _progressTimer.Stop();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
    }

    private sealed record ScanResult(IReadOnlyList<string> Roots, IReadOnlyList<ScannedDirectory> Directories, IReadOnlyList<ScannedFile> Files);
    private sealed record ScannedDirectory(string DestinationRelativePath);
    private sealed record ScannedFile(string FullPath, string DestinationRelativePath, long SizeBytes);
    private sealed record TransferOutcome(int CompletedDevices, IReadOnlyList<string> Failures, IReadOnlyList<string> SkippedItems);
}
