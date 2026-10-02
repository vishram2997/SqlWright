using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlWright.DynamicQuery.Internal;

/// <summary>
/// Opaque keyset-pagination token: the sort-key values of the last row returned, tagged with their types,
/// plus a fingerprint of the sort so a cursor can't be replayed against a different ordering.
/// The values only ever become SQL parameters, so a tampered cursor can at worst return a different page.
/// </summary>
internal static class Cursor
{
    public static string Fingerprint(string sortDescription)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sortDescription));
        return Convert.ToHexString(hash, 0, 8);
    }

    public static string Encode(string fingerprint, IReadOnlyList<object?> values)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("f", fingerprint);
            w.WriteStartArray("v");
            foreach (var value in values) WriteValue(w, value);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Base64Url(stream.ToArray());
    }

    public static bool TryDecode(string token, out string fingerprint, out object?[] values)
    {
        fingerprint = "";
        values = [];
        try
        {
            using var doc = JsonDocument.Parse(FromBase64Url(token));
            fingerprint = doc.RootElement.GetProperty("f").GetString() ?? "";
            values = doc.RootElement.GetProperty("v").EnumerateArray().Select(ReadValue).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        w.WriteStartArray();
        switch (value)
        {
            case null or DBNull: w.WriteStringValue("n"); w.WriteNullValue(); break;
            case bool b: w.WriteStringValue("b"); w.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int or uint or long:
                w.WriteStringValue("i"); w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case decimal m: w.WriteStringValue("m"); w.WriteStringValue(m.ToString(CultureInfo.InvariantCulture)); break;
            case double or float: w.WriteStringValue("f"); w.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
            case DateTime dt: w.WriteStringValue("t"); w.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: w.WriteStringValue("o"); w.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateOnly d: w.WriteStringValue("d"); w.WriteStringValue(d.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeOnly t: w.WriteStringValue("h"); w.WriteStringValue(t.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan ts: w.WriteStringValue("p"); w.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture)); break;
            case Guid g: w.WriteStringValue("g"); w.WriteStringValue(g); break;
            case byte[] bytes: w.WriteStringValue("x"); w.WriteBase64StringValue(bytes); break;
            default: w.WriteStringValue("s"); w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
        w.WriteEndArray();
    }

    private static object? ReadValue(JsonElement pair)
    {
        var tag = pair[0].GetString();
        var v = pair[1];
        return tag switch
        {
            "n" => null,
            "b" => v.GetBoolean(),
            "i" => v.GetInt64(),
            "m" => decimal.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "f" => v.GetDouble(),
            "t" => DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "o" => DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "d" => DateOnly.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "h" => TimeOnly.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "p" => TimeSpan.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "g" => v.GetGuid(),
            "x" => v.GetBytesFromBase64(),
            "s" => v.GetString(),
            _ => throw new FormatException($"Unknown cursor value tag '{tag}'."),
        };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        var s = token.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Bad cursor length.") };
        return Convert.FromBase64String(s);
    }
}
