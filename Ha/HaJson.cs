using System.Globalization;
using System.Text.Json;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// Lenient readers for the optional fields of a client command. Everything returns null or a
/// default rather than throwing, so a malformed field becomes a bad_request instead of a 500.
/// </summary>
public static class HaJson
{
    public static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    public static int? Int(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int value)
            ? value
            : null;

    public static double? Double(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetDouble(out double value)
            ? value
            : null;

    public static bool? Bool(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    /// <summary>A value that may arrive as a JSON number or as the string an mDNS TXT record holds.</summary>
    public static int? IntOrParsedString(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number))
            return number;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;

        return null;
    }

    /// <summary>Missing, null or non-array yields an empty list rather than an error.</summary>
    public static List<string> StringArray(JsonElement data, string name)
    {
        var values = new List<string>();
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;

            string? value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value!.Trim());
        }

        return values;
    }
}
