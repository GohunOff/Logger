using MC.Log.M;
using MC.Log.I;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MC.Log.Sink.Base
{
    public abstract class AsyncBatchSinkBase : I.ILogSink, IAsyncDisposable
    {
        private const int DefaultBatchSize = 100;
        private const int DefaultChannelCapacity = 2000;

        private static long _droppedLogs;

        public static long DroppedLogs =>
            Interlocked.Read(ref _droppedLogs);

        private readonly Channel<QueueItem> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _worker;

        private readonly int _batchSize;

        private int _isDisposed;

        public E.LogTypes MinimumLevel { get; }

        protected AsyncBatchSinkBase(
            E.LogTypes minimumLevel = E.LogTypes.Info,
            int batchSize = DefaultBatchSize,
            int channelCapacity = DefaultChannelCapacity)
        {
            MinimumLevel = minimumLevel;
            _batchSize = batchSize;

            _cts = new CancellationTokenSource();

            _channel = Channel.CreateBounded<QueueItem>(
                new BoundedChannelOptions(channelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

            _worker = Task.Run(ProcessAsync);
        }
        /// <summary>
        /// Enqueues a log entry and asynchronously waits for available channel
        /// capacity when the sink is under backpressure.
        /// </summary>
        public async ValueTask<bool> WriteCriticalAsync(
            M.LogData log,
            CancellationToken token = default)
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return false;

            try
            {
                await _channel.Writer
                    .WriteAsync(
                        QueueItem.CreateLog(log),
                        token)
                    .ConfigureAwait(false);

                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }
        /// <summary>
        /// Enqueues a log entry using best-effort semantics.
        /// The call never waits when the channel is full and may drop the log.
        /// </summary>
        public bool TryWrite(LogData log)
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return false;

            var result = _channel.Writer.TryWrite(
                QueueItem.CreateLog(log));

            if (!result)
            {
                RegisterDroppedLog();
            }

            return result;
        }

        private static void RegisterDroppedLog()
        {
            var count = Interlocked.Increment(ref _droppedLogs);

            if (count % 1000 == 0)
            {
                Debug.WriteLine(
                    $"Logger sink dropped {count} messages");
            }
        }

        public async Task FlushAsync(
            CancellationToken token = default)
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return;

            var completion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await _channel.Writer
                    .WriteAsync(
                        QueueItem.CreateFlush(completion),
                        token)
                    .ConfigureAwait(false);

                await WaitWithCancellation(
                    completion.Task,
                    token)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Sink został zamknięty podczas FlushAsync.
            }
        }

        private async Task<bool> WriteBatchSafeAsync(
                    IReadOnlyList<QueueItem> batch)
        {
            if (batch.Count == 0)
                return true;

            try
            {
                await WriteBatchAsync(batch)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Error writing batch in {GetType().Name}: {ex}");
                return false;
            }
        }

        private async Task ProcessAsync()
        {
            var batch = new List<QueueItem>(_batchSize);
            var reader = _channel.Reader;

            try
            {
                while (await reader
                    .WaitToReadAsync(_cts.Token)
                    .ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        if (item.FlushCompletion != null)
                        {
                            var success = true;
                            if (batch.Count > 0)
                            {
                                success = await WriteBatchSafeAsync(batch)
                                    .ConfigureAwait(false);

                                batch.Clear();
                            }

                            item.FlushCompletion.TrySetResult(success);
                            continue;
                        }

                        batch.Add(item);

                        if (batch.Count >= _batchSize)
                        {
                            await WriteBatchSafeAsync(batch)
                                .ConfigureAwait(false);

                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await WriteBatchSafeAsync(batch)
                            .ConfigureAwait(false);

                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normalne zakończenie workera.
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Error in {GetType().Name} worker: {ex}");
            }

            // Ostatnia próba opróżnienia kolejki podczas shutdown.
            while (reader.TryRead(out var item))
            {
                if (item.FlushCompletion != null)
                {
                    if (batch.Count > 0)
                    {
                        await WriteBatchSafeAsync(batch)
                            .ConfigureAwait(false);

                        batch.Clear();
                    }

                    item.FlushCompletion.TrySetResult(true);
                    continue;
                }

                batch.Add(item);

                if (batch.Count >= _batchSize)
                {
                    await WriteBatchSafeAsync(batch)
                        .ConfigureAwait(false);

                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await WriteBatchSafeAsync(batch)
                    .ConfigureAwait(false);
            }
        }

        protected abstract Task WriteBatchAsync(
            IReadOnlyList<QueueItem> batch);

        private static async Task WaitWithCancellation(
            Task task,
            CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTask =
                Task.Delay(
                    Timeout.Infinite,
                    token);

            var completed =
                await Task.WhenAny(
                    task,
                    cancellationTask)
                .ConfigureAwait(false);

            if (completed == cancellationTask)
            {
                token.ThrowIfCancellationRequested();
            }

            await task.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref _isDisposed,
                    1) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();

            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Error during {GetType().Name} dispose: {ex}");
            }
            finally
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}
