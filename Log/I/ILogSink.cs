using MC.Log.M;
using MC.Log.E;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable 1591
namespace MC.Log.I
{
    public interface ILogSink
    {
        LogTypes MinimumLevel { get; }
        bool TryWrite(LogData log);
        ValueTask<bool> WriteCriticalAsync(LogData log, CancellationToken token = default);
        Task FlushAsync(CancellationToken token = default);
    }
}

