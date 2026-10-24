using System.Text;

namespace SmartBench.Services;

/// <summary>
/// 线程安全的日志缓冲。串口 / Modbus / UDP 的收发回调运行在后台线程，
/// 直接高频操作 TextBox 会把 UI 消息队列打爆，因此先在内存里拼接，再由界面定时器批量刷新。
/// </summary>
public sealed class LogBuffer
{
    private readonly StringBuilder _builder = new();
    private readonly object _gate = new();

    public void Append(string text)
    {
        lock (_gate) _builder.Append(text);
    }

    public string Take()
    {
        lock (_gate)
        {
            if (_builder.Length == 0) return string.Empty;
            var text = _builder.ToString();
            _builder.Clear();
            return text;
        }
    }

    public void Clear()
    {
        lock (_gate) _builder.Clear();
    }
}
