namespace SoftwareUpdateTracker.Core.Settings;

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
    public static Shortcut Default { get; } = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x55);
}
