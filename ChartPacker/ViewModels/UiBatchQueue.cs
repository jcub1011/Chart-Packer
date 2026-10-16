using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// Items reported from any thread, taken in on the UI thread a batch at a time, below input and drawing, so a
    /// fast run of them neither holds the UI up nor has the window lay itself out once per item.
    /// </summary>
    /// <remarks>
    /// The batch handler is given the items as it enumerates them, while the budget lasts, so its own work counts
    /// against the budget. It must enumerate every item it's given, or <see cref="Clear"/> the queue.
    /// </remarks>
    internal sealed class UiBatchQueue<T> : IProgress<T>
    {
        // Each item, and whether it holds one of the room's places, given back once it's taken in.
        private readonly ConcurrentQueue<(T Item, bool Counted)> _items = new();
        private readonly Action<IEnumerable<T>> _take;
        private readonly TimeSpan _budget;
        private readonly DispatcherTimer _timer;

        // The places left for items reported with ReportAsync; null when there's no limit.
        private readonly SemaphoreSlim? _room;

        // Set while a batch's leftovers are posted to go straight after input and drawing, rather than at the next tick.
        private bool _posted;

        /// <param name="interval">How often the items queued are taken in while it runs.</param>
        /// <param name="take">Takes in a batch.</param>
        /// <param name="budget">How long one batch may hold the UI thread; what's left goes in the next. None to take every item at once.</param>
        /// <param name="capacity">How many items <see cref="ReportAsync"/> lets wait to be taken in before it waits too; none for no limit.</param>
        public UiBatchQueue(TimeSpan interval, Action<IEnumerable<T>> take, TimeSpan? budget = null, int? capacity = null)
        {
            _take = take;
            _budget = budget ?? Timeout.InfiniteTimeSpan;
            if (capacity is { } places)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(places);
                _room = new SemaphoreSlim(places);
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            _timer.Tick += (_, _) => Take(_budget);
        }

        public bool IsEmpty => _items.IsEmpty;

        /// <summary>Queues <paramref name="value"/>, from any thread, however many are waiting.</summary>
        public void Report(T value) => _items.Enqueue((value, false));

        /// <summary>
        /// Queues <paramref name="value"/>, from any thread, once there's room: while as many items as the capacity
        /// wait to be taken in, it waits for some to be.
        /// </summary>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
        public async Task ReportAsync(T value, CancellationToken cancellationToken = default)
        {
            if (_room is null)
            {
                Report(value);
                return;
            }

            await _room.WaitAsync(cancellationToken).ConfigureAwait(false);
            _items.Enqueue((value, true));
        }

        /// <summary>Starts taking in the items queued, every interval.</summary>
        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Clear()
        {
            while (TryDequeue(out _))
            {
            }
        }

        /// <summary>Takes in every item queued now, in one batch, however long it takes.</summary>
        public void TakeAll() => Take(Timeout.InfiniteTimeSpan);

        /// <summary>Takes in every item queued, a batch at a time, letting input and drawing in between.</summary>
        public async Task TakeRestAsync()
        {
            while (!_items.IsEmpty)
                await Dispatcher.UIThread.InvokeAsync(() => Take(_budget), DispatcherPriority.Background);
        }

        private void Take(TimeSpan budget)
        {
            if (_items.IsEmpty)
                return;

            _take(Dequeue(budget));
            if (!_items.IsEmpty && budget != Timeout.InfiniteTimeSpan && !_posted)
            {
                _posted = true;
                Dispatcher.UIThread.Post(TakePosted, DispatcherPriority.Background);
            }
        }

        private void TakePosted()
        {
            _posted = false;
            Take(_budget);
        }

        private IEnumerable<T> Dequeue(TimeSpan budget)
        {
            long start = Stopwatch.GetTimestamp();
            while ((budget == Timeout.InfiniteTimeSpan || Stopwatch.GetElapsedTime(start) < budget) && TryDequeue(out var item))
                yield return item;
        }

        // Takes the next item, giving its place back to the room.
        private bool TryDequeue(out T item)
        {
            if (!_items.TryDequeue(out var queued))
            {
                item = default!;
                return false;
            }

            if (queued.Counted)
                _room!.Release();
            item = queued.Item;
            return true;
        }
    }
}
