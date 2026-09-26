using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftwareUpdateTracker.Presentation;

namespace SoftwareUpdateTracker.App.Controls;

// A notice from a page's list, as a banner with a Details button when it has a code. It shows its DataContext.
public sealed partial class NoticeBar : InfoBar
{
    public NoticeBar()
    {
        IsOpen = true;
        DataContextChanged += (_, _) => Show();
    }

    private void Show()
    {
        if (DataContext is not Notice notice) return;
        Title = notice.Title;
        Message = notice.Message;
        Severity = Ui.Severity(notice.Severity);
        IsClosable = notice.Closable;
        Content = notice.Details is { } details ? DetailsButton(details) : null;
    }

    private static Button DetailsButton(string details) => new()
    {
        Content = Strings.Details,
        Style = (Style)Application.Current.Resources["LinkButton"],
        Margin = new Thickness(0, 0, 0, 8),
        Flyout = new Flyout { Content = new TextBlock { Text = details, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } },
    };
}
