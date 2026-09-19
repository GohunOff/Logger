using MC.Log.Config;
using MC.Log.M;
using MC.Log.I;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MC.Log
{
    public static class Logger
    {
        private readonly static List<Sink.Base.AsyncBatchSinkBase> _sinkBase = new List<Sink.Base.AsyncBatchSinkBase>();

        private const int _criticalQueueWaitMilliseconds = 5000;

        //czas pruby zapisu krytycznego loga do sinka, po tym czasie log jest odrzucany i liczony jako dropped
        private const int CriticalSinkTimeoutMilliseconds = 2000;
        private static void InitSinkDefault()
        {
            _sinkBase.Clear();
            _sinkBase.Add(new Sink.ConsoleSink(E.LogTypes.Info));
            _sinkBase.Add(new Sink.FileSink(
                    _fileManager,
                    _fileName,
                    E.LogTypes.Info));
            _sinkBase.Add(new Sink.OnLogSink(
                    E.LogTypes.Info));
        }

        public static void InitSinkJeson()
        {
            _sinkBase.Clear();
            _sinkBase.Add(new Sink.ConsoleSink(E.LogTypes.Info));
            _sinkBase.Add(new Sink.FileSink(
                    _fileManager,
                    _fileName,
                    E.LogTypes.Info));
            _sinkBase.Add(new Sink.OnLogSink(
                    E.LogTypes.Info));
        }

        public static void InitSinkStandard()
        {
            _sinkBase.Clear();
            _sinkBase.Add(new Sink.ConsoleSink(E.LogTypes.Info));
            _sinkBase.Add(new Sink.FileSink(
                    _fileManager,
                    _fileName,
                    E.LogTypes.Info));
            _sinkBase.Add(new Sink.JsonSink(
                   _fileManager,
                   _jsonFileName,
                   E.LogTypes.Info));
            _sinkBase.Add(new Sink.OnLogSink(
                    E.LogTypes.Info));
        }

        #region Thread safety
        private static long _droppedLogs;
        public static long DroppedLogs => Interlocked.Read(ref _droppedLogs);
        private static  long _writtenLogs =0;
        public static long WrittenLogs => Interlocked.Read(ref _writtenLogs);
        private static long _failedWrites = 0;
        public static long FailedWrites => Interlocked.Read(ref _failedWrites);

        public static int QueueLength =>_mainQueue.Count;
        public static int QueueCapacityValue => _defaultQueueCapacity;

        public static bool IsInitialized => _loggerInitialization.Task.Status == TaskStatus.RanToCompletion;
        public static bool InitializationFailed => _loggerInitialization.Task.IsFaulted;

        private static long _lastErrorTicks;
        public static DateTime? LastErrorTime
        {
            get
            {
                var ticks =
                  Interlocked.Read(
                     ref _lastErrorTicks);
                return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        #endregion

        private const int _defaultQueueCapacity = 10000;
        private static DateTime _lastMaintenanceUtc;
        private static int _stateInitialized;
        private static int _state;
        private enum LoggerState
        {
            Empty = 0,
            Configuring = 1,
            Ready = 2,
            Stopping = 3,
            Failed = 4,
            Shutdown = 5
        }
        public static bool IsRunning => IsState(LoggerState.Ready);

        private static volatile ILogSink[] _sinks = Array.Empty<ILogSink>();
        private static volatile TemplateToken[] _tokens;

        private static readonly object _sinkLock = new object();
        private static readonly string _jsonFileName = "Log_Day.json";
        private static readonly string _fileName = "Log_Day";
        private static readonly LogFileManager _fileManager = new LogFileManager("Log");
        private static readonly object _maintenanceLock = new object();
        private static readonly string _stateFile = Path.Combine("Log", "logger.state");


        private static readonly BlockingCollection<M.LogData> _mainQueue =
          new BlockingCollection<M.LogData>(new ConcurrentQueue<M.LogData>(), _defaultQueueCapacity);

        private static readonly Lazy<Task> _worker =
                    new Lazy<Task>(
                        () => Task.Run(ProcessQueue),
                        LazyThreadSafetyMode.ExecutionAndPublication);

        private static void WorkStart() => _ = _worker.Value;

        public static event Action<I.ILogData> OnLog;

        private static readonly TaskCompletionSource<bool> _loggerInitialization =
                    new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);


        private static bool ShouldWaitForSink(E.LogTypes type)
        {
            return type >= E.LogTypes.Error;
        }

        private static bool ShouldWaitForQueue(E.LogTypes type)
        {
            return type >= E.LogTypes.Error;
        }

        private static async Task ProcessQueue()
        {
            foreach (var log in _mainQueue.GetConsumingEnumerable())
            {
                var sinks = Volatile.Read(ref _sinks);

                List<Task> criticalTasks = null;

                foreach (var sink in sinks)
                {
                    if (log.Level < sink.MinimumLevel)
                        continue;

                    if (ShouldWaitForSink(log.Level))
                    {
                        if (criticalTasks == null)
                        {
                            criticalTasks = new List<Task>(sinks.Length);
                        }

                        criticalTasks.Add(
                            ProcessSinkWriteAsync(
                                sink,
                                log));
                    }
                    else
                    {
                        ProcessSinkWriteBestEffort(
                            sink,
                            log);
                    }
                }

                if (criticalTasks != null &&
                    criticalTasks.Count > 0)
                {
                    await Task.WhenAll(criticalTasks)
                        .ConfigureAwait(false);
                }
            }
        }

        private static void ProcessSinkWriteBestEffort(
             ILogSink sink,
             M.LogData log)
        {
            if (sink == null || log == null)
                return;

            try
            {
                bool accepted = sink.TryWrite(log);

                if (accepted)
                {
                    Interlocked.Increment(
                        ref _writtenLogs);
                }
                else
                {
                    RegisterDroppedLog();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(
                    ref _failedWrites);

                Interlocked.Exchange(
                    ref _lastErrorTicks,
                    DateTime.UtcNow.Ticks);

                Debug.WriteLine(
                    "Logger sink write error: " +
                    sink.GetType().Name +
                    " - " +
                    ex);
            }
        }


        private static async Task ProcessSinkWriteAsync(
            ILogSink sink,
            M.LogData log)
        {
            if (sink == null || log == null)
                return;

            try
            {
                using (var cts = new CancellationTokenSource(
                    CriticalSinkTimeoutMilliseconds))
                {
                    bool accepted =
                        await sink.WriteCriticalAsync(
                            log,
                            cts.Token)
                        .ConfigureAwait(false);

                    if (accepted)
                    {
                        Interlocked.Increment(
                            ref _writtenLogs);
                    }
                    else
                    {
                        // Log nie został przyjęty przez kolejkę konkretnego sinka.
                        // Nie zwiększamy tutaj _droppedLogs,
                        // ponieważ jest to drop sinka.
                        Debug.WriteLine(
                            "Critical log rejected by sink: " +
                            sink.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout albo anulowanie oczekiwania na wolne miejsce
                // w kolejce konkretnego sinka.
                Interlocked.Increment(
                    ref _failedWrites);

                Interlocked.Exchange(
                    ref _lastErrorTicks,
                    DateTime.UtcNow.Ticks);

                Debug.WriteLine(
                    "Critical log timeout in sink: " +
                    sink.GetType().Name);
            }
            catch (Exception ex)
            {
                // Awaria konkretnego sinka nie może zatrzymać
                // pozostałych sinków.
                Interlocked.Increment(
                    ref _failedWrites);

                Interlocked.Exchange(
                    ref _lastErrorTicks,
                    DateTime.UtcNow.Ticks);

                Debug.WriteLine(
                    "Critical log write error in sink: " +
                    sink.GetType().Name +
                    " - " +
                    ex);
            }
        }

        


        #region struct TemplateToken
        private enum TokenType
        {
            Text,
            Timestamp,
            Source,
            Destination,
            LogType,
            SourceType,
            Message
        }
        private readonly struct TemplateToken
        {
            public TemplateToken(TokenType type, string text = null)
            {
                Type = type;
                Text = text;
            }

            public TokenType Type { get; }

            public string Text { get; }
        }
        #endregion
        #region ParseTemplate
        private static readonly Regex PlaceholderRegex =
            new Regex(@"\{(\w+)\}", RegexOptions.Compiled);
        private static TemplateToken[] ParseTemplate(string template)
        {
            var tokens = new List<TemplateToken>();

            int last = 0;

            foreach (Match match in PlaceholderRegex.Matches(template))
            {
                if (match.Index > last)
                    tokens.Add(new TemplateToken(
                        TokenType.Text,
                        template.Substring(last, match.Index - last)));

                switch (match.Groups[1].Value)
                {
                    case "Timestamp":
                        tokens.Add(new TemplateToken(TokenType.Timestamp));
                        break;

                    case "Source":
                        tokens.Add(new TemplateToken(TokenType.Source));
                        break;

                    case "Destination":
                        tokens.Add(new TemplateToken(TokenType.Destination));
                        break;

                    case "LogType":
                        tokens.Add(new TemplateToken(TokenType.LogType));
                        break;

                    case "SourceType":
                        tokens.Add(new TemplateToken(TokenType.SourceType));
                        break;

                    case "Message":
                    case "Msg":
                        tokens.Add(new TemplateToken(TokenType.Message));
                        break;

                    default:
                        tokens.Add(new TemplateToken(TokenType.Text, match.Value));
                        break;
                }

                last = match.Index + match.Length;
            }

            if (last < template.Length)
                tokens.Add(new TemplateToken(TokenType.Text, template.Substring(last)));

            return tokens.ToArray();
        }
        #endregion

        private static bool IsState(params LoggerState[] states)
        {
            var current = (LoggerState)Volatile.Read(ref _state);

            foreach (var state in states)
            {
                if (current == state)
                    return true;
            }

            return false;
        }
        private static bool TrySetState(LoggerState from,LoggerState to)
            {
                return Interlocked.CompareExchange(
                    ref _state,
                    (int)to,
                    (int)from) == (int)from;
            }

        #region Configure  
        private static void WaitForInitialization()
        {
            var task = _loggerInitialization.Task;

            if (!task.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException(
                    "Logger initialization exceeded 10 seconds.");
            }

            // Jeżeli Configure zakończył się błędem,
            // pokaż prawdziwy wyjątek
            task.GetAwaiter().GetResult();
        }
        private static void EnsureDefaultConfiguration()
        {
            var state = (LoggerState)Volatile.Read(ref _state);
            // Logger już gotowy
            if (state == LoggerState.Ready)
                return;
            // Ktoś inny właśnie inicjalizuje
            if (state == LoggerState.Configuring)
            {
                WaitForInitialization();
                return;
            }
            // Próba przejęcia inicjalizacji
            if (!TrySetState(
                LoggerState.Empty,
                LoggerState.Configuring))
            {
                WaitForInitialization();
                return;
            }

            try
            {
                if (_sinkBase.Count<=0) InitSinkDefault();
                foreach(var sink in _sinkBase)
                {
                    AddSink(sink);
                }
                
                var template =
                    "[{Timestamp}] SRC: {Source} -> DST: {Destination} |{LogType}|{SourceType}|{Message}";
                Volatile.Write(
                    ref _tokens,
                    ParseTemplate(template));

                FinishInitialization();
            }
            catch (Exception ex)
            {
                Volatile.Write(
                    ref _state,
                    (int)LoggerState.Failed);

                _loggerInitialization.TrySetException(ex);
                throw;
            }
        }
        public static void Configure(
        Action<LoggerBuilder> configure)
        {
            if (!TrySetState(
            LoggerState.Empty,
            LoggerState.Configuring))
                {
                    throw new InvalidOperationException(
                        "Logger already configured");
                }

            try
            {
                var builder = new LoggerBuilder();

                configure(builder);

                if (builder.Sinks.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Logger requires at least one sink.");
                }

                foreach (var sink in builder.Sinks)
                {
                    AddSink(sink);
                }

                TemplateToken[] tokens = null;

                if (!string.IsNullOrEmpty(builder.Template))
                {
                    tokens = ParseTemplate(builder.Template);
                }

                if (tokens != null)
                {
                    Volatile.Write(ref _tokens, tokens);
                }

                FinishInitialization();
            }
            catch (Exception ex)
            {
                Volatile.Write(
                   ref _state,
                   (int)LoggerState.Failed);
                _loggerInitialization.TrySetException(ex);
                throw;
            }
        }
        private static void FinishInitialization()
        {
            WorkStart();
            Volatile.Write(ref _state, (int)LoggerState.Ready);
            _loggerInitialization.TrySetResult(true);
        }
        #endregion

        #region LoggerState
        private static DateTime LoadState()
        {
            try
            {
                if (!File.Exists(_stateFile))
                    return CreateDefaultState();

                var line = File.ReadAllText(_stateFile).Trim();

                var parts = line.Split(';');
                if (parts.Length < 1)
                    return CreateDefaultState();
                if (DateTime.TryParseExact(parts[0], "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var result))
                    return result;
                else
                    return CreateDefaultState();
            }
            catch
            {
                return CreateDefaultState();
            }
        }
        private static void SaveState()
        {
            try
            {
                var line =
                    $"{_lastMaintenanceUtc:O};";

                string tempFile = _stateFile + ".tmp";

                File.WriteAllText(tempFile, line, Encoding.UTF8);

                if (File.Exists(_stateFile))
                {
                    File.Replace(tempFile, _stateFile, null);
                }
                else
                {
                    File.Move(tempFile, _stateFile);
                }

                //File.WriteAllText(_stateFile, line);
            }
            catch
            {
                // ignore
            }
        }
        private static DateTime CreateDefaultState()
        {
            return DateTime.UtcNow;
        }
        private static void EnsureStateLoaded()
        {
            if (Interlocked.Exchange(ref _stateInitialized, 1) == 1)
                return;

            _lastMaintenanceUtc = LoadState();
        }
        #endregion

        private static void AddSink(ILogSink sink)
        {
            if (sink == null) return;

            if (IsState(LoggerState.Ready,
                        LoggerState.Stopping,
                        LoggerState.Shutdown))
                {
                    throw new InvalidOperationException(
                        "Cannot add sink after logger start.");
                }

            lock (_sinkLock)
            {
                var current = _sinks;

                if (current.Contains(sink))
                    return;

                var updated = new ILogSink[current.Length + 1];

                Array.Copy(current, updated, current.Length);

                updated[current.Length] = sink;

                Volatile.Write(ref _sinks, updated);
            }
        }
        
        public static Task OnLogILogData(I.ILogData ev)
        {
            try
            {
                OnLog?.Invoke(ev);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in OnLog event handler: {ex}");
            }

            return Task.CompletedTask;
        }

        public static string CreateLine(I.ILogData log)
        {
            if (_tokens == null || _tokens.Length == 0)
                return log.Message ?? string.Empty;

            var sb = new StringBuilder(256);

            var tokens = Volatile.Read(ref _tokens);

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Text:
                        sb.Append(token.Text);
                        break;

                    case TokenType.Timestamp:
                        sb.Append(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        break;

                    case TokenType.Source:
                        sb.Append(log.Source ?? "null");
                        break;

                    case TokenType.Destination:
                        sb.Append(log.Target ?? "null");
                        break;

                    case TokenType.LogType:
                        sb.Append(log.Level);
                        break;

                    case TokenType.SourceType:
                        sb.Append(log.LoggerName );
                        break;

                    case TokenType.Message:
                        sb.Append(log.Message ?? "null");
                        break;
                }
            }

            return sb.ToString();
        }

        #region API
        public static void SetLog(I.ILogData logData)
        {
            if (logData == null)
                throw new ArgumentNullException(nameof(logData));
            Write(
                logData.Message,
                logData.Level,
                logData.LoggerName ,
                logData.Source,
                logData.Target,
                logData.Properties);
        }

        public static void Info(string msg,string source,string target,string loggerName = null)
            => Write(msg, E.LogTypes.Info,loggerName,source,null,target);
        public static void InfoPersistent(string msg, string source, string target, string loggerName = null)
            => Write(msg, E.LogTypes.InfoPersistent, loggerName, source, null, target);
        public static void Warning(string msg, string source, string target,string loggerName = null)
            => Write(msg, E.LogTypes.Warning, loggerName,source, null, target);
        public static void Error(string msg, string source, string target,string loggerName = null)
            => Write(msg, E.LogTypes.Error, loggerName,source, null, target);

        public static void Info(string msg,string loggerName = null)
            => Write(msg, E.LogTypes.Info, getCallerMethodName(),loggerName);
        public static void InfoPersistent(string msg, string loggerName = null)
            => Write(msg, E.LogTypes.InfoPersistent, getCallerMethodName(), loggerName);
        public static void Warning(string msg, string loggerName = null)
            => Write(msg, E.LogTypes.Warning, getCallerMethodName(),loggerName);
        public static void Error(string msg, string loggerName = null)
            => Write(msg, E.LogTypes.Error, getCallerMethodName(),loggerName);
        public static void Critical(Exception ex, string loggerName = null)
        {
            if (ex == null)
                return;

            Write(
                ex.Message,
                E.LogTypes.Critical,
                ex.Source,
                loggerName,
                string.Empty,
                exception: ex);
        }

        private static string getCallerMethodName()
        {
            var stackTrace = new StackTrace();
            var caller = stackTrace.GetFrame(2)?.GetMethod();
            if (caller == null)
                return "unknown";

            var declaringType = caller.DeclaringType;

            if (declaringType == null)
                return caller.Name;

            return $".{caller.Name}/{declaringType.FullName}";
        }

        public static void Info(
        string msg,
        params LogProperty[] properties) => Write(msg, E.LogTypes.Info, getCallerMethodName(), properties: ToDictionary(properties));
        public static void InfoPersistent(string msg, params LogProperty[] properties)
            => Write(msg, E.LogTypes.InfoPersistent, getCallerMethodName(), properties: ToDictionary(properties));
        public static void Warning(
        string msg,
        params LogProperty[] properties)
        => Write(msg, E.LogTypes.Warning, getCallerMethodName(), properties: ToDictionary(properties));
        public static void Error(
        string msg,
        params LogProperty[] properties)
            => Write(msg, E.LogTypes.Error, getCallerMethodName(), properties: ToDictionary(properties));
        public static void Critical(Exception ex, params LogProperty[] properties)
        {
            if (ex == null)
                return;
            Write(
                ex.Message,
                E.LogTypes.Critical,
                ex.Source,
                string.Empty,
                string.Empty,
                ToDictionary(properties),
                ex);
        }

        public static void Info(
            string msg,
            string loggerName,
            params LogProperty[] properties) 
            => Write(msg, E.LogTypes.Info, getCallerMethodName(), loggerName, properties: ToDictionary(properties));
        public static void InfoPersistento(
            string msg,
            string loggerName,
            params LogProperty[] properties)
            => Write(msg, E.LogTypes.InfoPersistent, getCallerMethodName(), loggerName, properties: ToDictionary(properties));
        public static void Warning(
            string msg,
            string loggerName,
            params LogProperty[] properties)
            => Write(msg, E.LogTypes.Warning, getCallerMethodName(), loggerName, properties: ToDictionary(properties));
        public static void Error(
            string msg,
            string loggerName,
            params LogProperty[] properties)
            => Write(msg, E.LogTypes.Error, getCallerMethodName(), loggerName, properties: ToDictionary(properties));
        public static void Critical(Exception ex, string loggerName, params LogProperty[] properties)
        {
            if (ex == null)
                return;
            Write(
                ex.Message,
                E.LogTypes.Critical,
                ex.Source,
                loggerName,
                string.Empty,
                ToDictionary(properties),
                ex);
        }

        private static IReadOnlyDictionary<string, object> ConvertProperties(object properties)
        {
            if (properties == null)
                return null;

            if (properties is IReadOnlyDictionary<string, object> dictionary)
                return dictionary;

            if (properties is IDictionary<string, object> dict)
                return new Dictionary<string, object>(dict);

            var result = new Dictionary<string, object>();

            foreach (var prop in properties.GetType().GetProperties())
            {
                try
                {
                    result[prop.Name] = prop.GetValue(properties);
                }
                catch
                {
                    // ignorujemy niedostępne właściwości
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, object>
        ToDictionary(LogProperty[] properties)
            {
                if (properties == null || properties.Length == 0)
                    return null;


                var result =
                    new Dictionary<string, object>();

                foreach (var p in properties)
                {
                    result[p.Name] = p.Value;
                }

                return result;
            }

        private const int MaxMessageLength = 8192;

        private static string NormalizeMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return msg;

            if (msg.Length <= MaxMessageLength)
                return msg;

            return msg.Substring(0, MaxMessageLength)
                   + "... [message truncated]";
        }

        public static void SetError(this Exception ex)
        {
            if (ex == null) return;
            Critical(ex);
        }
        //========================= Write log =========================
        private static void Write(string message, E.LogTypes level,
                                string source = null, string loggerName = null, 
                                string target = null, object properties = null,Exception exception = null)
        {

            if (IsState(LoggerState.Failed,
                LoggerState.Stopping,
                LoggerState.Shutdown))
                return;

            EnsureDefaultConfiguration();

            var now = DateTime.UtcNow;

            var log = new M.LogData(NormalizeMessage(message), level)
            {
                Source = source,
                Target = target,
                LoggerName = loggerName
            };


            if (properties != null)
            {
                log.Properties = ConvertProperties(properties);
            }

            if (exception != null)
            {
                log.Exception = exception;
            }

            EnsureStateLoaded();

            if ((now - _lastMaintenanceUtc) > TimeSpan.FromDays(1))
            {
                lock (_maintenanceLock)
                {
                    if ((now - _lastMaintenanceUtc) > TimeSpan.FromDays(1))
                    {
                        _lastMaintenanceUtc = now;
                        CheckOrCreateDirectory();
                        SaveState();
                    }
                }
            }

            try
            {
                bool accepted;

                if (ShouldWaitForQueue(log.Level))
                {
                    accepted = _mainQueue.TryAdd(
                        log,
                        _criticalQueueWaitMilliseconds);
                }
                else
                {
                    accepted = _mainQueue.TryAdd(log);
                }

                if (!accepted)
                {
                    RegisterDroppedLog();
                }

            }
            catch (InvalidOperationException)
            {
                // Queue została zamknięta podczas Shutdown.
                RegisterDroppedLog();
            }

            //try
            //{
            //    if (!_mainQueue.TryAdd(log))
            //    {
            //        RegisterDroppedLog();
            //    }
            //}
            //catch (InvalidOperationException)
            //{
            //    RegisterDroppedLog();
            //}
        }

        private static void RegisterDroppedLog()
        {
            var count = Interlocked.Increment(ref _droppedLogs);

            if (count % 1000 == 0)
            {
                Debug.WriteLine(
                    $"Logger dropped {count} messages");
            }
        }


        private static void CheckOrCreateDirectory()
        {
            //implementacja logiki tworzenia katalogów i podkatalogów na podstawie daty lub innych kryteriów
            _fileManager.CleanupOldLogs(); // Keep logs for the last 
            _fileManager.EnsureLogDirectories(DateTime.UtcNow);
        }
        #endregion

        public static async Task Shutdown()
        {
            if (!TrySetState(LoggerState.Ready, LoggerState.Stopping))
                return;

            try
            {
                _mainQueue.CompleteAdding();

                if (_worker.IsValueCreated)
                {
                    await _worker.Value.ConfigureAwait(false);
                }

                var sinks = Volatile.Read(ref _sinks);
                foreach (var sink in sinks)
                {
                    try
                    {
                        await sink.FlushAsync();

                        if (sink is IAsyncDisposable asyncDisposable)
                            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        else if (sink is IDisposable disposable)
                            disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
            }
            finally
            {
                Volatile.Write(
                    ref _state,
                    (int)LoggerState.Shutdown);
            }
        }

        public static async Task FlushAsync()
        {
            if (!IsState(LoggerState.Ready))
                return;
            var sinks = Volatile.Read(ref _sinks);
            foreach(var sink in sinks)
            {
                try
                {
                    await sink.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }
    }
}
