using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Par2Recovery;
using UsenetSharp.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Repair;

public partial class Par2RepairService
{
    private sealed class ResolvedSliceAccessor : IDisposable
    {
        private readonly IReadOnlyList<SourceLayout> _layouts;
        private readonly UsenetStreamingClient _client;
        private readonly RepairReadContext _reads;
        private readonly Func<int, bool> _isUnavailableSlice;
        private readonly Dictionary<int, (byte[] Bytes, IDisposable Reservation)> _bodies = new();
        private readonly Dictionary<int, HashSet<int>> _missing = new();
        private readonly Dictionary<int, HashSet<int>> _corrupt = new();
        // Upcoming segments of the current layout already requested; each holds its memory reservation.
        private readonly Dictionary<int, SourceRead> _readAhead = new();
        private CancellationTokenSource _readAheadCts = new();
        private long _readAheadBytes;
        private SourceLayout _layout;
        private long _cachedBodyBytes;
        private long _peakCachedBodyBytes;
        private long _retainedByteLimit;

        public ResolvedSliceAccessor(IReadOnlyList<SourceLayout> layouts, UsenetStreamingClient client,
            RepairReadContext reads, Func<int, bool> isUnavailableSlice)
        {
            _layouts = layouts;
            _layout = layouts[0];
            _client = client;
            _reads = reads;
            _isUnavailableSlice = isUnavailableSlice;
            _retainedByteLimit = reads.Budget.Limit;
            foreach (var layout in layouts)
            {
                _missing[layout.FileIndex] = [];
                _corrupt[layout.FileIndex] = [];
            }
        }

        public IReadOnlyCollection<int> MissingSegmentIndices => _missing[_layout.FileIndex];
        public IReadOnlyCollection<int> CorruptSegmentIndices => _corrupt[_layout.FileIndex];
        public long CachedBodyBytes => Interlocked.Read(ref _cachedBodyBytes);
        public long PeakCachedBodyBytes => Interlocked.Read(ref _peakCachedBodyBytes);
        public long RetainedByteLimit => Interlocked.Read(ref _retainedByteLimit);

        public void NoteMissing(int index) { _missing[_layout.FileIndex].Add(index); Evict(index); DropReadAhead(index); }
        public void NoteCorrupt(int index) { _corrupt[_layout.FileIndex].Add(index); Evict(index); DropReadAhead(index); }

        public void UseLayout(SourceLayout layout)
        {
            if (_layout.FileIndex == layout.FileIndex) return;
            BeginSequentialPass();
            _layout = layout;
        }

        public void BeginSequentialPass()
        {
            DiscardReadAhead();
            foreach (var body in _bodies.Values) body.Reservation.Dispose();
            _bodies.Clear();
            Interlocked.Exchange(ref _cachedBodyBytes, 0);
        }

        public void SetRetainedByteLimit(long limit)
        {
            if (limit <= 0) throw new Par2MemoryCapExceededException("PAR2 repair has no memory available for source segments.");
            Interlocked.Exchange(ref _retainedByteLimit, limit);
            if (CachedBodyBytes > limit) BeginSequentialPass();
        }

        public async Task<byte[]?> FetchSliceBytesAsync(int globalSlice, int sliceSize, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_isUnavailableSlice(globalSlice)) return null;
            var layout = FindLayout(globalSlice);
            if (layout is null) return null;
            UseLayout(layout);
            var range = layout.Map.SliceFileRange(globalSlice);
            foreach (var index in _bodies.Keys.Where(index => layout.Map.SegmentRanges[index].EndExclusive <= range.StartInclusive).ToArray())
                Evict(index);
            var bytes = new byte[sliceSize];
            long copied = 0;
            foreach (var index in layout.Map.SegmentIndicesForGlobalSlice(globalSlice))
            {
                var body = await GetBodyAsync(index, ct).ConfigureAwait(false);
                if (body is null) return null;
                var segment = layout.Map.SegmentRanges[index];
                var start = Math.Max(range.StartInclusive, segment.StartInclusive);
                var end = Math.Min(range.EndExclusive, segment.EndExclusive);
                var count = checked((int)(end - start));
                body.AsSpan((int)(start - segment.StartInclusive), count).CopyTo(bytes.AsSpan((int)(start - range.StartInclusive)));
                copied += count;
            }
            return copied == range.Count ? bytes : null;
        }

        public Task<byte[]?> GetSegmentBodyForPatchAsync(int index, CancellationToken ct)
            => _layout.Map.GlobalSlicesForSegment(index).All(_isUnavailableSlice)
                ? Task.FromResult<byte[]?>(null) : GetBodyAsync(index, ct);

        private SourceLayout? FindLayout(int globalSlice)
        {
            var lower = 0;
            var upper = _layouts.Count - 1;
            while (lower <= upper)
            {
                var middle = lower + (upper - lower) / 2;
                var candidate = _layouts[middle];
                if (globalSlice < candidate.Map.GlobalSliceBase) upper = middle - 1;
                else if (globalSlice >= candidate.Map.GlobalSliceBase + candidate.Map.SliceCount) lower = middle + 1;
                else return candidate;
            }
            return null;
        }

