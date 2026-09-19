# MC Logger

MC Logger is a lightweight asynchronous logging library for .NET Framework 4.8, designed for applications where logging should have minimal impact on application execution.

- The library focuses on:
- low overhead on application threads,
- asynchronous logging,
- independent queues for individual sinks,
- controlled backpressure,
- batch-based file I/O,
- resilience to individual sink failures,
- configurable log levels,
- structured logging,
- safe shutdown and queue draining,
- diagnostics and logger health monitoring,
- simple integration with existing .NET Framework applications.

The core design principle is:
Logging must not become a critical failure point for the application.

MC Logger therefore treats logging as a supporting service rather than a component on which application correctness depends.

## Features

- Asynchronous logging
-.NET Framework 4.8 support
- Bounded queues
- Separate queues per sink
- Best-effort logging for normal messages
- Timeout-controlled delivery for critical messages
- Batch processing
- Asynchronous file I/O
- Console logging
- File logging
- JSON logging
- Application callbacks through OnLog
- Structured log properties
- Configurable message templates
- Per-sink minimum log levels
- Global minimum log level
- Automatic caller information
- Maximum message length protection
- Runtime diagnostics counters
- Safe logger shutdown
- Queue flushing
- Log file archiving
- ZIP archive protection using temporary files
- Failure isolation between sinks
- Optional dependency bundling with Costura.Fody

---

## Installation

MC.Code.Logger is distributed as a NuGet package.

### NuGet Package Manager

Install the package using the NuGet Package Manager:

```powershell
Install-Package mcLogger
```

### .NET CLI

```bash
dotnet add package mcLogger
```

## Architecture

MC Logger uses two levels of asynchronous buffering.
```text
                         APPLICATION
                              |
                              v
                     Logger.Info(...)
                              |
                              v
                  +----------------------+
                  |      Main Queue      |
                  |      10,000 items    |
                  +----------------------+
                              |
                              v
                       Logger Worker
                              |
             +----------------+----------------+
             |                |                |
             v                v                v
       ConsoleSink        FileSink         JsonSink
             |                |                |
             v                v                v
          Channel          Channel          Channel
             |                |                |
             v                v                v
          Console            File             JSON
```
The main queue separates application code from the logger worker.
Each asynchronous sink then has its own bounded queue.
This means that a slow sink does not have to directly block other sinks.

For example:
```text
FileSink       -> slow / failed
ConsoleSink    -> operational
JsonSink       -> operational
OnLogSink      -> operational
```
A problem with FileSink should not automatically stop the other sinks or the main logger worker.

## Main Logger Queue
The logger uses:
```csharp
BlockingCollection<LogData>
```

The default capacity is:
10,000 items
The queue provides a buffer between application threads and the logger worker.
```text
Application threads
        |
        v
+-------------------+
|    Main Queue     |
|    10,000 items   |
+-------------------+
        |
        v
 Logger Worker
        |
        v
     Sinks
```

The purpose of this queue is to absorb short-term logging bursts without immediately performing I/O on application threads.
The queue is bounded intentionally.
An unlimited queue could allow a logging overload to turn into uncontrolled memory consumption.

Asynchronous Sinks
Each asynchronous sink is based on:

AsyncBatchSinkBase
The implementation uses:
```csharp
System.Threading.Channels.Channel<T>
```

with bounded capacity.

Conceptually:
```text
Logger Worker
     |
     +----------------------+
     |                      |
     v                      v
ConsoleSink             FileSink
     |                      |
     v                      v
 Channel                 Channel
     |                      |
     v                      v
 Console                 File I/O
```

Each sink therefore has an independent processing pipeline.
This is particularly important when one sink performs slow I/O.
Batch Processing
Sinks can process messages in batches.

For example:

BatchSize = 100;

allows a sink to collect up to 100 log entries before performing the corresponding operation.
For file logging, batching can significantly reduce I/O overhead.

Instead of:
```text
log 1 -> open -> write -> close
log 2 -> open -> write -> close
log 3 -> open -> write -> close
...
```

the sink can process:
```text
100 logs
    |
    v
one batch
    |
    v
file operation
    |
    v
write
    |
    v
flush
```
The exact I/O behavior depends on the sink implementation and configuration.

Backpressure
MC Logger uses bounded queues to provide controlled backpressure.

There are two main delivery modes.

Best-Effort Logging
Normal log messages use a non-blocking operation such as:
```text
TryWrite()
```
Conceptually:
```text
TryWrite()
    |
    +-- space available --> accepted
    |
    +-- queue full -------> dropped
```

