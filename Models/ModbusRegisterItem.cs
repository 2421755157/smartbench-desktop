using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartBench.Models;

public sealed class ModbusRegisterItem : INotifyPropertyChanged
{
    private string _alias = string.Empty;
    private ushort _value;
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
        set { _value = value; OnPropertyChanged(); OnPropertyChanged(nameof(HexValue)); }
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
}
