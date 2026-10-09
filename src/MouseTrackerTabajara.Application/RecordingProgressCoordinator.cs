using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

/// <summary>
/// Serializes engine progress for one batch. A late running update cannot replace a
/// terminal recording, and the collection percent is the share of terminal recordings,
/// never the percent of whichever clip reported most recently.
/// </summary>
public sealed class RecordingProgressCoordinator
{
    private readonly object _state = new();
    private readonly object _deliver = new();
    private readonly Dictionary<string, Slot> _slots;
    private readonly Slot[] _order;
    private readonly IProgress<BatchProgress>? _legacy;
    private readonly IProgress<RecordingBatchProgress>? _typed;
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _clock;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _observerErrors = new(StringComparer.Ordinal);
    private DateTime _nextNonTerminal;
    private long _sequence;
    private long _lastDelivered;

    public RecordingProgressCoordinator(
        IReadOnlyList<FrozenRecordingIdentity> recordings,
        IProgress<BatchProgress>? legacy,
        IProgress<RecordingBatchProgress>? typed,
        TimeSpan minimumInterval,
        Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "Progress interval cannot be negative.");
        _legacy = legacy;
        _typed = typed;
        _interval = minimumInterval;
        _clock = clock ?? (() => DateTime.UtcNow);
        _order = new Slot[recordings.Count];
        _slots = new Dictionary<string, Slot>(recordings.Count, StringComparer.Ordinal);
        for (var index = 0; index < recordings.Count; index++)
        {
            var recording = recordings[index] ?? throw new ArgumentException("A recording identity is missing.", nameof(recordings));
            var slot = new Slot(recording.VideoId, recording.Index, recording.Filename);
            _order[index] = slot;
            _slots.Add(recording.VideoId, slot);
        }
    }

    public string? ProgressObserverError(string videoId)
        => _observerErrors.TryGetValue(videoId, out var error) ? error : null;

    public void Report(BatchProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Delivery? delivery;
        lock (_state)
        {
            if (!_slots.TryGetValue(update.JobId, out var slot))
                return;
            if (slot.Authoritative)
                return;
            var terminal = IsTerminal(update.State);
            if (slot.Terminal && (!terminal || slot.State != update.State))
                return;
            slot.State = update.State;
            slot.Terminal = terminal;
            if (update.ItemPercent is { } percent)
                slot.ItemPercent = percent;
            if (terminal && update.State is BatchItemState.Completed or BatchItemState.Reused && slot.ItemPercent is null)
                slot.ItemPercent = 100d;
            slot.Message = LegacyStatus(update.State, update.Message);
            if (!ShouldDeliver(terminal))
                return;
            delivery = Capture(slot, ++_sequence);
        }

        if (delivery is { } ready)
            Deliver(ready);
    }

    public void ReportFinal(string videoId, BatchItemState state, string? message, double? itemPercent)
    {
        if (!IsTerminal(state))
            throw new ArgumentOutOfRangeException(nameof(state), "The authoritative recording state has to be terminal.");
        Delivery? delivery;
        lock (_state)
        {
            if (!_slots.TryGetValue(videoId, out var slot))
                return;
            slot.Authoritative = true;
            slot.Terminal = true;
            slot.State = state;
            if (itemPercent is not null)
                slot.ItemPercent = itemPercent;
            else if (state is BatchItemState.Completed or BatchItemState.Reused && slot.ItemPercent is null)
                slot.ItemPercent = 100d;
            slot.Message = LegacyStatus(state, message);
            if (slot.LastDeliveredState == state && slot.LastDeliveredMessage == slot.Message)
                return;
            delivery = Capture(slot, ++_sequence);
            slot.LastDeliveredState = state;
            slot.LastDeliveredMessage = slot.Message;
        }

        if (delivery is { } ready)
            Deliver(ready);
    }

    private bool ShouldDeliver(bool terminal)
    {
        if (_legacy is null && _typed is null)
            return false;
        if (terminal || _interval <= TimeSpan.Zero)
            return true;
        var now = _clock();
        if (now < _nextNonTerminal)
            return false;
        _nextNonTerminal = now + _interval;
        return true;
    }

    private Delivery Capture(Slot focus, long sequence)
    {
        var completed = 0;
        var reused = 0;
        var failed = 0;
        var cancelled = 0;
        var running = 0;
        foreach (var slot in _order)
        {
            switch (slot.State)
            {
                case BatchItemState.Completed: completed++; break;
                case BatchItemState.Reused: reused++; break;
                case BatchItemState.Failed: failed++; break;
                case BatchItemState.Cancelled: cancelled++; break;
                case BatchItemState.Running: running++; break;
            }
        }

        var total = _order.Length;
        var terminal = completed + reused + failed + cancelled;
        var collection = total == 0 ? 0d : 100d * terminal / total;
        var typed = new RecordingBatchProgress
        {
            VideoId = focus.VideoId,
            InputIndex = focus.Index,
            ItemState = focus.State,
            ItemPercent = focus.ItemPercent,
            ItemMessage = focus.Message,
            TotalRecordings = total,
            CompletedRecordings = completed,
            ReusedRecordings = reused,
            FailedRecordings = failed,
            CancelledRecordings = cancelled,
            RunningRecordings = running,
            CollectionPercent = collection,
            CountInterpretation = RecordingAttempts.CountInterpretation
        };
        var legacy = new BatchProgress(completed + reused, total, focus.Filename, collection, focus.Message ?? "Queued");
        return new Delivery(sequence, focus.VideoId, legacy, typed);
    }

    private void Deliver(Delivery delivery)
    {
        lock (_deliver)
        {
            if (delivery.Sequence < _lastDelivered)
                return;
            _lastDelivered = delivery.Sequence;
            try
            {
                _legacy?.Report(delivery.Legacy);
            }
            catch (Exception ex)
            {
                Remember(delivery.VideoId, ex);
            }

            try
            {
                _typed?.Report(delivery.Typed);
            }
            catch (Exception ex)
            {
                Remember(delivery.VideoId, ex);
            }
        }
    }

    private void Remember(string videoId, Exception exception)
        => _observerErrors[videoId] = OneLine(exception.Message);

    private static bool IsTerminal(BatchItemState state)
        => state is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled;

    internal static string LegacyStatus(BatchItemState state, string? message) => state switch
    {
        BatchItemState.Completed => "Completed",
        BatchItemState.Reused => "Reused matching immutable run",
        BatchItemState.Failed => "Failed: " + OneLine(message),
        BatchItemState.Cancelled when string.IsNullOrWhiteSpace(message) => "Cancelled",
        BatchItemState.Cancelled when message.TrimStart().StartsWith("Cancelled", StringComparison.Ordinal) => OneLine(message),
        BatchItemState.Cancelled => "Cancelled: " + OneLine(message),
        BatchItemState.Running => "Processing",
        _ => "Queued"
    };

    internal static string OneLine(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "The operation failed without a message." : message.Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
            text = text[..newline].TrimEnd();
        const int max = 500;
        return text.Length <= max ? text : text[..max];
    }

    private sealed class Slot(string videoId, int index, string filename)
    {
        public string VideoId { get; } = videoId;
        public int Index { get; } = index;
        public string Filename { get; } = filename;
        public BatchItemState State = BatchItemState.Queued;
        public bool Terminal;
        public bool Authoritative;
        public double? ItemPercent;
        public string? Message;
        public BatchItemState? LastDeliveredState;
        public string? LastDeliveredMessage;
    }

    private readonly record struct Delivery(long Sequence, string VideoId, BatchProgress Legacy, RecordingBatchProgress Typed);
}

public sealed record FrozenRecordingIdentity(string VideoId, int Index, string Filename);
