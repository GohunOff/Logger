using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.M
{
    public sealed class QueueItem
    {
        public M.LogData Log { get; }
        public TaskCompletionSource<bool> FlushCompletion { get; }

        private QueueItem(
            M.LogData log,
            TaskCompletionSource<bool> flushCompletion)
        {
            Log = log;
            FlushCompletion = flushCompletion;
        }

        public static QueueItem CreateLog(M.LogData log)
        {
            return new QueueItem(log, null);
        }

        public static QueueItem CreateFlush(
            TaskCompletionSource<bool> completion)
        {
            return new QueueItem(null, completion);
        }
    }
}