        private async Task<byte[]?> GetBodyAsync(int index, CancellationToken ct)
        {
            if (MissingSegmentIndices.Contains(index) || CorruptSegmentIndices.Contains(index)) return null;
            if (_bodies.TryGetValue(index, out var cached)) return cached.Bytes;
            if (_readAhead.Remove(index, out var read))
            {
                _readAheadBytes -= read.Length;
            }
            else
            {
                var length = checked((int)_layout.Map.SegmentRanges[index].Count);
                // Read-ahead never costs the caller its own segment.
                if (length > RetainedByteLimit - CachedBodyBytes - _readAheadBytes) DiscardReadAhead();
                if (length > RetainedByteLimit - CachedBodyBytes)
                    throw new Par2MemoryCapExceededException("PAR2 source window exceeds the repair memory cap.");
#pragma warning disable CA2000 // the reservation moves into the read; it is disposed on failure or with the cached body
                read = StartRead(index, length, _reads.Budget.Reserve(length + 128L));
#pragma warning restore CA2000
            }
            ScheduleReadAhead(index);

            byte[]? bytes;
            try
            {
                bytes = await read.Body.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsUnavailableArticle(exception))
            {
                read.Reservation.Dispose();
                _reads.NoteUnavailable(_layout.SegmentIds[index], exception);
                if (_reads.MissingIds.Contains(_layout.SegmentIds[index])) NoteMissing(index);
                else NoteCorrupt(index);
                return null;
            }
            catch
            {
                read.Reservation.Dispose();
                throw;
            }

            _bodies[index] = (bytes!, read.Reservation);
            var retained = Interlocked.Add(ref _cachedBodyBytes, bytes!.Length);
            Interlocked.Exchange(ref _peakCachedBodyBytes, Math.Max(PeakCachedBodyBytes, retained));
            return bytes;
        }

        // Passes read segments in order, so request the next ones while the caller consumes this one.
        // FetchGate still bounds how many BODY requests are on the wire.
        private void ScheduleReadAhead(int index)
        {
            var window = 2 * _reads.Concurrency;
            var segmentCount = _layout.SegmentIds.Length;
            for (var next = index + 1; next < segmentCount && next <= index + window; next++)
            {
                if (_readAhead.ContainsKey(next) || _bodies.ContainsKey(next)
                    || MissingSegmentIndices.Contains(next) || CorruptSegmentIndices.Contains(next)
                    || _layout.Map.GlobalSlicesForSegment(next).All(_isUnavailableSlice))
                    continue;
                var length = checked((int)_layout.Map.SegmentRanges[next].Count);
                // Keep room for a segment the caller asks for outside the read-ahead window.
                if (2L * length > RetainedByteLimit - CachedBodyBytes - _readAheadBytes) return;
                IDisposable reservation;
#pragma warning disable CA2000 // the reservation moves into the queued read; DiscardReadAhead or GetBodyAsync releases it
                try { reservation = _reads.Budget.Reserve(length + 128L); }
#pragma warning restore CA2000
                catch (Par2BudgetExceededException) { return; }
                _readAhead[next] = StartRead(next, length, reservation);
                _readAheadBytes += length;
            }
        }

        private SourceRead StartRead(int index, int length, IDisposable reservation)
        {
            var body = ReadBodyAsync(_layout.SegmentIds, index, length, _readAheadCts.Token);
            return new SourceRead(body, reservation, length);
        }

        private async Task<byte[]> ReadBodyAsync(string[] segmentIds, int index, int length, CancellationToken ct)
        {
            await _reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another post's article (message-id collision) is rejected on each provider so
                // one holding this file's own article can serve it; otherwise it surfaces as a
                // missing article and its slices are rebuilt from recovery blocks (#130).
                UsenetDecodedBodyResponse response;
                using (YencFileValidationContext.BeginRepairSourceRead(segmentIds))
                    response = await _client.DecodedBodyAsync(segmentIds[index], ct).ConfigureAwait(false);
                await using var stream = response.Stream!;
                await using var counted = new RepairCountingStream(stream, bytes => _reads.ReadBytes(bytes));
                var bytes = new byte[length];
                await counted.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                var extra = new byte[1];
                if (await counted.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("PAR2 source article exceeds its validated volume range.");
                return bytes;
            }
            finally { _reads.FetchGate.Release(); }
        }

        // Unconsumed reads are cancelled and their memory returned at once; the abandoned
        // requests finish on their own and their results are dropped.
        private void DiscardReadAhead()
        {
            if (_readAhead.Count == 0) return;
            var abandoned = _readAhead.Values.Select(read => read.Body).ToArray();
            foreach (var read in _readAhead.Values) read.Reservation.Dispose();
            _readAhead.Clear();
            _readAheadBytes = 0;
            var cancelled = _readAheadCts;
            _readAheadCts = new CancellationTokenSource();
            cancelled.Cancel();
            _ = Task.WhenAll(abandoned).ContinueWith(
                static (all, state) =>
                {
                    _ = all.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                }, cancelled, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void DropReadAhead(int index)
        {
            if (!_readAhead.Remove(index, out var read)) return;
            read.Reservation.Dispose();
            _readAheadBytes -= read.Length;
            _ = read.Body.ContinueWith(static body => _ = body.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Evict(int index)
        {
            if (!_bodies.Remove(index, out var body)) return;
            body.Reservation.Dispose();
            Interlocked.Add(ref _cachedBodyBytes, -body.Bytes.Length);
        }

        public void Dispose()
        {
            BeginSequentialPass();
            _readAheadCts.Dispose();
        }
    }

    // Budget reservations release at most once, so a read can be discarded and consumed safely.
    private sealed record SourceRead(Task<byte[]> Body, IDisposable Reservation, int Length);
}