The application thread does not wait for a slow sink to become available.
This behavior is intentional.
A logging subsystem should not freeze the application because a disk, console, or callback cannot keep up with the generated log volume.

Critical Logging
Higher-priority messages such as:

Error
Critical

can use:

WriteCriticalAsync()

Critical delivery may wait for queue capacity.

The wait is bounded by:

CriticalSinkTimeoutMilliseconds = 2000

Therefore the logger can give important messages a better opportunity to reach a sink without waiting indefinitely.

Conceptually:
```text
Critical log
     |
     v
Queue has capacity?
     |
   +---+
   |   |
  YES  NO
   |   |
   v   v
write wait
       |
       v
    timeout?
     /   \
   no     yes
   |       |
 write    failed
```

This is a trade-off between delivery reliability and application responsiveness.

Failure Isolation
A failure in one sink should not terminate the complete logging pipeline.

For example:
```text
FileSink     -> ERROR
ConsoleSink  -> OK
JsonSink     -> OK
OnLogSink    -> OK
```

The logger continues processing the operational sinks.

Typical sink failures include:

- file access errors,
- directory errors,
- I/O failures,
- serialization errors,
- callback exceptions,
- closed sinks,
- queue capacity exhaustion,
- timeout conditions.

Infrastructure errors are handled internally rather than being propagated as normal application exceptions.

Fail-Safe Logging
One of the core design principles is:

A logging failure should not become an application failure.

For example, the logger should not normally execute:

throw ex;

because a disk failure or serialization error occurred while writing a log.
Instead, infrastructure failures are handled internally and can be exposed through diagnostic counters and timestamps.

For example:
```csharp
Debug.WriteLine(
    $"Error writing batch: {ex}");
```

This prevents a secondary logging problem from becoming the cause of an application crash.

Diagnostic Counters
MC Logger exposes runtime information that can be used to monitor the health of the logging infrastructure.

Examples include:
```csharp
Logger.DroppedLogs
Logger.WrittenLogs
Logger.FailedWrites
Logger.QueueLength
Logger.QueueCapacityValue
Logger.LastErrorTime

Example:

if (Logger.DroppedLogs > 0)
{
    // The logger has experienced overload.
}

Another example:

Debug.WriteLine(
    $"Logger queue: {Logger.QueueLength}/" +
    $"{Logger.QueueCapacityValue}");

Debug.WriteLine(
    $"Written: {Logger.WrittenLogs}");

Debug.WriteLine(
    $"Dropped: {Logger.DroppedLogs}");

Debug.WriteLine(
    $"Failed: {Logger.FailedWrites}");
```

These counters make it possible to monitor the logger without having to inspect the generated log files.

Main Queue vs Sink Queue Drops
Dropped messages can originate at different stages of the pipeline.

Conceptually:
```text
Application
     |
     v
Main Queue
     |
     v
Logger Worker
     |
     +----> Sink Queue
```

This allows diagnostics to distinguish between:
overload of the main logger queue,
overload of a particular sink queue.

This distinction is useful when investigating throughput problems.

For example, the main queue may be healthy while a specific FileSink is experiencing backpressure.

ConsoleSink
ConsoleSink is intended primarily for development and runtime diagnostics.

Example:

cfg.AddConsole();

Console output is performed by the sink worker rather than directly by the application thread.

Conceptually:
```text
Application
     |
     v
Logger
     |
     v
ConsoleSink queue
     |
     v
Console.WriteLine()
```

The default batch size is:

1

which is appropriate when near-immediate console output is desired.

FileSink
FileSink provides standard text-based file logging.

Example:
```csharp
.AddFile(file =>
{
    file.Directory = "Log";
    file.FileName = "Log_Day";
    file.BatchSize = 100;
})
```

A typical directory structure is:
```text
Log/
└── 2026/
    └── 9/
        ├── Log_Day_18.txt
        ├── Log_Day_19.txt
        └── ...
```

The exact path is determined by LogFileManager.

Batch processing is especially useful for file logging because it reduces the number of individual I/O operations.

JSON Logging
JsonSink provides structured JSON logging.

Example:

.AddJson(...)

Depending on the configured model, JSON records can contain fields such as:
- Source
- Destination
- Message
- LogType
- SourceType
- Timestamp
- CorrelationId
- MachineName
- ApplicationName
- ThreadId
- Properties

JSON output is useful when logs are later processed by:
- monitoring systems,
- log aggregation systems,
- JSON parsers,
- analytical tools,
- custom processing pipelines.

Structured Logging
Log entries can contain additional structured properties.

Example:
```csharp
Logger.Info(
    "User logged in",
    new LogProperty("UserId", userId),
    new LogProperty("Role", role));
```

