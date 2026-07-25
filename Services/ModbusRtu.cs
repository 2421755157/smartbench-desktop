namespace SmartBench.Services;

public static class ModbusRtu
{
    public static byte[] ReadHoldingRegisters(byte station, ushort startAddress, ushort quantity)
    {
        ValidateReadRange(startAddress, quantity);
        return Build(station, 0x03, startAddress, quantity);
    }

    public static byte[] ReadInputRegisters(byte station, ushort startAddress, ushort quantity)
    {
        ValidateReadRange(startAddress, quantity);
        return Build(station, 0x04, startAddress, quantity);
    }

    public static byte[] WriteSingleRegister(byte station, ushort address, ushort value) =>
        Build(station, 0x06, address, value);

    public static byte[] WriteMultipleRegisters(byte station, ushort address, IReadOnlyList<ushort> values)
    {
        if (values.Count is < 1 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "一次写入数量必须在 1 到 123 个寄存器之间。");
        if ((uint)address + values.Count > 65_536)
            throw new ArgumentOutOfRangeException(nameof(address), "起始地址和写入数量超出 Modbus 地址范围。");

        var frame = new byte[9 + values.Count * 2];
        frame[0] = station;
        frame[1] = 0x10;
        frame[2] = (byte)(address >> 8);
        frame[3] = (byte)address;
        frame[4] = (byte)(values.Count >> 8);
        frame[5] = (byte)values.Count;
        frame[6] = (byte)(values.Count * 2);
        for (var index = 0; index < values.Count; index++)
        {
            frame[7 + index * 2] = (byte)(values[index] >> 8);
            frame[8 + index * 2] = (byte)values[index];
        }

        AppendCrc(frame);
        return frame;
    }

    public static ushort Crc16(ReadOnlySpan<byte> buffer)
    {
        ushort crc = 0xFFFF;
        foreach (var value in buffer)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 1 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }

        return crc;
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        var received = (ushort)(frame[^2] | frame[^1] << 8);
        return Crc16(frame[..^2]) == received;
    }

    public static ushort[] ParseRegisterResponse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5 || frame[1] is not 0x03 and not 0x04)
            throw new FormatException("不是读取寄存器的响应帧。");

        var byteCount = frame[2];
        if (byteCount % 2 != 0 || frame.Length != byteCount + 5)
            throw new FormatException("寄存器响应帧长度无效。");
        if (!HasValidCrc(frame))
            throw new FormatException("寄存器响应 CRC 校验失败。");

        var values = new ushort[byteCount / 2];
        for (var index = 0; index < values.Length; index++)
            values[index] = (ushort)(frame[3 + index * 2] << 8 | frame[4 + index * 2]);
        return values;
    }

    public static string DescribeException(byte exceptionCode) => exceptionCode switch
    {
        0x01 => "非法功能码 (01)",
        0x02 => "非法数据地址 (02)",
        0x03 => "非法数据值 (03)",
        0x04 => "从站设备故障 (04)",
        0x05 => "确认响应 (05)",
        0x06 => "从站忙 (06)",
        0x0A => "网关路径不可用 (0A)",
        0x0B => "网关目标设备无响应 (0B)",
        _ => $"设备异常 (0x{exceptionCode:X2})"
    };

    private static byte[] Build(byte station, byte function, ushort address, ushort data)
    {
        var frame = new byte[8];
        frame[0] = station;
        frame[1] = function;
        frame[2] = (byte)(address >> 8);
        frame[3] = (byte)address;
        frame[4] = (byte)(data >> 8);
        frame[5] = (byte)data;
        AppendCrc(frame);
        return frame;
    }

    private static void ValidateReadRange(ushort address, ushort quantity)
    {
        if (quantity is < 1 or > 125)
            throw new ArgumentOutOfRangeException(nameof(quantity), "读取数量必须在 1 到 125 个寄存器之间。");
        if ((uint)address + quantity > 65_536)
            throw new ArgumentOutOfRangeException(nameof(address), "起始地址和读取数量超出 Modbus 地址范围。");
    }

    private static void AppendCrc(byte[] frame)
    {
        var crc = Crc16(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
    }
}
