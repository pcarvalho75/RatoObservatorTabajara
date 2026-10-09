// C# port of DORIS detection operations. Copyright 2017-2022 Olivier Friard.
// GPL-3.0-or-later. See LICENSE.txt and docs/SOURCE-PROVENANCE.txt.
using System.Diagnostics;
using System.Security.Cryptography;
using MouseTrackerTabajara.Core;
using OpenCvSharp;

namespace MouseTrackerTabajara.Tracking;

public sealed record Detection(int X, int Y, double Area, int MinX, int MinY, int MaxX, int MaxY);

public static class OpenCvTracker
{
    static OpenCvTracker() => Cv2.SetNumThreads(1);
    public static string NativeVersion => Cv2.GetVersionString() ?? "unavailable";
    public static string HashFile(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }

    public static VideoMetadata Probe(string path)
    {
        using var capture = Open(path);
        var metadata = new VideoMetadata((int)capture.Get(VideoCaptureProperties.FrameWidth), (int)capture.Get(VideoCaptureProperties.FrameHeight),
            capture.Get(VideoCaptureProperties.Fps), (int)capture.Get(VideoCaptureProperties.FrameCount));
        if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.FrameCount <= 0 || !double.IsFinite(metadata.Fps) || metadata.Fps <= 0)
            throw new InvalidDataException("Video geometry or frame rate is invalid.");
        return metadata;
    }

    private static VideoCapture Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Original video is missing.", path);
        var capture = new VideoCapture(path, VideoCaptureAPIs.FFMPEG);
        if (!capture.IsOpened()) { capture.Dispose(); throw new InvalidDataException("FFmpeg could not open this recording."); }
        return capture;
    }

    public static Mat ArenaMask(int width, int height, TrackingSettings s)
    {
        s.Validate();
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        var points = Enumerable.Range(0, 160).Select(i =>
        {
            var theta = i * (2 * Math.PI / 160);
            return new Point((int)Math.Round(s.Cx * width + s.Rx * width * Math.Cos(theta)),
                (int)Math.Round(s.Cy * height + s.Ry * height * Math.Sin(theta)));
        }).ToArray();
        Cv2.FillPoly(mask, new[] { points }, Scalar.White);
        // Exclusions operate on the final arena mask, so Invert cannot reverse them.
        foreach (var exclusion in s.Exclusions)
            Cv2.Rectangle(mask, new Point((int)Math.Round(exclusion.Left * width), (int)Math.Round(exclusion.Top * height)),
                new Point((int)Math.Round(exclusion.Right * width), (int)Math.Round(exclusion.Bottom * height)), Scalar.Black, -1);
        return mask;
    }

    public static Mat ProcessFrame(Mat frame, TrackingSettings s, Mat arena)
    {
        using var blurred = new Mat(); using var gray = new Mat(); using var thresholded = new Mat();
        if (s.Blur > 0) Cv2.Blur(frame, blurred, new Size(s.Blur, s.Blur)); else frame.CopyTo(blurred);
        Cv2.CvtColor(blurred, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, thresholded, s.Threshold, 255, ThresholdTypes.Binary);
        if (s.Invert) Cv2.BitwiseNot(thresholded, thresholded);
        var result = new Mat(); Cv2.BitwiseAnd(thresholded, arena, result); return result;
    }

    public static List<Detection> Detect(Mat binary, TrackingSettings s)
    {
        Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var selected = new List<Detection>();
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area < s.MinArea || area > s.MaxArea) continue;
            var minX = contour.Min(p => p.X); var minY = contour.Min(p => p.Y);
            var maxX = contour.Max(p => p.X); var maxY = contour.Max(p => p.Y);
            if (maxX - minX > s.MaxExtension || maxY - minY > s.MaxExtension) continue;
            var moments = Cv2.Moments(contour);
            var x = moments.M00 != 0 ? (int)(moments.M10 / moments.M00) : (int)contour.Average(p => p.X);
            var y = moments.M00 != 0 ? (int)(moments.M01 / moments.M00) : (int)contour.Average(p => p.Y);
            selected.Add(new(x, y, area, minX, minY, maxX, maxY));
        }
        return selected;
    }

    public static AnalysisRun Analyze(VideoAsset asset, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var timer = Stopwatch.StartNew(); var s = asset.Settings; s.Validate();
        var initialInfo = new FileInfo(asset.Path); var initialLength = initialInfo.Length; var initialWrite = initialInfo.LastWriteTimeUtc;
        var hash = HashFile(asset.Path);
        if (asset.SourceSha256.Length > 0 && hash != asset.SourceSha256) throw new InvalidDataException("The original recording changed. Import and review the changed source before analysis.");
        var meta = Probe(asset.Path);
        var first = (int)Math.Ceiling(s.StartSeconds * meta.Fps);
        var last = Math.Min(meta.FrameCount, s.EndSeconds is { } end ? (int)Math.Ceiling(end * meta.Fps) : meta.FrameCount);
        if (first >= last) throw new ArgumentException("Selected interval is outside this recording.");
        using var arena = ArenaMask(meta.Width, meta.Height, s);
        using var capture = Open(asset.Path); capture.Set(VideoCaptureProperties.PosFrames, first);
        using var frame = new Mat();
        var points = new List<TrackPoint>(last - first); Detection? previous = null; double maxTimeError = 0;
        for (var index = first; index < last; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!capture.Read(frame) || frame.Empty()) throw new InvalidDataException($"Decoding stopped at frame {index}; incomplete analysis was not published.");
            var pts = capture.Get(VideoCaptureProperties.PosMsec) / 1000;
            var expected = index / meta.Fps;
            if (!double.IsFinite(pts)) throw new InvalidDataException("Decoder returned an invalid timestamp.");
            maxTimeError = Math.Max(maxTimeError, Math.Abs(pts - expected));
            using var binary = ProcessFrame(frame, s, arena);
            var detections = Detect(binary, s);
            if (detections.Count == 0)
            { points.Add(new(index, pts, null, null, PointStatus.Missing, 0)); previous = null; }
            else if (detections.Count > 1)
            {
                var likely = detections.OrderByDescending(d => d.Area).First();
                points.Add(new(index, pts, null, null, PointStatus.Ambiguous, detections.Count, likely.X, likely.Y, likely.Area)); previous = null;
            }
            else
            {
                var detection = detections[0];
                var jump = previous is not null && Math.Sqrt(Math.Pow(detection.X - previous.X, 2) + Math.Pow(detection.Y - previous.Y, 2)) > s.MaxJump;
                points.Add(jump ? new(index, pts, null, null, PointStatus.Jump, 1, detection.X, detection.Y, detection.Area)
                    : new(index, pts, detection.X, detection.Y, PointStatus.Tracked, 1, detection.X, detection.Y, detection.Area));
                previous = jump ? null : detection;
            }
            if ((index - first) % 30 == 0) progress?.Report(100d * (index - first + 1) / (last - first));
        }
        // Initial native port has explicitly verified CFR semantics. Irregular PTS
        // fail closed rather than silently deriving time from an average frame rate.
        VerifyNominalTiming(points, meta.Fps);
        var finalInfo = new FileInfo(asset.Path);
        if (finalInfo.Length != initialLength || finalInfo.LastWriteTimeUtc != initialWrite || HashFile(asset.Path) != hash)
            throw new InvalidDataException("Recording changed during processing; this run was not published.");
        progress?.Report(100);
        return new AnalysisRun
        {
            VideoId = asset.Id, SourceSha256 = hash, Metadata = meta, Settings = s, Points = points,
            AlgorithmContract = AlgorithmIdentity.Contract,
            Metrics = MetricCalculator.Calculate(points, meta, s), NominalTimingVerified = true,
            MaximumTimestampErrorSeconds = maxTimeError, ElapsedSeconds = timer.Elapsed.TotalSeconds
        };
    }

    public static byte[] ReadFrame(string path, int index, TrackingSettings? binarySettings = null)
    {
        return FrameDecoderPool.ReadShared(path, index, binarySettings);
    }

    public static byte[] ReadFrame(string path, int index, TrackingSettings? binarySettings, string expectedSha256) =>
        FrameDecoderPool.ReadShared(path, index, binarySettings, expectedSha256);

    public static void VerifyNominalTiming(IEnumerable<TrackPoint> points, double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0) throw new InvalidDataException("Invalid nominal frame rate.");
        foreach (var point in points)
        {
            var error = Math.Abs(point.TimeSeconds - point.Frame / fps);
            if (!double.IsFinite(point.TimeSeconds) || error > .001)
                throw new InvalidDataException($"Decoder timestamps are inconsistent with nominal CFR at frame {point.Frame}. Irregular timing is withheld from this native measurement contract.");
        }
    }
}
