using System.Globalization;
using System.Text.RegularExpressions;
using SettleFlow.Core.Model;

namespace SettleFlow.Core.Parsing;

/// <summary>Deterministic conversion of messy cell values into typed values.</summary>
public static partial class ValueNormalizer
{
    public static readonly string[] DefaultDateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd",
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        "dd/MM/yy", "d/M/yy",
    ];

    public static string NormalizeText(object? value)
    {
        if (TabularFileReader.IsBlank(value)) return "";
        var s = value switch
        {
            double d when d == Math.Floor(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        return TabularFileReader.CollapseWhitespace(s.Trim());
    }

    /// <summary>Key form of a value: case-insensitive, whitespace- and separator-insensitive.</summary>
    public static string NormalizeKey(object? value) =>
        KeyNoise().Replace(NormalizeText(value), "").ToUpperInvariant();

    public static bool TryParseNumber(object? value, out decimal result)
    {
        result = 0;
        switch (value)
        {
            case null or DBNull: return false;
            case double d: result = (decimal)d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case decimal m: result = m; return true;
        }
        var s = NormalizeText(value);
        if (s.Length == 0) return false;

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')')) { negative = true; s = s[1..^1]; }
        if (s.EndsWith('-')) { negative = true; s = s[..^1]; }
        s = CurrencyNoise().Replace(s, "");
        if (s.StartsWith('-')) { negative = !negative; s = s[1..]; }

        // Decide decimal separator: "1.234,56" (EU) vs "1,234.56" (US/IN, incl. lakh grouping 1,23,456.00).
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma > lastDot && s.Length - lastComma - 1 is 1 or 2)
            s = s.Replace(".", "").Replace(',', '.');
        else
            s = s.Replace(",", "");

        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result)) return false;
        if (negative) result = -result;
        return true;
    }

    public static bool TryParseDate(object? value, IReadOnlyList<string> preferredFormats, out DateOnly result)
    {
        result = default;
        switch (value)
        {
            case null or DBNull: return false;
            case DateTime dt: result = DateOnly.FromDateTime(dt); return true;
            case DateOnly d: result = d; return true;
            case double serial when serial is > 1 and < 2958466:
                result = DateOnly.FromDateTime(DateTime.FromOADate(serial)); return true;
        }
        var s = NormalizeText(value);
        if (s.Length == 0) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is > 20000 and < 80000 && !s.Contains('-') && !s.Contains('/'))
        {
            result = DateOnly.FromDateTime(DateTime.FromOADate(n)); return true;
        }
        var formats = preferredFormats.Concat(DefaultDateFormats).ToArray();
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            result = DateOnly.FromDateTime(parsed); return true;
        }
        return false;
    }

    /// <summary>Converts a raw cell to the canonical representation for a field type.</summary>
    public static bool TryNormalize(object? raw, FieldType type, IReadOnlyList<string> dateFormats, out object? value)
    {
        value = null;
        if (TabularFileReader.IsBlank(raw)) return true;
        switch (type)
        {
            case FieldType.Number:
                if (!TryParseNumber(raw, out var num)) return false;
                value = num; return true;
            case FieldType.Date:
                if (!TryParseDate(raw, dateFormats, out var date)) return false;
                value = date; return true;
            default:
                value = NormalizeText(raw); return true;
        }
    }

    [GeneratedRegex(@"[\s\-_/.]")]
    private static partial Regex KeyNoise();

    [GeneratedRegex(@"[\s$€£₹¥]|INR|USD|EUR|GBP|Rs\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyNoise();
}
