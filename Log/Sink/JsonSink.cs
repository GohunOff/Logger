using MC.Log.M;
using MC.Log.Sink.Base;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MC.Log.Sink
{
    public sealed class JsonSink : AsyncBatchSinkBase
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
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

            public DateTimeOffset Timestamp { get; }
            public string CorrelationId { get; }
            public string MachineName { get; }

            public string ApplicationName { get; }
            public string ThreadId { get; }

            public IReadOnlyDictionary<string, object> Properties { get; }

            public LogEntry(I.ILogData log)
            {
                ObjSource = log.Source;
                ObjDestination = log.Target;
                Msg = log.Message;
                LogType = log.Level;
                SourceType = log.LoggerName;

                Timestamp = log.Timestamp;
                CorrelationId = log.CorrelationId;
                MachineName = log.MachineName;

                ApplicationName = log.ApplicationName;
                ThreadId = log.ThreadId;

                Properties = log.Properties;
            }
        }

        private readonly LogFileManager _fileManager;
        private readonly string _fileName;

        public JsonSink(
            LogFileManager fileManager,
            string fileName,
            E.LogTypes minimumLevel = E.LogTypes.Info)
            : base(
                minimumLevel,
                batchSize: 100,
                channelCapacity: 5000)
        {
            if (fileManager == null)
                throw new ArgumentNullException(nameof(fileManager));

            if (fileName == null)
                throw new ArgumentNullException(nameof(fileName));

            _fileManager = fileManager;
            _fileName = fileName;
        }

        protected override async Task WriteBatchAsync(
            IReadOnlyList<QueueItem> batch)
        {
            string filePath =
                _fileManager.GetLogFilePath(
                    _fileName,
                    DateTime.UtcNow);

            try
            {
                using (FileStream fs = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    8192,
                    FileOptions.Asynchronous))
                using (StreamWriter writer = new StreamWriter(
                    fs,
                    Encoding.UTF8))
                {
                    foreach (QueueItem item in batch)
                    {
                        if (item.Log == null)
                            continue;

                        LogEntry entry =
                            new LogEntry(item.Log);

                        string json =
                            JsonSerializer.Serialize(
                                entry,
                                JsonOptions);

                        await writer
                            .WriteLineAsync(json)
                            .ConfigureAwait(false);
                    }

                    await writer
                        .FlushAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "JsonSink write error: " + ex);
            }
        }
    }
}