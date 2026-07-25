using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    public ObservableCollection<UsbDriveItem> Drives { get; } = [];
    public ObservableCollection<TransferFileItem> TransferFiles { get; } = [];
    public ObservableCollection<UploadNode> UploadItems { get; } = [];

    private readonly DispatcherTimer _driveRefreshTimer;
    private readonly HashSet<string> _uploadedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transferFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transferDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScannedDirectory> _transferDirectories = [];
    private CancellationTokenSource? _transferCancellation;
    private bool _isTransferring;
    private bool _disposed;

    public UsbTransferView()
    {
        InitializeComponent();
        DataContext = this;
        RefreshDrives();
        _driveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _driveRefreshTimer.Tick += (_, _) => { if (!_isTransferring) RefreshDrives(); };
        _driveRefreshTimer.Start();
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        var selectedRoots = Drives.Where(drive => drive.IsSelected).Select(drive => drive.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    IsReady = true,
                    IsSelected = selectedRoots.Contains(drive.Name)
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

        Drives.Clear();
        foreach (var device in discovered) Drives.Add(device);
        DriveSummary.Text = discovered.Count switch
        {
            0 => "未发现可用的可移动设备",
            >= 10 => "已显示前 10 个可移动设备",
            _ => $"已识别 {discovered.Count} 个可移动设备"
        };
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
        foreach (var drive in Drives.Where(drive => drive.IsReady)) drive.IsSelected = true;
    }

    private void ClearDriveSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var drive in Drives) drive.IsSelected = false;
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

        TransferSummary.Text = "正在读取文件与目录结构…";
        var scan = await Task.Run(() => ScanUploadPaths(paths));
        var addedRoots = 0;
        var addedFiles = 0;
        var addedDirectories = 0;

        foreach (var root in scan.Roots)
        {
            if (!_uploadedRoots.Add(root)) continue;
            UploadItems.Add(CreateNode(root));
            addedRoots++;
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
        if (addedFiles > 0 || addedDirectories > 0)
            TransferStatus.Text = $"已添加 {addedFiles} 个文件、{addedDirectories} 个文件夹；可在“待下发文件”中调整选择。";
        else if (addedRoots > 0)
            TransferStatus.Text = "已添加内容，但没有可读取的文件或文件夹。";
    }

    private static ScanResult ScanUploadPaths(IEnumerable<string> paths)
    {
        var roots = new List<string>();
        var directories = new List<ScannedDirectory>();
        var files = new List<ScannedFile>();
        var options = new EnumerationOptions
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

    private static UploadNode CreateNode(string path)
    {
        var node = new UploadNode(path, Directory.Exists(path));
        if (node.IsDirectory && HasChildren(path)) node.Children.Add(new UploadNode(path, false, true));
        return node;
    }

    private static bool HasChildren(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Take(1).Any(); }
        catch { return false; }
    }

    private void UploadTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.DataContext is not UploadNode node || !node.IsDirectory) return;
        if (node.Children.Count != 1 || !node.Children[0].IsPlaceholder) return;

        node.Children.Clear();
        try
        {
            var directories = Directory.EnumerateDirectories(node.FullPath).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);
            var files = Directory.EnumerateFiles(node.FullPath).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);
            foreach (var directory in directories) node.Children.Add(CreateNode(directory));
            foreach (var file in files) node.Children.Add(CreateNode(file));
        }
        catch (UnauthorizedAccessException) { node.Children.Add(new UploadNode("无法访问此文件夹", false)); }
        catch (IOException) { node.Children.Add(new UploadNode("无法读取此文件夹", false)); }
    }

    private void UploadTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        UploadPathText.Text = e.NewValue is UploadNode node ? node.FullPath : "选择一个内容项可查看完整路径";
    }

    private void OpenSelectedUpload_Click(object sender, RoutedEventArgs e)
    {
        if (UploadTree.SelectedItem is not UploadNode node || node.IsPlaceholder || (!File.Exists(node.FullPath) && !Directory.Exists(node.FullPath))) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法打开位置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
        UploadItems.Clear();
        TransferFiles.Clear();
        _uploadedRoots.Clear();
        _transferFilePaths.Clear();
        _transferDirectoryPaths.Clear();
        _transferDirectories.Clear();
        UploadPathText.Text = "选择一个内容项可查看完整路径";
        TransferSummary.Text = "拖入文件或文件夹，支持任意格式并保留目录层级";
        TransferStatus.Text = "已清空待下发内容";
        TransferProgress.Value = 0;
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
            : $"已载入 {TransferFiles.Count} 个文件、{_transferDirectories.Count} 个文件夹 · {UsbDriveItem.FormatSize(totalBytes)} · {UploadItems.Count} 个根项目";
    }

    private async void StartTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
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
        var copiedBytes = 0L;

        try
        {
            var outcome = await Task.Run(async () =>
            {
                var failures = new List<string>();
                var skippedItems = new List<string>();
                var completedDevices = 0;
                foreach (var target in targets)
                {
                    _transferCancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        ValidateTargetForWrite(target, bytesPerDrive);
                        ValidateSourceIsNotTarget(target);
                        var destinationRoot = target.RootPath;
                        Directory.CreateDirectory(destinationRoot);
                        var blockedFolderRoots = FindExistingFolderRoots(destinationRoot, directories);

                        foreach (var directory in directories)
                        {
                            _transferCancellation.Token.ThrowIfCancellationRequested();
                            if (IsInBlockedFolderRoot(directory.DestinationRelativePath, blockedFolderRoots)) continue;
                            Directory.CreateDirectory(BuildSafeDestinationPath(destinationRoot, directory.DestinationRelativePath));
                        }

                        foreach (var file in files)
                        {
                            _transferCancellation.Token.ThrowIfCancellationRequested();
                            if (IsInBlockedFolderRoot(file.DestinationRelativePath, blockedFolderRoots))
                            {
                                skippedItems.Add($"{target.RootPath}：文件夹“{TopLevelSegment(file.DestinationRelativePath)}”已存在，跳过其中内容。");
                                continue;
                            }
                            var destinationPath = BuildSafeDestinationPath(destinationRoot, file.DestinationRelativePath);
                            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                            {
                                skippedItems.Add($"{target.RootPath}：同名项“{file.DestinationRelativePath}”已存在，已跳过。");
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                            ReportTransfer($"正在复制到 {target.RootPath}：{file.Name}", copiedBytes, totalBytes);
                            await CopyFileSafelyAsync(file.FullPath, destinationPath, count =>
                            {
                                var completed = Interlocked.Add(ref copiedBytes, count);
                                ReportTransfer($"正在复制到 {target.RootPath}：{file.Name}", completed, totalBytes);
                            }, _transferCancellation.Token);
                        }
                        completedDevices++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        failures.Add($"{target.RootPath}：{exception.Message}");
                        ReportTransfer($"{target.RootPath} 下发失败，继续处理其他设备", copiedBytes, totalBytes);
                    }
                }
                return new TransferOutcome(completedDevices, failures, skippedItems);
            });

            TransferProgress.Value = totalBytes == 0 ? 100 : Math.Min(100, 100d * copiedBytes / totalBytes);
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
        Dispatcher.InvokeAsync(() =>
        {
            TransferStatus.Text = status;
            TransferProgress.Value = total == 0 ? 0 : Math.Min(100, 100d * completed / total);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _driveRefreshTimer.Stop();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
    }

    private sealed record ScanResult(IReadOnlyList<string> Roots, IReadOnlyList<ScannedDirectory> Directories, IReadOnlyList<ScannedFile> Files);
    private sealed record ScannedDirectory(string DestinationRelativePath);
    private sealed record ScannedFile(string FullPath, string DestinationRelativePath, long SizeBytes);
    private sealed record TransferOutcome(int CompletedDevices, IReadOnlyList<string> Failures, IReadOnlyList<string> SkippedItems);
}
