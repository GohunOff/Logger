using MC.Log.M;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.Sink
{
    internal class FileSink : Base.AsyncBatchSinkBase
    {
        private readonly LogFileManager _fileManager;
        private readonly string _fileName;

        public FileSink(
            LogFileManager fileManager,
            string fileName,
            E.LogTypes minimumLevel = E.LogTypes.Info)
            : base(minimumLevel,
                  batchSize: 200,
                  channelCapacity: 10000)
        {
            _fileManager = fileManager;
            _fileName = fileName;
        }

        protected override async Task WriteBatchAsync(
            IReadOnlyList<QueueItem> batch)
        {
            var filePath =
                _fileManager.GetLogFilePath(
                    _fileName,
                    DateTime.UtcNow);

            try
            {
                using (var fs = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read))
                using (var writer = new StreamWriter(
                    fs,
                    Encoding.UTF8))
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var line =
                            Logger.CreateLine(batch[i].Log);

                        await writer
                            .WriteLineAsync(line)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "FileSink write error: " + ex);
            }
        }
    }
}