Instead of embedding all contextual information into a single message string, applications can attach structured values to the log entry.
This is particularly useful with JSON output.

Example conceptual record:
```csharp
{
  "Message": "User logged in",
  "Properties": {
    "UserId": 1234,
    "Role": "Administrator"
  }
}
```

Templates
MC Logger supports configurable output templates.

Example:
```csharp
.TemplateIn(
    "[{Timestamp}] {LogType} {Message}")
```

Supported placeholders include:
```csharp
{Timestamp}
{Source}
{Destination}
{LogType}
{SourceType}
{Message}
{Msg}
```

Example output:
```text
[2026-09-19 12:32:14.123] Error Database connection failed
```

The template is parsed during configuration rather than for every individual log entry.
This reduces repeated template-processing overhead during normal logging.

Maximum Message Length
MC Logger limits the maximum message length.

Default:

MaxMessageLength = 8192

Very large messages are truncated.

Conceptually:

[first 8192 characters] ... [message truncated]

This provides protection against accidentally placing extremely large strings into the logging pipeline.
It also limits the amount of memory consumed by individual log messages.
```csharp
Log Levels
MC Logger uses:

E.LogTypes

Typical API calls include:

Logger.Info(...);
Logger.InfoPersistent(...);
Logger.Warning(...);
Logger.Error(...);
Logger.Critical(...);
```

A global minimum level can be configured:
```csharp
cfg.MinimumLevelIn(E.LogTypes.Info);
```

Individual sinks can also have their own minimum level.

For example:
```text
Console -> Info
File    -> Info
JSON    -> Warning
```

This allows different sinks to receive different subsets of the application's logs.

Critical Exceptions
Exceptions can be logged directly.

Example:
```csharp
Logger.Critical(exception);
```

Additional structured context can also be provided:
```csharp
Logger.Critical(
    exception,
    new LogProperty(
        "Operation",
        "DatabaseUpdate"));
```

The exception is stored in:
```csharp
LogData.Exception
```
allowing sinks to decide how the exception should be represented.

Caller Information
MC Logger can automatically determine information about the calling method.

For example:
```csharp
Logger.Info("Starting operation");
```
can provide source information similar to:

MethodName/Namespace.Type

This avoids requiring applications to manually specify the calling method for common logging scenarios.

OnLog Callback
Applications can subscribe to logger events through OnLog.

Example:
```csharp
Logger.OnLog += log =>
{
    // Custom application handling
};
```
The callback is processed through OnLogSink rather than being executed directly as part of the application's logger call path.

Exceptions thrown by the callback are isolated from the rest of the logging pipeline.

## Configuration
A typical configuration looks like this:
```csharp
Logger.Configure(cfg =>
{
    cfg.MinimumLevelIn(E.LogTypes.Info)
       .TemplateIn(
           "[{Timestamp}] {LogType} {Message}")
       .AddConsole()
       .AddFile(file =>
       {
           file.Directory = "Log";
           file.FileName = "Log_Day";
           file.BatchSize = 100;
       })
       .AddOnLog();
});
```

A logger configuration requires at least one sink.

A production configuration may additionally include JSON logging:
```csharp
Logger.Configure(cfg =>
{
    cfg.MinimumLevelIn(E.LogTypes.Info)
       .TemplateIn(
           "[{Timestamp}] {LogType} {Message}")
       .AddConsole()
       .AddFile(file =>
       {
           file.Directory = "Log";
           file.FileName = "Log_Day";
           file.BatchSize = 100;
       })
       .AddJson(...)
       .AddOnLog();
});
```

## Flush
MC Logger provides:
```csharp
await Logger.FlushAsync();
```

FlushAsync() is intended to allow pending log entries to be processed without shutting down the complete logger.

Typical use cases include:
- controlled application restart,
- component restart,
- configuration changes,
- critical application operations,
- controlled application shutdown.

The exact guarantees provided by FlushAsync() depend on the implementation of the current sink lifecycle and should be verified against the current version of the library.

## Shutdown
The recommended application shutdown sequence is:

await Logger.Shutdown();

The intended shutdown process is:
```text
Stop accepting new logs
        |
        v
Close main queue
        |
        v
Process remaining messages
        |
        v
Flush sinks
        |
        v
Dispose sinks
        |
        v
Release resources
```

The purpose is to avoid abandoning messages that are already queued when the application terminates.
Applications should call Shutdown() during controlled application termination.

## Log Archiving
LogFileManager works together with LogArchiver to archive older log files.

Archives are created using a temporary file:

archive.zip.tmp

and then replaced with:

archive.zip

