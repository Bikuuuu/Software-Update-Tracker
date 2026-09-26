using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyTracker.Presentation;
using Windows.Storage.Streams;

namespace TinyTracker.App;

// Small functions for x:Bind, and the hamster logo.
public static class Ui
{
    private static SvgImageSource? s_logo;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Both(bool first, bool second) => Visible(first && second);

    public static string Chevron(bool expanded) => expanded ? "\uE70E" : "\uE70D";

    public static InfoBarSeverity Severity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Error => InfoBarSeverity.Error,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    // Loaded once, from the SVG next to the exe.
    public static async Task<SvgImageSource> LogoAsync()
    {
        if (s_logo is not null) return s_logo;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "hamster-small.svg"));
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var svg = new SvgImageSource();
        await svg.SetSourceAsync(stream);
        return s_logo = svg;
    }
}
