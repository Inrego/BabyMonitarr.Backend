namespace BabyMonitarr.Backend.Talkback;

/// <summary>
/// Keeps the stored Google cookie alive: Google rotates some of its session cookies on every
/// issueToken exchange, and a credential that is not updated with them expires.
/// </summary>
public static class GoogleHomeCookies
{
    /// <summary>
    /// Applies <c>Set-Cookie</c> headers to a <c>Cookie</c> request header value. A cookie is
    /// replaced in place, added at the end when new, and removed when the server expires it.
    /// </summary>
    public static string Merge(string cookieHeader, IEnumerable<string> setCookieHeaders)
    {
        var cookies = Parse(cookieHeader);

        foreach (var header in setCookieHeaders)
        {
            var parts = header.Split(';');
            int eq = parts[0].IndexOf('=');
            if (eq <= 0) continue;

            string name = parts[0][..eq].Trim();
            string value = parts[0][(eq + 1)..].Trim();
            int index = cookies.FindIndex(c => c.Name == name);

            if (IsExpired(parts.Skip(1)))
            {
                if (index >= 0) cookies.RemoveAt(index);
            }
            else if (index >= 0)
            {
                cookies[index] = (name, value);
            }
            else
            {
                cookies.Add((name, value));
            }
        }

        return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>Names only, for diagnostics: cookie values are secrets.</summary>
    public static IReadOnlyList<string> Names(IEnumerable<string> setCookieHeaders) =>
        setCookieHeaders.Select(h => h.Split('=', 2)[0].Trim()).Where(n => n.Length > 0).ToList();

    private static List<(string Name, string Value)> Parse(string cookieHeader) =>
        cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv[0].Length > 0)
            .Select(kv => (kv[0], kv.Length > 1 ? kv[1] : string.Empty))
            .ToList();

    private static bool IsExpired(IEnumerable<string> attributes)
    {
        foreach (var attribute in attributes)
        {
            var kv = attribute.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            if (kv[0].Equals("Max-Age", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(kv[1], out long maxAge) && maxAge <= 0)
            {
                return true;
            }

            if (kv[0].Equals("Expires", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(kv[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var expires) &&
                expires <= DateTimeOffset.UtcNow)
            {
                return true;
            }
        }

        return false;
    }
}