Conceptually:
```text
archive.zip.tmp
       |
       v
 archive.zip
```

Source log files are removed only after the archive operation has completed successfully.
This reduces the risk of losing the only copy of a log file when archive creation fails.

Safe Archiving
The archive mechanism is designed to:
- avoid archiving the currently active log file,
- create ZIP archives through a temporary file,
- preserve existing archive contents where applicable,
- remove source files only after successful archive creation,
- handle file access errors,
- handle I/O errors,
- clean up temporary files after failed operations.

Typical structure:
```text
Log/
└── 2026/
    └── 9/
        ├── Log_Day_18.txt
        ├── Log_Day_19.txt
        └── 9.zip
```

## Performance Model
The main performance principle is:

I/O should not be part of the critical path of the application logging call.

The architecture uses several mechanisms to achieve this:
- bounded main queue,
- independent sink queues,
- Channel<T>,
- asynchronous workers,
- batch processing,
- asynchronous I/O,
- TryWrite() for best-effort messages,
- bounded waiting for critical messages,
- pre-parsed templates,
- maximum message length,
- internal exception handling.

The application thread primarily performs log object creation and queue submission.
Physical I/O is handled by background workers.

Overload Behavior
MC Logger does not guarantee that every log entry will always be persisted.

Instead, the system is designed to preserve application responsiveness during extreme logging pressure.

Conceptually:
```text
Log production rate
        >
Logger throughput
        |
        v
Queue reaches capacity
        |
        v
Best-effort logs may be dropped
```

## This is intentional.

The design prioritizes:
- Keeping the application responsive
- Avoiding indefinite blocking
- Processing logs asynchronously
- Absorbing short-term bursts
- Using best-effort delivery for normal messages
- Providing bounded waiting for critical messages
- Avoiding infinite waits

This makes the logger suitable for applications where logging should not become a dependency for normal application operation.

Example Usage
Basic logging:
```csharp
Logger.Info("Application started");
```

Structured warning:
```csharp
Logger.Warning(
    "Configuration file is missing",
    new LogProperty("File", "config.json"));
```

Exception logging:
```csharp
try
{
    ExecuteOperation();
}
catch (Exception ex)
{
    Logger.Critical(
        ex,
        new LogProperty(
            "Operation",
            "ExecuteOperation"));
}
```

Controlled shutdown:

await Logger.Shutdown();

## Monitoring
Production applications should monitor the health of the logging infrastructure.

Useful metrics include:
```text
Logger.QueueLength
Logger.QueueCapacityValue
Logger.DroppedLogs
Logger.WrittenLogs
Logger.FailedWrites
Logger.LastErrorTime
```
Example:
```csharp
Debug.WriteLine(
    $"Logger queue: {Logger.QueueLength}/" +
    $"{Logger.QueueCapacityValue}");

Debug.WriteLine(
    $"Written: {Logger.WrittenLogs}");

Debug.WriteLine(
    $"Dropped: {Logger.DroppedLogs}");

Debug.WriteLine(
    $"Failed: {Logger.FailedWrites}");
```

These values can be integrated into an application's own monitoring or health-check infrastructure.

Recommended Production Configuration
A typical production configuration may look like:
```csharp
Logger.Configure(cfg =>
{
    cfg.MinimumLevelIn(E.LogTypes.Info)
       .TemplateIn(
           "[{Timestamp}] {LogType} {Message}")
       .AddConsole()
       .AddFile(file =>
       {
           file.Directory = "Log";
           file.FileName = "Log_Day";
           file.BatchSize = 100;
       })
       .AddJson(...)
       .AddOnLog();
});
```

For production deployments, pay particular attention to:
- DroppedLogs
- FailedWrites
- QueueLength
- LastErrorTime

A non-zero DroppedLogs counter can indicate that the logging system has reached its configured capacity.
A growing FailedWrites counter can indicate persistent infrastructure problems.

.NET Framework 4.8

## MC Logger — Target Platform
.NET Framework 4.8

The implementation uses modern asynchronous APIs through compatible NuGet packages, including:
- System.Threading.Channels
- System.Text.Json
- Microsoft.Bcl.AsyncInterfaces
- System.Memory
- System.Buffers
- System.Threading.Tasks.Extensions

The dependencies listed above are embedded into the resulting mcLogger.dll using Costura.Fody. As a result, they do not need to be deployed as separate DLL files with the application.
The NuGet packages are still used by the project during the restore and build processes. However, after the application is built, their dependencies are bundled into mcLogger.dll according to the Costura.Fody configuration.

As a result, a typical deployment may contain only:
```test
Application.exe
mcLogger.dll
```

