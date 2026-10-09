using System.Security.Cryptography;
using System.Text;

namespace MouseTrackerTabajara.Core;

public static class SettingsIdentity
{
    public static string Hash(TrackingSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Serialize(settings)))).ToLowerInvariant();
}

public static class ArenaGeometry
{
    public static bool SameGeometry(TrackingSettings left, TrackingSettings right) =>
        left.Cx == right.Cx && left.Cy == right.Cy && left.Rx == right.Rx && left.Ry == right.Ry
        && left.DiameterCm == right.DiameterCm && SamePlatform(left.Platform, right.Platform);

    public static TrackingSettings FromCorners(TrackingSettings current, int sourceWidth, int sourceHeight, double x1, double y1, double x2, double y2)
    {
        RequireInside(x1, y1, sourceWidth, sourceHeight);
        RequireInside(x2, y2, sourceWidth, sourceHeight);
        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        var top = Math.Min(y1, y2);
        var bottom = Math.Max(y1, y2);
        if (right - left < 1 || bottom - top < 1) throw new ArgumentException("Pool corners are too close.");
        var edited = current with
        {
            Cx = (left + right) / (2 * sourceWidth),
            Cy = (top + bottom) / (2 * sourceHeight),
            Rx = (right - left) / (2 * sourceWidth),
            Ry = (bottom - top) / (2 * sourceHeight)
        };
        return MergeEdit(current, edited);
    }

    public static TrackingSettings WithPlatform(TrackingSettings current, int sourceWidth, int sourceHeight, double centerX, double centerY, double diameterPixels)
    {
        RequireInside(centerX, centerY, sourceWidth, sourceHeight);
        if (!double.IsFinite(diameterPixels) || diameterPixels <= 0) throw new ArgumentException("Platform diameter must be a positive pixel length.");
        var edited = current with { Platform = new PlatformSite(centerX / sourceWidth, centerY / sourceHeight, diameterPixels / (2 * sourceWidth)) };
        return MergeEdit(current, edited);
    }

    public static TrackingSettings WithPoolDiameter(TrackingSettings current, double? diameterCm)
    {
        var edited = current with { DiameterCm = diameterCm };
        return MergeEdit(current, edited);
    }

    public static TrackingSettings Confirm(TrackingSettings current)
    {
        var confirmed = current with { ArenaConfirmed = true };
        confirmed.Validate();
        return confirmed;
    }

    /// <summary>A geometry-number change clears confirmation. An unchanged ellipse can be confirmed on its own.</summary>
    public static TrackingSettings MergeEdit(TrackingSettings previous, TrackingSettings edited)
    {
        edited.Validate();
        if (SameGeometry(previous, edited)) return edited;
        var cleared = edited with { ArenaConfirmed = false };
        cleared.Validate();
        return cleared;
    }

    private static void RequireInside(double x, double y, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= sourceWidth || y >= sourceHeight)
            throw new ArgumentException("Click is outside the source image.");
    }

    private static bool SamePlatform(PlatformSite? left, PlatformSite? right) =>
        left is null ? right is null : right is not null && left.X == right.X && left.Y == right.Y && left.Radius == right.Radius;

    /// <summary>
    /// Metric site test, unchanged: normalized by the pool axes and compared with site.Radius/Rx.
    /// The source region is the ellipse from <see cref="SourceEllipse"/>, not a circle.
    /// </summary>
    public static bool InsidePlatformSite(double x, double y, TrackingSettings settings, int sourceWidth, int sourceHeight)
    {
        if (settings.Platform is not { } site) return false;
        var rx = settings.Rx * sourceWidth;
        var ry = settings.Ry * sourceHeight;
        var dx = (x - site.X * sourceWidth) / rx;
        var dy = (y - site.Y * sourceHeight) / ry;
        return Math.Sqrt(dx * dx + dy * dy) <= site.Radius * sourceWidth / rx;
    }

    public static PlatformSiteEllipse SourceEllipse(PlatformSite site, TrackingSettings settings, int sourceWidth, int sourceHeight)
    {
        var horizontal = site.Radius * sourceWidth;
        var vertical = horizontal * (settings.Ry * sourceHeight) / (settings.Rx * sourceWidth);
        var centerX = site.X * sourceWidth;
        var centerY = site.Y * sourceHeight;
        return new(centerX - horizontal, centerY - vertical, horizontal * 2, vertical * 2, horizontal, vertical);
    }
}

public readonly record struct PlatformSiteEllipse(double Left, double Top, double Width, double Height, double HorizontalRadius, double VerticalRadius)
{
    public double CenterX => Left + HorizontalRadius;
    public double CenterY => Top + VerticalRadius;

    public bool Contains(double x, double y)
    {
        if (HorizontalRadius <= 0 || VerticalRadius <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        var dx = (x - CenterX) / HorizontalRadius;
        var dy = (y - CenterY) / VerticalRadius;
        return dx * dx + dy * dy <= 1;
    }
}
