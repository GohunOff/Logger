using System;
using System.Collections.Generic;

namespace MC.Log.I
{
    public interface ILogData
    {
        //data root
        string Message { get; }
        E.LogTypes Level { get; }
        DateTimeOffset Timestamp { get; }
        //data source
        string Source { get; set; }
        string Target { get; set; }
        //data context
        string LoggerName { get; set; }
        string CorrelationId { get; }
        string MachineName { get; }
        string ApplicationName { get; }
        string ThreadId { get; }
        //data properties
        IReadOnlyDictionary<string, object> Properties { get; }
        //exeption data
        Exception Exception { get; set; }
    }
}
