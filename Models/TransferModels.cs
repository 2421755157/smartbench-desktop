using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace SmartBench.Models;

public sealed class UsbDriveItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _identity = string.Empty;
    private string _label = string.Empty;
    private string _state = string.Empty;
    private long _freeBytes;
    private bool _isReady;

    public required string RootPath { get; init; }

    /// <summary>卷序列号等设备身份，用于下发前的二次核验。</summary>
    public string Identity
    {
        get => _identity;
        set => SetField(ref _identity, value);
    }

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public long FreeBytes
    {
        get => _freeBytes;
        set
        {
            if (SetField(ref _freeBytes, value)) OnPropertyChanged(nameof(FreeSpaceText));
        }
    }

    public bool IsReady
    {
        get => _isReady;
        set
        {
            if (SetField(ref _isReady, value)) OnPropertyChanged(nameof(FreeSpaceText));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string FreeSpaceText => IsReady ? $"{FormatSize(FreeBytes)} 可用" : "不可读取";

    /// <summary>把最新探测结果合并到已有条目，避免整表重建导致勾选与滚动位置丢失。</summary>
    public void ApplyFrom(UsbDriveItem other)
    {
        Identity = other.Identity;
        Label = other.Label;
        State = other.State;
        FreeBytes = other.FreeBytes;
        IsReady = other.IsReady;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

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

public sealed class ModbusRegisterItem : INotifyPropertyChanged
{
    private string _alias = string.Empty;
    private ushort _value;
    private string _valueText = "0";
    private string _state = "已读取";
    private string _updatedAt = "—";

    public required byte Station { get; init; }
    public required ushort Address { get; init; }
    public required string DeviceAddress { get; init; }

    public string Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(); }
    }

    public ushort Value
    {
        get => _value;
        set
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (_value == value && _valueText == text) return;
            _value = value;
            _valueText = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexValue));
            OnPropertyChanged(nameof(ValueText));
        }
    }

    /// <summary>表格中直接编辑的文本，支持十进制与 0x 十六进制。</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (TryParseRegisterValue(value, out var parsed))
            {
                _value = parsed;
                _valueText = value.Trim();
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(HexValue));
            }
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public string HexValue => $"0x{Value:X4}";

    public string State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); }
    }

    public string UpdatedAt
    {
        get => _updatedAt;
        set { _updatedAt = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool TryParseRegisterValue(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        // 只有显式写 0x 才按十六进制解析，避免把打错的字母静默当成十六进制数值。
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
            return trimmed.Length > 0
                && ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
