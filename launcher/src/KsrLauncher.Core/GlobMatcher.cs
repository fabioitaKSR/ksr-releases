using System.Text;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public static class GlobMatcher
{
    public static bool Any(string path, IEnumerable<string> patterns) => patterns.Any(pattern => IsMatch(path, pattern));

    public static bool IsMatch(string path, string pattern)
    {
        path = SafePaths.ManifestPath(path);
        pattern = SafePaths.ManifestPath(pattern);
        var regex = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                regex.Append(".*");
                index++;
            }
            else if (character == '*') regex.Append("[^/]*");
            else if (character == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(character.ToString()));
        }
        regex.Append('$');
        return Regex.IsMatch(path, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
