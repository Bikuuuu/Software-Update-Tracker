using TinyTracker.Core.Checking;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation;

// Same names as InfoBarSeverity.
public enum NoticeSeverity
{
    Informational,
    Warning,
    Error,
}

public enum NoticeKind
{
    WinGet,
    Check,
    SettingsRecovered,
    SettingsUnreadable,
    HistoryRecovered,
    HistoryUnreadable,
    SaveFailed,
    StartupNotChanged,
    HistoryNotCleared,
}

// A banner at the top of a page. A page shows one notice per kind.
public sealed record Notice(NoticeKind Kind, NoticeSeverity Severity, string Title, string Message = "", string? Details = null, bool Closable = false)
{
    // App Installer's Microsoft Store page, where winget is updated.
    public const string AppInstallerStoreLink = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    public bool OffersStore => Kind == NoticeKind.WinGet;

    public bool HasDetails => Details is not null;

    public static Notice? ForProblem(CheckProblem problem, string? detail) => problem switch
    {
        CheckProblem.None => null,
        CheckProblem.WinGetMissing or CheckProblem.WinGetTooOld => new(NoticeKind.WinGet, NoticeSeverity.Error, Strings.WinGetNeedsUpdate, Strings.WinGetNeedsUpdateMessage, detail),
        CheckProblem.WinGetUnreachable => new(NoticeKind.Check, NoticeSeverity.Warning, Strings.WinGetUnreachable, Details: detail),
        CheckProblem.TimedOut => new(NoticeKind.Check, NoticeSeverity.Warning, Strings.CheckTimedOut, Details: detail),
        CheckProblem.SettingsNotSaved => new(NoticeKind.Check, NoticeSeverity.Error, Strings.SettingsNotSaved, Details: detail),
        _ => new(NoticeKind.Check, NoticeSeverity.Error, Strings.CheckFailed, Details: detail),
    };

    public static Notice SettingsRecovered { get; } = new(NoticeKind.SettingsRecovered, NoticeSeverity.Warning, Strings.SettingsRecovered, Closable: true);
    public static Notice SettingsUnreadable { get; } = new(NoticeKind.SettingsUnreadable, NoticeSeverity.Error, Strings.SettingsUnreadable);
    public static Notice HistoryRecovered { get; } = new(NoticeKind.HistoryRecovered, NoticeSeverity.Warning, Strings.HistoryRecovered, Closable: true);
    public static Notice HistoryUnreadable { get; } = new(NoticeKind.HistoryUnreadable, NoticeSeverity.Error, Strings.HistoryUnreadable);
    public static Notice SaveFailed { get; } = new(NoticeKind.SaveFailed, NoticeSeverity.Error, Strings.SaveFailed, Closable: true);

    public static Notice StartupNotChanged(Exception error) =>
        new(NoticeKind.StartupNotChanged, NoticeSeverity.Error, Strings.StartupNotChanged, Details: Code(error), Closable: true);

    public static Notice HistoryNotCleared(Exception error) =>
        new(NoticeKind.HistoryNotCleared, NoticeSeverity.Error, Strings.HistoryNotCleared, Details: Code(error), Closable: true);

    private static string Code(Exception error) => Words.Format(Strings.DetailsCode, $"0x{error.HResult:X8}");
}