The exact versions of the packages used should be taken from the project's package configuration rather than assumed from this documentation.
Before production deployment, the final Release output should be verified and the application should be run in the target .NET Framework 4.8 environment to confirm that mcLogger.dll and its embedded dependencies work correctly.

## Design Philosophy

MC Logger intentionally follows a fail-safe design.
The logging system should not become more dangerous than the problem it is trying to report.

For normal application logging:
```text
Application
     |
     v
Logger
     |
     v
Queue
     |
     v
Background processing
     |
     v
I/O
```

rather than:
```text
Application
     |
     v
Logger
     |
     v
File I/O
     |
     v
Application continues
```

The first model isolates application execution from slow or failing infrastructure.

## What MC Logger Guarantees
- The design aims to provide:
- asynchronous processing,
- bounded memory usage,
- independent sink processing,
- controlled backpressure,
- failure isolation,
- bounded waiting for critical messages,
- internal handling of logging infrastructure errors,
- configurable filtering,
- structured logging,
- controlled shutdown.

## What MC Logger Does Not Guarantee
MC Logger is not designed to guarantee lossless logging under unlimited load.

In particular:
- best-effort messages may be dropped,
- bounded queues can become full,
- a failing sink may fail to persist messages,
- critical message delivery can fail after its timeout,
- process termination can prevent queued messages from being written,
- logging throughput is ultimately limited by the configured sinks and underlying infrastructure.

Applications requiring transactional or lossless audit records should not treat MC Logger as a transactional persistence mechanism.

## Operational Characteristics
The intended behavior can be summarized as follows:
- Situation	Expected behavior
- Normal log	Asynchronous queueing
- Temporary logging burst	Buffered by bounded queues
- Main queue full	Best-effort messages may be dropped
- Sink queue full	Best-effort messages may be dropped
- Error/Critical	May wait for capacity
- Critical timeout	Delivery attempt fails without indefinite waiting
- File sink failure	Other sinks continue
- Console failure	Other sinks continue
- JSON serialization failure	Failure handled internally
- OnLog callback failure	Failure isolated
- Application shutdown	Queues are drained according to shutdown lifecycle
- Archive failure	Temporary archive can be cleaned up without deleting source first

## Typical Architecture
```text
                         APPLICATION
                              |
                              v
                    Logger.Write(...)
                              |
                              v
                 +----------------------+
                 |      Main Queue      |
                 |      10,000          |
                 +----------------------+
                              |
                              v
                       Logger Worker
                              |
          +-------------------+-------------------+
          |                   |                   |
          v                   v                   v
     ConsoleSink          FileSink            JsonSink
          |                   |                   |
          v                   v                   v
       Channel             Channel             Channel
          |                   |                   |
          v                   v                   v
       Console               File                JSON

                              |
                              v
                         OnLogSink
                              |
                              v
                        Application
```

The important architectural separation is:
```text
Log production
      !=
Log persistence
```
Application code produces log records.

Background workers are responsible for delivering those records to the configured sinks.

## Summary
MC Logger is an asynchronous logging infrastructure for .NET Framework 4.8 designed around a simple principle:
Logging should support the application, not become a dependency that can stop it.

Its architecture combines:
- bounded queues,
- asynchronous workers,
- independent sink pipelines,
- batch processing,
- controlled backpressure,
- structured logging,
- configurable filtering,
- failure isolation,
- diagnostic counters,
- safe shutdown,
- log archiving.

The result is a logging subsystem intended for applications that generate significant log volumes while requiring predictable application behavior when logging infrastructure becomes slow, unavailable, or overloaded.

MC Logger is particularly suited to:
- Windows Services,
- server applications,
- industrial applications,
- automation systems,
- long-running processes,
- 24/7 applications,
- applications generating high volumes of diagnostic logs.

The key design trade-off is deliberate:
```text
Application availability
        >
Individual best-effort log delivery
```
The logger therefore prefers controlled message loss over indefinite blocking or allowing logging infrastructure failures to propagate into the application.
---

## Requirements

- .NET Framework 4.8
- C# or another compatible .NET development environment

MC.Code.Logger does not require additional external dependencies.

---

## License

This project is licensed under the MIT License.

See [`LICENSE.txt`](LICENSE.txt) for the complete license text.

Copyright (c) 2026 gohunoff@gmail.com

**Author:** Przemysław Załuska  
**Email:** gohunoff@gmail.com

**GitHub:**  [MC.Code.Logger on GitHub]https://github.com/GohunOff/Logger

MC.Code.Logger is developed and maintained by the author.

If you find a problem, have a feature request, or would like to contribute, please open an issue in the GitHub repository.