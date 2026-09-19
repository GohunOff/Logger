using System;
using System.Collections.Generic;
using System.Diagnostics;

#pragma warning disable 1591
namespace MC.Log.M
{
    public class LogData : I.ILogData
    {
        public string Message { get; }
        public E.LogTypes Level { get; }
        public DateTimeOffset Timestamp { get; } 

        public string Source { get; set; }
        public string Target { get; set; }

        public string LoggerName  { get; set; }
        public string CorrelationId { get; private set; }
        public string MachineName { get; private set; } = Environment.MachineName;
        public string ApplicationName { get; private set; } 
        public string ThreadId { get; private set; } 

        public IReadOnlyDictionary<string, object> Properties { get; set; }
        public Exception Exception { get; set; }

        public LogData( string message, E.LogTypes level)
        {
            Message = message;
            Level = level;

            Timestamp = DateTimeOffset.UtcNow;
            MachineName =
                Environment.MachineName;
            ApplicationName =
                AppDomain.CurrentDomain.FriendlyName;
            ThreadId =
                Environment.CurrentManagedThreadId
                .ToString();
            CorrelationId =
                Activity.Current?.Id
                ?? Guid.NewGuid().ToString();
            Properties = new Dictionary<string, object>();
        }

        public override string ToString()
        {
            return $"{Level.ToString().ToUpperInvariant()} | {Source ?? "[null]"} | {Target ?? "[null]"} | {Message ?? "[null]"}";
        }
    }
}
