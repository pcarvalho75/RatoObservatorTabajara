using System.Diagnostics;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Tests;

public class FrameDecoderPoolTests(ITestOutputHelper output)
{
    private sealed class Media : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "tabajara-playback-" + Guid.NewGuid().ToString("N"));
        public Media() => Directory.CreateDirectory(Folder);
        public string Video(string name, int offset = 0)
        {
            var path = Path.Combine(Folder, name + ".avi");
            using var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new Size(300, 200));
            Assert.True(writer.IsOpened());
            for (var f = 0; f < 70; f++)
            {
                using var frame = new Mat(200, 300, MatType.CV_8UC3, new Scalar(20 + 3 * f + offset, 40 + 2 * f, 230 - 3 * f));
                Cv2.Circle(frame, new Point(100 + f, 100), 7, Scalar.Black, -1); writer.Write(frame);
            }
            return path;
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
    private static void Signature(byte[] bytes, int frame, int offset = 0)
    {
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        Assert.Equal(300, image.Width); Assert.Equal(200, image.Height);
        var pixel = image.At<Vec3b>(10, 10);
        Assert.InRange(Math.Abs(pixel.Item0 - (20 + 3 * frame + offset)), 0, 1);
        Assert.InRange(Math.Abs(pixel.Item1 - (40 + 2 * frame)), 0, 1);
        Assert.InRange(Math.Abs(pixel.Item2 - (230 - 3 * frame)), 0, 1);
    }
    [Fact] public void SequentialRepeatedAndOutOfOrderRequestsKeepCanonicalSignatures()
    {
        using var media = new Media(); var path = media.Video("first");
        using var pool = new FrameDecoderPool();
        foreach (var frame in new[] { 0, 1, 2, 3, 3, 20, 19, 69, 0, 42 }) Signature(pool.Read(path, frame), frame);
        Assert.Equal(1, pool.Statistics.Opens); Assert.Equal(9, pool.Statistics.Reads);
        Assert.Equal(5, pool.Statistics.Seeks);
        var bytes = pool.Read(path, 42); Array.Fill<byte>(bytes, 0);
        Signature(pool.Read(path, 42), 42); // returned buffers are never shared with a cached encoded result
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Read(path, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Read(path, 70));
        Assert.Equal(0, pool.Statistics.ActiveSessions);
    }
    [Fact] public async Task ConcurrentSourcesAreSerializedAndEvictionDisposesNativeResources()
    {
        using var media = new Media(); var a = media.Video("a"); var b = media.Video("b", 1); var c = media.Video("c", 2);
        using var pool = new FrameDecoderPool(2);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => Signature(pool.Read(i % 2 == 0 ? a : b, i), i, i % 2))));
        Assert.Equal(2, pool.Statistics.Opens); Assert.Equal(2, pool.Statistics.ActiveSessions);
        Signature(pool.Read(c, 5), 5, 2);
        Assert.Equal(3, pool.Statistics.Opens); Assert.Equal(1, pool.Statistics.Disposals); Assert.Equal(2, pool.Statistics.ActiveSessions);
        pool.Reset(); Assert.Equal(0, pool.Statistics.ActiveSessions); Assert.Equal(3, pool.Statistics.Disposals);
        Signature(pool.Read(a, 4), 4);
        pool.Dispose(); pool.Dispose();
        Assert.Equal(pool.Statistics.Opens, pool.Statistics.Disposals);
        Assert.Throws<ObjectDisposedException>(() => pool.Read(a, 1));
        Assert.Throws<ObjectDisposedException>(() => pool.Reset());
    }
    [Fact] public void ChangedDeletedUndecodableAndUnmatchedSourcesFailWithoutStalePixels()
    {
        using var media = new Media(); var path = media.Video("source"); var expected = OpenCvTracker.HashFile(path);
        using var pool = new FrameDecoderPool(); Signature(pool.Read(path, 10, expectedSha256: expected), 10);
        // Windows guard prevents even same-length/same-stamp writes and deletion while decoding resources persist.
        Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.Throws<IOException>(() => File.Delete(path));
        Signature(pool.Read(path, 10, expectedSha256: expected), 10);
        pool.Reset(); media.Video("source", 2);
        Assert.Throws<InvalidDataException>(() => pool.Read(path, 10, expectedSha256: expected));
        Assert.Equal(0, pool.Statistics.ActiveSessions);
        var changedHash = OpenCvTracker.HashFile(path);
        Signature(pool.Read(path, 10, expectedSha256: changedHash), 10, 2);
        pool.Reset(); File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => pool.Read(path, 10));
        var invalid = Path.Combine(media.Folder, "invalid.avi"); File.WriteAllText(invalid, "not a recording");
        Assert.Throws<InvalidDataException>(() => pool.Read(invalid, 0));
        Assert.Equal(0, pool.Statistics.ActiveSessions);
        Assert.Throws<ArgumentException>(() => pool.Read(invalid, 0, expectedSha256: "bad"));
    }
    [Fact] public void PerRequestMasksDoNotContaminateSourceOrOtherSettings()
    {
        using var media = new Media(); var path = media.Video("mask"); using var pool = new FrameDecoderPool();
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Blur = 0, Threshold = 255, Invert = false };
        using var empty = Cv2.ImDecode(pool.Read(path, 5, settings), ImreadModes.Grayscale);
        Assert.Equal(0, Cv2.CountNonZero(empty));
        using var full = Cv2.ImDecode(pool.Read(path, 5, settings with { Invert = true }), ImreadModes.Grayscale);
        Assert.Equal(255, full.At<byte>(50, 150));
        using var excluded = Cv2.ImDecode(pool.Read(path, 5, settings with { Invert = true, Exclusions = [new(.4, .2, .6, .4)] }), ImreadModes.Grayscale);
        Assert.Equal(0, excluded.At<byte>(50, 150));
        Signature(pool.Read(path, 5), 5);
        Assert.Equal(1, pool.Statistics.Reads); Assert.Equal(1, pool.Statistics.Opens);
    }
    [Fact] public void EvictedSourcesKeepIdentityEvenWhenMutationRestoresFileStamps()
    {
        using var media = new Media(); var a = media.Video("identity"); var b = media.Video("other");
        using var pool = new FrameDecoderPool(1); Signature(pool.Read(a, 4), 4); Signature(pool.Read(b, 4), 4);
        var creation = File.GetCreationTimeUtc(a); var written = File.GetLastWriteTimeUtc(a);
        var bytes = File.ReadAllBytes(a); bytes[^1] ^= 1; File.WriteAllBytes(a, bytes);
        File.SetCreationTimeUtc(a, creation); File.SetLastWriteTimeUtc(a, written);
        var error = Assert.Throws<InvalidDataException>(() => pool.Read(a, 4));
        Assert.Contains("content changed", error.Message);
        Assert.Equal(0, pool.Statistics.ActiveSessions);
        Assert.Equal(pool.Statistics.Opens, pool.Statistics.Disposals);
        pool.Reset(); media.Video("identity"); Signature(pool.Read(a, 4), 4);
    }
    [Fact] public void SourceStampChangeWhileOpenWithholdsCachedPixels()
    {
        using var media = new Media(); var path = media.Video("stamp"); using var pool = new FrameDecoderPool();
        Signature(pool.Read(path, 8), 8);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Assert.Throws<InvalidDataException>(() => pool.Read(path, 8));
        Assert.Equal(0, pool.Statistics.ActiveSessions);
    }
    [Fact] public void CalledPublicPathPreservesLegacyNullAndExpectedHashOverloads()
    {
        using var media = new Media(); var path = media.Video("public"); var hash = OpenCvTracker.HashFile(path);
        FrameDecoderPool.ResetShared();
        try
        {
            var before = FrameDecoderPool.SharedStatistics.Opens;
            Signature(OpenCvTracker.ReadFrame(path, 3, null), 3);
            Signature(OpenCvTracker.ReadFrame(path, 4, null, hash), 4);
            Assert.Equal(before + 1, FrameDecoderPool.SharedStatistics.Opens);
            Assert.Throws<InvalidDataException>(() => OpenCvTracker.ReadFrame(path, 4, null, new string('0', 64)));
            Assert.Equal(0, FrameDecoderPool.SharedStatistics.ActiveSessions);
        }
        finally { FrameDecoderPool.ResetShared(); }
    }
    [Fact] public void IdentityBudgetIsBoundedAndExplicitResetAllowsANewSource()
    {
        using var media = new Media(); var source = media.Video("template"); using var pool = new FrameDecoderPool();
        for (var i = 0; i < 128; i++)
        {
            var path = Path.Combine(media.Folder, $"identity-{i}.avi"); File.Copy(source, path);
            Signature(pool.Read(path, 0), 0); Assert.InRange(pool.Statistics.ActiveSessions, 1, 2);
        }
        var extra = Path.Combine(media.Folder, "extra.avi"); File.Copy(source, extra);
        Assert.Throws<InvalidOperationException>(() => pool.Read(extra, 0));
        Assert.Equal(128, pool.Statistics.Opens);
        pool.Reset(); Signature(pool.Read(extra, 0), 0);
    }
    [Fact] public void MeasureIdenticalSyntheticRequestsAgainstLegacyOpenPerRequest()
    {
        using var media = new Media(); var path = media.Video("timing");
        var requests = Enumerable.Range(0, 60).Concat(new[] { 20, 21, 22, 5, 6, 7 }).ToArray();
        var legacyOpens = 0; var timer = Stopwatch.StartNew();
        foreach (var index in requests)
        {
            using var capture = new VideoCapture(path, VideoCaptureAPIs.FFMPEG); Assert.True(capture.IsOpened()); legacyOpens++;
            Assert.True(capture.Set(VideoCaptureProperties.PosFrames, index));
            using var frame = new Mat(); Assert.True(capture.Read(frame)); Signature(frame.ToBytes(".jpg"), index);
        }
        timer.Stop(); var legacy = timer.Elapsed.TotalMilliseconds;
        using var pool = new FrameDecoderPool(); timer.Restart();
        foreach (var index in requests) Signature(pool.Read(path, index), index);
        timer.Stop();
        output.WriteLine($"Identical synthetic requests={requests.Length}; legacy opens={legacyOpens}, ms={legacy:F3}; persistent opens={pool.Statistics.Opens}, seeks={pool.Statistics.Seeks}, ms={timer.Elapsed.TotalMilliseconds:F3}. Includes encoding and independent signature assertions; no UI throughput inference.");
        Assert.Equal(66, legacyOpens); Assert.Equal(1, pool.Statistics.Opens); Assert.Equal(2, pool.Statistics.Seeks);
    }
}
