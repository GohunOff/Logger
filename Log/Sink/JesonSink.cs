using Kotrak.Log.E;
using Kotrak.Log.M;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kotrak.Log.Sink
{
    public sealed class JesonSink : I.ILogSink
    {
        private static long _droppedLogs;
        public static long DroppedLogs => Interlocked.Read(ref _droppedLogs);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public readonly struct LogEntry
        {
            public string ObjSource { get; }
            public string ObjDestination { get; }
            public string Msg { get; }
            public E.LogTypes LogType { get; }
            public string SourceType { get; }

            public DateTime Timestamp { get; }
            public string CorrelationId { get; }
            public string MachineName { get; }

            public string ApplicationName { get; }
            public string ThreadId { get; }

            public IReadOnlyDictionary<string, object> Properties { get; }

            public LogEntry(I.ILogData log)
            {
                ObjSource = log.ObjSource;
                ObjDestination = log.ObjDestination;
                Msg = log.Msg;
                LogType = log.LogType;
                SourceType = log.SourceType;

                Timestamp = log.Timestamp;
                CorrelationId = log.CorrelationId;
                MachineName = log.MachineName;

                ApplicationName = log.ApplicationName;
                ThreadId = log.ThreadId;

                Properties = log.Properties;
            }
        }

        private const int BatchSize = 100;

        private readonly Channel<I.ILogData> _channel;
        private readonly LogFileManager _fileManager;
        private readonly string _fileName;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;

        public E.LogTypes MinimumLevel { get; private set; }

        public JesonSink(LogFileManager fileManager, string fileName, E.LogTypes minimumLevel = E.LogTypes.Info)
        {
            _fileManager = fileManager;
            _fileName = fileName;
            MinimumLevel = minimumLevel;

            _channel = Channel.CreateBounded<I.ILogData>(new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            _worker = Task.Run(ProcessAsync);
        }


        public async Task WriteAsync(I.ILogData log)
        {
            if (log.LogType < MinimumLevel)
                return;

            if (log.LogType >= LogTypes.Error)
            {
                try
                {
                    await _channel.Writer.WriteAsync(log);
                }
                catch (ChannelClosedException)
                {
                    // normalne podczas zamykania loggera
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FileSink write error: {ex}");
                }
            }
            else
            {
              if (!_channel.Writer.TryWrite(log))
                 {
                    RegisterDroppedLog();
                }
            }
        }

        private static void RegisterDroppedLog()
        {
            var count = Interlocked.Increment(ref _droppedLogs);

            if (count % 1000 == 0)
            {
                Debug.WriteLine(
                    $"Logger JesonSink dropped {count} messages");
            }
        }


        private async Task ProcessAsync()
        {
            var batch = new List<I.ILogData>(BatchSize);
            var reader = _channel.Reader;

            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var log))
                    {
                        batch.Add(log);

                        if (batch.Count >= BatchSize)
                        {
                            await WriteBatchAsync(batch).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await WriteBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            // flush na shutdown
            while (reader.TryRead(out var log))
            {
                batch.Add(log);

                if (batch.Count >= BatchSize)
                {
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch).ConfigureAwait(false);
            }
        }

        private async Task WriteBatchAsync(List<I.ILogData> batch)
        {
            var filePath = _fileManager.GetLogFilePath(_fileName, DateTime.UtcNow);

            try
            {
                using (var fs = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    8192,
                    FileOptions.Asynchronous))
                using (var writer = new StreamWriter(fs, Encoding.UTF8))
                {
                    foreach (var log in batch)
                    {
                        var entry = new LogEntry(log);

                        var json = JsonSerializer.Serialize(entry, JsonOptions);

                        await writer.WriteLineAsync(json).ConfigureAwait(false);
                    }

                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FileSink write error: {ex}");
            }
        }

        public async Task FlushAsync()
        {
            _channel.Writer.TryComplete();
            try
            {
                await _worker.ConfigureAwait(false);
            }
            finally
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}