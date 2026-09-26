using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyTracker.Presentation.Icons;

// Where an installed app's icon comes from, read from winget's local id. The app loads it into memory only.
public abstract partial record IconSource
{
    // ARP\<Machine|User>\<architecture>\<uninstall key>, or MSIX\<package full name>.
    public static IconSource? Of(string? localId)
    {
        var parts = (localId ?? "").Split('\\', 4);
        if (parts is ["MSIX" or "msix", { Length: > 0 } fullName]) return new PackagedIcon(fullName);
        if (parts is not [var arp, var scope, var architecture, { Length: > 0 } key] || !arp.Equals("ARP", StringComparison.OrdinalIgnoreCase)) return null;
        var user = scope.Equals("User", StringComparison.OrdinalIgnoreCase);
        if (!user && !scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)) return null;
        return new UninstallIcon(user, !user && architecture.Equals("X86", StringComparison.OrdinalIgnoreCase), key);
    }

    // An uninstall entry's DisplayIcon: a path, maybe quoted, maybe followed by ",index".
    public static (string Path, int Index)? DisplayIcon(string? value)
    {
        var text = value?.Trim() ?? "";
        var index = 0;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            var rest = text[(end + 1)..].Trim();
            text = text[1..end];
            if (rest.StartsWith(',') && int.TryParse(rest[1..], NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite, CultureInfo.InvariantCulture, out var quoted)) index = quoted;
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..], NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var plain))
            {
                index = plain;
                text = text[..comma];
            }
        }
        text = text.Trim();
        return text.Length == 0 ? null : (text, index);
    }

    // The app's own program in its install folder: the only one left once installers and helpers are set aside,
    // or the only one whose name is part of the app's name.
    public static string? MainProgram(IEnumerable<string> fileNames, string appName)
    {
        var programs = fileNames.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Helper().IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();
        if (programs.Count == 1) return programs[0];
        var name = Squash(appName);
        var named = programs.Where(f => Squash(Path.GetFileNameWithoutExtension(f)) is { Length: > 2 } file && name.Contains(file, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    // Makes 32-bit icon pixels premultiplied BGRA. Old icons have no alpha, so their AND mask (black shows) decides.
    public static void Premultiply(Span<byte> bgra, ReadOnlySpan<byte> mask)
    {
        var hasAlpha = false;
        for (var i = 3; i < bgra.Length && !hasAlpha; i += 4) hasAlpha = bgra[i] != 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = hasAlpha ? bgra[i + 3] : mask.Length > i && mask[i] != 0 ? (byte)0 : (byte)255;
            bgra[i] = (byte)(bgra[i] * alpha / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * alpha / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * alpha / 255);
            bgra[i + 3] = alpha;
        }
    }

    [GeneratedRegex("unins|setup|install|update|crash|report|helper|elevat|service|notif", RegexOptions.IgnoreCase)]
    private static partial Regex Helper();

    private static string Squash(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

// A Windows uninstall entry: in HKCU for per-user apps, else in HKLM, in its 32-bit view for x86 apps.
public sealed record UninstallIcon(bool PerUser, bool Wow64, string KeyName) : IconSource
{
    public string KeyPath => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + KeyName;
}

// An MSIX package, by its full name.
public sealed record PackagedIcon(string FullName) : IconSource;
