using System.Globalization;

namespace SmartBench.Services;

public static class HexCodec
{
    public static byte[] Parse(string input)
    {
        var normalized = input.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        var tokens = normalized.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Array.Empty<byte>();

        var output = new byte[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!byte.TryParse(tokens[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output[index]))
                throw new FormatException($"“{tokens[index]}”不是有效的十六进制字节。");
        }
        return output;
    }

    public static string Format(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return "(空)";
        return Convert.ToHexString(data).Chunk(2).Select(chunk => new string(chunk)).Aggregate((left, right) => $"{left} {right}");
    }
}
