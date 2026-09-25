using System.Text;
using System.Text.RegularExpressions;

namespace SoftwareUpdateTracker.WinGet.Matching;

// Compares app names loosely: case, brackets, versions, architecture and punctuation don't count.
public static partial class NameKey
{
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|[™®©]|\b(?:x64|x86|x86_64|amd64|arm64|64-bit|32-bit)\b|\bv?\d+(?:[._-]\d+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "Python 3.12.5 (64-bit)" and "Python 3.12" both become "python".
    public static string Of(string? name)
    {
        var text = Noise().Replace(name ?? "", " ");
        var key = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
        return key.ToString();
    }

    // The same cleanup kept readable for a catalog search: "Python 3.12.5 (64-bit)" becomes "Python".
    public static string SearchTerm(string? name) => Spaces().Replace(Noise().Replace(name ?? "", " "), " ").Trim(' ', '-', ',', '.');
}
