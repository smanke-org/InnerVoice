// The Mac's JSON helper (Agent/AgentCore.swift): sorted keys, slashes not escaped, fractions
// in their shortest form. Values are plain .NET objects: JObject (string → value), lists,
// strings, bools, whole numbers (long/int), fractions (double) and null.

global using JObject = System.Collections.Generic.Dictionary<string, object?>;

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InnerVoice.Agent;

public static class Json
{
    public static string Write(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Quote(sb, s); break;
            case char c: Quote(sb, c.ToString()); break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case double or float or decimal:
                sb.Append(Number(Convert.ToDouble(value, CultureInfo.InvariantCulture))); break;
            case IDictionary<string, object?> map:
                sb.Append('{');
                bool first = true;
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Quote(sb, key);
                    sb.Append(':');
                    Write(sb, map[key]);
                }
                sb.Append('}');
                break;
            case IEnumerable list:
                sb.Append('[');
                bool firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            default: Quote(sb, value.ToString() ?? ""); break;
        }
    }

    /// 0.5, not 0.50000000000000011; 1 for 1.0, as the Mac writes it.
    static string Number(double d) => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null";

    static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// The value, or null for anything that isn't JSON.
    public static object? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return From(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    static object? From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().GroupBy(p => p.Name).ToDictionary(g => g.Key, g => From(g.Last().Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(From).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// A fraction rounded to hundredths, as the Mac's `(x * 100).rounded() / 100`.
    public static double Hundredths(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// ISO 8601 in UTC, as the Mac's `Date.formatted(.iso8601)`: 2026-11-13T12:00:00Z.
    public static string Date(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
