namespace SoftwareUpdateTracker.Core.Layout;

public static class FlyoutPlacement
{
    public const double WidthDip = 380;
    public const double MarginDip = 12;
    public const double MaxHeightFraction = 0.7;

    public static PixelRect Compute(PixelRect workArea, uint dpi, double desiredHeightDip)
    {
        var scale = dpi / 96.0;
        var width = (int)Math.Ceiling(WidthDip * scale);
        var margin = (int)Math.Round(MarginDip * scale);
        var maxHeight = (int)Math.Floor(workArea.Height * MaxHeightFraction);
        var height = Math.Min((int)Math.Ceiling(desiredHeightDip * scale), maxHeight);
        return new PixelRect(workArea.Right - width - margin, workArea.Bottom - height - margin, width, height);
    }
}
