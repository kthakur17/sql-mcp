using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SqlMcp.Safety;

internal static class GlobPattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string pattern, string value)
    {
        var regex = Cache.GetOrAdd(pattern, static p =>
        {
            var body = Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
            return new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        });
        return regex.IsMatch(value);
    }
}
