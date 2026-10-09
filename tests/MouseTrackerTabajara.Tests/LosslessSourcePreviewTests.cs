using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Tests;

public sealed class LosslessSourcePreviewTests(ITestOutputHelper output)
{
    [Fact]
    public void OriginalPreviewPreservesEveryDecodedChannelAcrossSeekingAndReuse()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-lossless-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        output.WriteLine("Retained synthetic source: " + folder);
        var path = Path.Combine(folder, "color-detail.avi");
        using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new Size(64, 64)))
        {
            Assert.True(writer.IsOpened(), "Synthetic FFV1 writer must be available.");
            for (var frame = 0; frame < 5; frame++)
            {
                using var image = new Mat(64, 64, MatType.CV_8UC3);
                for (var y = 0; y < 64; y++)
                    for (var x = 0; x < 64; x++)
                        image.Set(y, x, new Vec3b((byte)((x * 37 + y * 11 + frame * 19) % 256), (byte)((x * 7 + y * 43 + frame * 29) % 256), (byte)((x * 23 + y * 17 + frame * 31) % 256)));
                writer.Write(image);
            }
        }
        var hash = OpenCvTracker.HashFile(path);
        var originals = new List<Mat>();
        try
        {
            using (var source = new VideoCapture(path, VideoCaptureAPIs.FFMPEG))
            {
                Assert.True(source.IsOpened());
                for (var i = 0; i < 5; i++)
                {
                    var image = new Mat();
                    Assert.True(source.Read(image));
                    originals.Add(image);
                }
            }
            using var pool = new FrameDecoderPool(1);
            foreach (var frame in new[] { 0, 3, 2, 2, 4, 0 })
            {
                using var preview = Cv2.ImDecode(pool.Read(path, frame, expectedSha256: hash), ImreadModes.Color);
                Assert.Equal(originals[frame].Size(), preview.Size());
                Assert.Equal(originals[frame].Type(), preview.Type());
                Assert.Equal(0d, Cv2.Norm(originals[frame], preview, NormTypes.INF));
            }
            Assert.Equal(hash, OpenCvTracker.HashFile(path));
            Assert.Equal(1, pool.Statistics.Opens);
        }
        finally { foreach (var image in originals) image.Dispose(); }
    }
}
