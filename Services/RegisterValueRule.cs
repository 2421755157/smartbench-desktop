using System.Globalization;
using System.Windows.Controls;

namespace SmartBench.Services;

/// <summary>
/// 寄存器数值输入校验：接受十进制与 0x 前缀的十六进制，越界或非法字符时给出中文提示。
/// </summary>
public sealed class RegisterValueRule : ValidationRule
{
    public override ValidationResult Validate(object? value, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (text.Length == 0) return new ValidationResult(false, "数值不能为空。");
        if (TryParse(text, out _)) return new ValidationResult(true, null);
        return new ValidationResult(false, "需为 0 到 65535 的十进制数字，或 0x 开头的十六进制。");
    }

    public static bool TryParse(string text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // 只有显式写 0x 才按十六进制解析。
        // 否则 ushort.TryParse(trimmed, HexNumber) 会把 "abc" 悄悄当成 0xABC 接受，
        // 用户打错字母却被当成有效数值写进设备，属于危险的静默转换。
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
            return trimmed.Length > 0
                && ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
