using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using SmartBench.Models;

namespace SmartBench.Services;

/// <summary>
/// 任务日志。后台线程通过 <see cref="Write"/> 写入无锁队列，
/// 由界面线程上的定时器批量搬到可绑定集合，避免跨线程操作界面元素。
/// </summary>
public sealed class TaskLog
{
    private const int MaximumEntries = 800;
    private const int MaximumBatchSize = 200;

    private readonly ConcurrentQueue<TaskLogEntry> _pending = new();
    private readonly ObservableCollection<TaskLogEntry> _entries = [];
    private readonly DispatcherTimer _timer;

    public TaskLog()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public IReadOnlyList<TaskLogEntry> Entries => _entries;

    /// <summary>可安全地从任意线程调用。</summary>
    public void Write(TaskLogLevel level, string message, string detail = "") =>
        _pending.Enqueue(new TaskLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
            Detail = detail
        });

    public void Info(string message, string detail = "") => Write(TaskLogLevel.Info, message, detail);

    public void Success(string message, string detail = "") => Write(TaskLogLevel.Success, message, detail);

    public void Warning(string message, string detail = "") => Write(TaskLogLevel.Warning, message, detail);

    public void Error(string message, string detail = "") => Write(TaskLogLevel.Error, message, detail);

    /// <summary>有新增条目时触发，用于刷新界面上的统计文本。</summary>
    public event Action? EntriesChanged;

    /// <summary>把待写队列刷进界面集合，并裁剪到上限。</summary>
    public void Flush()
    {
        var added = 0;
        while (added < MaximumBatchSize && _pending.TryDequeue(out var entry))
        {
            _entries.Add(entry);
            added++;
        }

        if (added == 0) return;

        if (_entries.Count > MaximumEntries)
        {
            var excess = _entries.Count - MaximumEntries;
            for (var index = 0; index < excess; index++) _entries.RemoveAt(0);
        }

        EntriesChanged?.Invoke();
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        _entries.Clear();
    }

    public void Dispose() => _timer.Stop();
}
