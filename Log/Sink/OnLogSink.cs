using MC.Log.M;
using MC.Log.Sink.Base;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MC.Log.Sink
{
    public sealed class OnLogSink : AsyncBatchSinkBase
    {
        public OnLogSink(
            E.LogTypes minimumLevel = E.LogTypes.Info,
            int batchSize = 100,
            int channelCapacity = 2000)
            : base(
                minimumLevel,
                batchSize,
                channelCapacity)
        {
        }

        protected override Task WriteBatchAsync(
            IReadOnlyList<QueueItem> batch)
        {
            foreach (QueueItem item in batch)
            {
                if (item.Log == null)
                    continue;

                try
                {
                    Logger.OnLogILogData(item.Log);
                }
                catch (Exception ex)
                {
                    // Błąd pojedynczego callbacku nie może zatrzymać workera.
                    Debug.WriteLine(
                        "OnLogSink callback error: " + ex);
                }
            }

            return Task.CompletedTask;
        }
    }
}
    
