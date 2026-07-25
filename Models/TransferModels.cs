using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SmartBench.Models;

public sealed class UsbDriveItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string RootPath { get; init; }
    public required string Identity { get; init; }
    public required string Label { get; init; }
    public required string State { get; init; }
    public required long FreeBytes { get; init; }
    public required bool IsReady { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string FreeSpaceText => IsReady ? $"{FormatSize(FreeBytes)} 可用" : "不可读取";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed class TransferFileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string DestinationRelativePath { get; init; }
    public required long SizeBytes { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string SizeText => UsbDriveItem.FormatSize(SizeBytes);
    public string DisplayPath => DestinationRelativePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class UploadNode
{
    public UploadNode(string fullPath, bool isDirectory, bool isPlaceholder = false)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        DisplayName = isPlaceholder ? "正在加载…" : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = fullPath;
    }

    public string FullPath { get; }
    public string DisplayName { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<UploadNode> Children { get; } = [];
    public string Icon => IsDirectory ? "▸" : "•";
    public string Detail => IsDirectory ? "文件夹" : GetFileDetail();

    private string GetFileDetail()
    {
        try { return UsbDriveItem.FormatSize(new FileInfo(FullPath).Length); }
        catch { return "无法读取"; }
    }
}
