using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MouseTrackerTabajara.Core;

[assembly: InternalsVisibleTo("MouseTrackerTabajara.Validation.Tests")]

namespace MouseTrackerTabajara.Application;

/// <summary>
/// Lowercase SHA-256 of the UTF-8 bytes <see cref="JsonDefaults.Serialize{T}"/> would produce.
/// Commits go through <see cref="IBufferWriter{T}"/> because a stream target flushes the whole document at once.
/// Cancellation is checked on each commit and before a larger replacement buffer is allocated.
/// A cancelled call returns no digest.
/// </summary>
internal static class CancelableJsonHash
{
    const int ChunkBytes = 16 * 1024;

    /// <summary>Per-flow observer for tests. Production leaves it null. It sees committed chunk lengths and cannot change bytes.</summary>
    internal static readonly AsyncLocal<Action<int>?> TestBeforeWrite = new();

    /// <summary>Largest replacement buffer actually allocated. Tests reset it. Production growth still honors the writer.</summary>
    internal static long TestLargestAllocatedBuffer;

    internal static string Hash<T>(T value, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var destination = new HashingBufferWriter(incremental, cancellation);
        WriteUtf8(value, destination, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void WriteUtf8<T>(T value, IBufferWriter<byte> destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var serializerOptions = JsonDefaults.Options;
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Encoder = serializerOptions.Encoder,
            Indented = serializerOptions.WriteIndented,
            IndentCharacter = serializerOptions.IndentCharacter,
            IndentSize = serializerOptions.IndentSize,
            MaxDepth = serializerOptions.MaxDepth,
            NewLine = serializerOptions.NewLine
        });
        try
        {
            JsonSerializer.Serialize(writer, value, serializerOptions);
            writer.Flush();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (FindCancellation(ex) is OperationCanceledException oce)
        {
            throw new OperationCanceledException(oce.Message, oce, cancellation);
        }
    }

    static OperationCanceledException? FindCancellation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is OperationCanceledException cancelled) return cancelled;
        return null;
    }

    sealed class HashingBufferWriter(IncrementalHash hash, CancellationToken cancellation) : IBufferWriter<byte>
    {
        byte[] _buffer = new byte[ChunkBytes];
        bool _faulted;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_faulted) throw new InvalidOperationException("Fingerprint writer already failed.");
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var needed = Math.Max(sizeHint, 1);
                if (_buffer.Length < needed)
                {
                    cancellation.ThrowIfCancellationRequested();
                    _buffer = new byte[needed];
                    if (needed > TestLargestAllocatedBuffer) TestLargestAllocatedBuffer = needed;
                }
                return _buffer;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Advance(int count)
        {
            if (count < 0 || (uint)count > (uint)_buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (_faulted) return;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (count == 0) return;
                TestBeforeWrite.Value?.Invoke(count);
                cancellation.ThrowIfCancellationRequested();
                hash.AppendData(_buffer.AsSpan(0, count));
                if (_buffer.Length > ChunkBytes) _buffer = new byte[ChunkBytes];
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }
}
