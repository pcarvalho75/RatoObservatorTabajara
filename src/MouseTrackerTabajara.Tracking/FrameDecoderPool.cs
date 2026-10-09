using System.Security.Cryptography;
using MouseTrackerTabajara.Core;
using OpenCvSharp;

namespace MouseTrackerTabajara.Tracking;

public sealed record FrameDecoderStatistics(long Opens, long Disposals, long Reads, long Seeks, int ActiveSessions);

/// <summary>
/// Bounded synchronous viewer resources, independent of analysis. One decoded frame per session;
/// caller-owned encoded buffers. On Windows an open read-only guard denies source writes/deletion.
/// Length/creation/write stamps are checked before and after each request; SHA256 is pinned at open.
/// A bounded identity ledger retains at most 128 source identities even after decoder eviction;
/// changed sources fail until explicit Reset. For a known recording identity use expectedSha256.
/// Reset explicitly releases all source handles and clears that ledger.
/// </summary>
public sealed class FrameDecoderPool : IDisposable
{
    private static readonly FrameDecoderPool Shared = new(2);
    static FrameDecoderPool() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();
    public static byte[] ReadShared(string path, int frame, TrackingSettings? settings = null, string? expectedSha256 = null) =>
        Shared.Read(path, frame, settings, expectedSha256);
    public static void ResetShared() => Shared.Reset();
    public static FrameDecoderStatistics SharedStatistics => Shared.Statistics;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SourceStamp Stamp, string Hash)> _identities = new(StringComparer.OrdinalIgnoreCase);
    private long _opens, _disposals, _reads, _seeks, _clock;
    private bool _disposed;
    public FrameDecoderPool(int capacity = 2)
    {
        if (capacity is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(capacity), "One to four viewer sessions are supported.");
        _capacity = capacity;
    }
    public FrameDecoderStatistics Statistics
    { get { lock (_gate) return new(_opens, _disposals, _reads, _seeks, _sessions.Count); } }

    public byte[] Read(string path, int index, TrackingSettings? binarySettings = null, string? expectedSha256 = null)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "Source frames are zero based.");
        if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("Expected source identity must be a SHA256 hex string.", nameof(expectedSha256));
        binarySettings?.Validate();
        var canonical = Path.GetFullPath(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Session? session = null;
            try
            {
                var stamp = SourceStamp.Read(canonical);
                if (_identities.TryGetValue(canonical, out var identity) && identity.Stamp != stamp)
                    throw new InvalidDataException("Original recording changed since the viewer first opened it; reset and verify source identity.");
                if (_sessions.TryGetValue(canonical, out session))
                {
                    if (stamp != session.Stamp) throw new InvalidDataException("Original recording changed; reset the viewer and verify the new source identity.");
                }
                else
                {
                    if (!_identities.ContainsKey(canonical) && _identities.Count == 128)
                        throw new InvalidOperationException("Viewer source identity budget is full (128 recordings); explicitly reset viewer resources.");
                    if (_sessions.Count == _capacity) Remove(_sessions.MinBy(x => x.Value.LastUse).Key);
                    session = Open(canonical, stamp);
                    _sessions.Add(canonical, session);
                    if (_identities.TryGetValue(canonical, out identity) && identity.Hash != session.Hash)
                        throw new InvalidDataException("Original recording content changed despite identical file stamps; reset and verify source identity.");
                }
                if (expectedSha256 is not null && !string.Equals(session.Hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Original recording differs from the expected source SHA256.");
                _identities.TryAdd(canonical, (session.Stamp, session.Hash));
                if (index >= session.FrameCount) throw new ArgumentOutOfRangeException(nameof(index), "Frame is outside the source recording.");
                session.LastUse = ++_clock;
                if (session.LastFrame != index)
                {
                    if (session.NextFrame != index)
                    {
                        if (!session.Capture.Set(VideoCaptureProperties.PosFrames, index)) throw new InvalidDataException("Decoder rejected the exact source seek.");
                        _seeks++;
                    }
                    if (!session.Capture.Read(session.Frame) || session.Frame.Empty()) throw new InvalidDataException($"Cannot decode source frame {index}.");
                    _reads++;
                    var next = session.Capture.Get(VideoCaptureProperties.PosFrames);
                    if (!double.IsFinite(next) || Math.Abs(next - (index + 1d)) > .01)
                        throw new InvalidDataException("Decoder did not confirm the requested canonical source frame.");
                    session.LastFrame = index; session.NextFrame = index + 1;
                }
                byte[] bytes;
                // Independent annotation must see decoded source channels without a lossy preview transform.
                if (binarySettings is null) bytes = session.Frame.ToBytes(".png");
                else
                {
                    using var arena = OpenCvTracker.ArenaMask(session.Frame.Width, session.Frame.Height, binarySettings);
                    using var binary = OpenCvTracker.ProcessFrame(session.Frame, binarySettings, arena);
                    bytes = binary.ToBytes(".png");
                }
                if (SourceStamp.Read(canonical) != session.Stamp)
                    throw new InvalidDataException("Original recording changed during frame decoding; pixels withheld.");
                return bytes;
            }
            catch
            {
                // Any uncertain decoder/source state is discarded; no stale cached pixels survive failure.
                if (_sessions.ContainsKey(canonical)) Remove(canonical);
                throw;
            }
        }
    }
    private Session Open(string path, SourceStamp stamp)
    {
        var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        VideoCapture? capture = null;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(guard)).ToLowerInvariant();
            if (SourceStamp.Read(path) != stamp) throw new InvalidDataException("Source changed while opening the viewer.");
            capture = new VideoCapture(path, VideoCaptureAPIs.FFMPEG);
            if (!capture.IsOpened()) throw new InvalidDataException("FFmpeg could not open this recording.");
            _opens++;
            var count = capture.Get(VideoCaptureProperties.FrameCount);
            var width = capture.Get(VideoCaptureProperties.FrameWidth); var height = capture.Get(VideoCaptureProperties.FrameHeight);
            if (!double.IsFinite(count) || count < 1 || count > int.MaxValue || Math.Abs(count - Math.Round(count)) > .01 ||
                !double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 || width * height > 16_777_216)
                throw new InvalidDataException("Invalid or oversized viewer source metadata (maximum 16777216 pixels).");
            return new(capture, guard, stamp, hash, (int)Math.Round(count));
        }
        catch { if (capture is not null) { if (capture.IsOpened()) _disposals++; capture.Dispose(); } guard.Dispose(); throw; }
    }
    private void Remove(string path) { var session = _sessions[path]; _sessions.Remove(path); session.Dispose(); _disposals++; }
    public void Reset() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); foreach (var path in _sessions.Keys.ToArray()) Remove(path); _identities.Clear(); } }
    public void Dispose() { lock (_gate) { if (_disposed) return; foreach (var path in _sessions.Keys.ToArray()) Remove(path); _identities.Clear(); _disposed = true; } }
    private sealed record SourceStamp(long Length, DateTime Created, DateTime Written)
    {
        public static SourceStamp Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Original recording is missing.", path);
            return new(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }
    }
    private sealed class Session(VideoCapture capture, FileStream guard, SourceStamp stamp, string hash, int count) : IDisposable
    {
        public VideoCapture Capture { get; } = capture;
        public Mat Frame { get; } = new();
        public SourceStamp Stamp { get; } = stamp;
        public string Hash { get; } = hash;
        public int FrameCount { get; } = count;
        public int LastFrame { get; set; } = -1;
        public int NextFrame { get; set; }
        public long LastUse { get; set; }
        public void Dispose() { Frame.Dispose(); Capture.Dispose(); guard.Dispose(); }
    }
}
