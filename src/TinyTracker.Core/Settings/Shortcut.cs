namespace TinyTracker.Core.Settings;

// Same values as RegisterHotKey's MOD_* flags.
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

// A virtual-key code plus modifiers.
public sealed record Shortcut(ShortcutModifiers Modifiers, int Key)
{
    private const ShortcutModifiers Known = ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Windows;

    public static Shortcut Default { get; } = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x55);

    // A key without a modifier would take over normal typing.
    public bool IsValid() => Key is >= 1 and <= 254 && Modifiers != ShortcutModifiers.None && (Modifiers & ~Known) == 0;
}
