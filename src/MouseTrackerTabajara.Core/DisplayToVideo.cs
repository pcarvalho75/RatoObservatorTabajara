namespace MouseTrackerTabajara.Core;

/// <summary>
/// Maps a WPF Uniform viewer back to source pixels.
/// Layout clicks are device-independent pixels (96 per inch). Physical pixels
/// are converted with the supplied DPI before the same letterbox math runs.
/// </summary>
public static class DisplayToVideo
{
    public const double DipsPerInch = 96;

    public readonly record struct VideoPoint(double X, double Y);

    public readonly record struct Placement(double OffsetX, double OffsetY, double Width, double Height, double Scale);

    public static Placement Place(double viewportWidth, double viewportHeight, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) || viewportWidth <= 0 || viewportHeight <= 0)
            throw new ArgumentException("Viewport size must be positive.");
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentException("Source dimensions must be positive.");
        var scale = Math.Min(viewportWidth / sourceWidth, viewportHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new((viewportWidth - width) / 2, (viewportHeight - height) / 2, width, height, scale);
    }

    public static VideoPoint? MapDips(double xDip, double yDip, double viewportWidthDips, double viewportHeightDips, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(xDip) || !double.IsFinite(yDip)) return null;
        var place = Place(viewportWidthDips, viewportHeightDips, sourceWidth, sourceHeight);
        // Half-open image bounds: the far edge is the first letterbox pixel, not a source pixel.
        if (xDip < place.OffsetX || yDip < place.OffsetY || xDip >= place.OffsetX + place.Width || yDip >= place.OffsetY + place.Height)
            return null;
        var x = (xDip - place.OffsetX) / place.Scale;
        var y = (yDip - place.OffsetY) / place.Scale;
        if (x < 0 || y < 0 || x >= sourceWidth || y >= sourceHeight) return null;
        return new(x, y);
    }

    public static VideoPoint? MapDevicePixels(double xPixels, double yPixels, double viewportWidthPixels, double viewportHeightPixels, double dpi, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(dpi) || dpi <= 0) throw new ArgumentException("DPI must be positive.");
        var toDip = DipsPerInch / dpi;
        return MapDips(xPixels * toDip, yPixels * toDip, viewportWidthPixels * toDip, viewportHeightPixels * toDip, sourceWidth, sourceHeight);
    }
}
