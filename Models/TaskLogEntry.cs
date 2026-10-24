using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartBench.Models;

public enum TaskLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 任务日志条目。用于 U 盘批量下发、批量格式化等长任务的现场留痕。
/// </summary>
public sealed class TaskLogEntry : INotifyPropertyChanged
{
    private string _detail = string.Empty;

    public required DateTime Timestamp { get; init; }
    public required TaskLogLevel Level { get; init; }
    public required string Message { get; init; }

    /// <summary>补充信息，例如具体文件路径、失败原因。</summary>
    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        TaskLogLevel.Success => "成功",
        TaskLogLevel.Warning => "警告",
        TaskLogLevel.Error => "失败",
        _ => "信息"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
