using MC.Log.M;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MC.Log.Sink
{
    public sealed class ConsoleSink : Base.AsyncBatchSinkBase
    {
        public ConsoleSink(
            E.LogTypes minimumLevel = E.LogTypes.Info)
            : base(
                minimumLevel,
                batchSize: 1)
        {
        }

        protected override Task WriteBatchAsync(
            IReadOnlyList<QueueItem> batch)
        {
            foreach (var item in batch)
            {
                try
                {
                    var line = Logger.CreateLine(item.Log);
                    Console.WriteLine(line);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"ConsoleSink error: {ex}");
                }
            }

            return Task.CompletedTask;
        }
    }
}
