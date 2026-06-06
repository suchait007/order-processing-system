# KT-04: Production Logging with Serilog — Deep Dive
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers implementing logging in ASP.NET Core services  
**Status:** Living Document

---

## Table of Contents
1. [Why Serilog Over Built-in Logging?](#1-why-serilog-over-built-in-logging)
2. [The Packages — What Each One Does](#2-the-packages--what-each-one-does)
3. [Architecture — How Serilog Fits In](#3-architecture--how-serilog-fits-in)
4. [Program.cs Setup — Line by Line](#4-programcs-setup--line-by-line)
5. [Three Log Outputs (Sinks)](#5-three-log-outputs-sinks)
6. [Enrichers — Extra Context in Every Log](#6-enrichers--extra-context-in-every-log)
7. [Request Logging Middleware](#7-request-logging-middleware)
8. [Structured Logging — The Key Concept](#8-structured-logging--the-key-concept)
9. [Log Levels & Filtering](#9-log-levels--filtering)
10. [Controller Logging Patterns](#10-controller-logging-patterns)
11. [Log File Output Examples](#11-log-file-output-examples)
12. [Configuration via appsettings.json](#12-configuration-via-appsettingsjson)
13. [Bootstrap Logger — Catching Startup Failures](#13-bootstrap-logger--catching-startup-failures)
14. [Production Best Practices](#14-production-best-practices)

---

## 1. Why Serilog Over Built-in Logging?

| Feature | Built-in `Microsoft.Extensions.Logging` | Serilog |
|---|---|---|
| Structured logging | Basic (string templates only) | Full (properties preserved as data) |
| Thread ID in logs | ❌ Not available | ✅ Via `Enrich.WithThreadId()` |
| Machine/Process info | ❌ | ✅ Via enrichers |
| Multiple outputs | Console only (basic) | Console + File + JSON + Seq + Elasticsearch + 100+ sinks |
| JSON structured logs | ❌ | ✅ `CompactJsonFormatter` |
| Request logging | Noisy (6+ lines per request) | Clean (1 line per request) |
| Log file rotation | ❌ Manual | ✅ Built-in daily rotation with retention |
| Configuration | Code only | Code + `appsettings.json` |

**Bottom line:** Built-in logging is fine for development. Serilog is what you use in production.

---

## 2. The Packages — What Each One Does

```bash
dotnet add package Serilog.AspNetCore              # Core integration with ASP.NET Core
dotnet add package Serilog.Enrichers.Thread         # Adds ThreadId + ThreadName to every log
dotnet add package Serilog.Enrichers.Environment    # Adds MachineName + EnvironmentName
dotnet add package Serilog.Enrichers.Process        # Adds ProcessId + ProcessName
dotnet add package Serilog.Formatting.Compact       # JSON log formatter (machine-readable)
dotnet add package Serilog.Sinks.Console            # Write logs to console/stdout
dotnet add package Serilog.Sinks.File               # Write logs to rolling files
```

### What is a "Sink"?
A sink is a **destination** where logs are written. You can have multiple sinks active simultaneously:

```
Your Code → Serilog Pipeline → Enrichers add context → Sinks write output
                                                        ├── Console (human-readable)
                                                        ├── File (text, for grep/tail)
                                                        └── File (JSON, for log aggregation)
```

### What is an "Enricher"?
An enricher **adds properties** to every log event automatically. You don't have to pass ThreadId manually — the enricher injects it into every log.

---

## 3. Architecture — How Serilog Fits In

```
┌──────────────────────────────────────────────────────────────────┐
│                    YOUR APPLICATION                               │
│                                                                   │
│  Controller:                                                      │
│    _logger.LogInformation("Fetching store {StoreId}", id)        │
│         │                                                         │
│         ▼                                                         │
│  ILogger<T> (Microsoft interface)                                │
│         │                                                         │
│         ▼                                                         │
│  Serilog Provider (replaces Microsoft's default)                 │
│         │                                                         │
│         ▼                                                         │
│  ┌─── Enrichment Pipeline ───────────────────────────────┐       │
│  │  + ThreadId: 12                                        │       │
│  │  + ThreadName: ".NET TP Worker"                        │       │
│  │  + MachineName: "TVDPF5X45TJ"                         │       │
│  │  + ProcessId: 25952                                    │       │
│  │  + EnvironmentName: "Development"                      │       │
│  │  + SourceContext: "StoreApi.Controllers.StoresController" │    │
│  │  + RequestId: "0HNM2Q34OMNLU:00000001"                │       │
│  │  + RequestPath: "/api/stores/1"                        │       │
│  └───────────────────────────────────────────────────────┘       │
│         │                                                         │
│    ┌────┼────────────┬────────────────┐                          │
│    ▼    ▼            ▼                ▼                           │
│  Console  Text File   JSON File    (Seq/Elastic/etc.)            │
└──────────────────────────────────────────────────────────────────┘
```

**Key insight:** Your code uses `ILogger<T>` (Microsoft's interface) — it never references Serilog directly. This means you can swap Serilog for another provider without changing any controller code.

---

## 4. Program.cs Setup — Line by Line

### Step 1: Bootstrap Logger (catches startup crashes)

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();
```

**Why?** If your app crashes during startup (bad connection string, missing config), the normal logger isn't ready yet. The bootstrap logger catches these early failures.

### Step 2: Replace .NET Logging with Serilog

```csharp
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)  // Read settings from appsettings.json
    .ReadFrom.Services(services)                     // Access DI services if needed
    .Enrich.FromLogContext()                         // Per-request properties (RequestId, etc.)
    .Enrich.WithThreadId()                           // Thread ID
    .Enrich.WithThreadName()                         // Thread name (e.g., ".NET TP Worker")
    .Enrich.WithEnvironmentName()                    // "Development" / "Production"
    .Enrich.WithMachineName()                        // Server hostname
    .Enrich.WithProcessId()                          // OS process ID
    .Enrich.WithProcessName()                        // "StoreApi"
```

**`UseSerilog()`** tells ASP.NET Core: "Don't use your built-in logger. Route all `ILogger<T>` calls through Serilog instead."

### Step 3: Configure Sinks (where logs go)

```csharp
    // Sink 1: Console (human-readable for developers)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{ThreadId:00}] {SourceContext}{NewLine}      {Message:lj}{NewLine}{Exception}")
    
    // Sink 2: Text file (for grep/tail in production)
    .WriteTo.File(
        path: "logs/storeapi-.log",
        rollingInterval: RollingInterval.Day,     // New file every day
        retainedFileCountLimit: 7,                // Keep 7 days of logs
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Thread:{ThreadId}] [Process:{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    
    // Sink 3: JSON file (for log aggregation tools like ELK, Grafana Loki)
    .WriteTo.File(
        new CompactJsonFormatter(),
        path: "logs/storeapi-json-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
);
```

### Step 4: Wrap in try/catch/finally

```csharp
try
{
    Log.Information("Starting StoreApi application");
    // ... all app setup ...
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");  // Catch startup crashes
}
finally
{
    Log.CloseAndFlush();  // Ensure all buffered logs are written before exit
}
```

---

## 5. Three Log Outputs (Sinks)

### Sink 1: Console — For Developers

```
[12:35:12.456 INF] [12] StoreApi.Controllers.StoresController
      Fetching all stores
[12:35:12.850 INF] [10] Microsoft.EntityFrameworkCore.Database.Command
      Executed DbCommand (6ms) SELECT [s].[Id]... FROM [Stores] AS [s]
[12:35:12.875 INF] [10] Serilog.AspNetCore.RequestLoggingMiddleware
      HTTP GET /api/stores responded 200 in 496.724ms
```

**Reading this:**
- `12:35:12.456` — timestamp with milliseconds
- `INF` — log level (INF/WRN/ERR/DBG)
- `[12]` — **Thread ID** (this request ran on thread 12)
- `StoreApi.Controllers.StoresController` — which class logged this

### Sink 2: Text File — For Production Grep

```
2026-06-05 12:35:12.456 +05:30 [INF] [Thread:12] [Process:25952] [StoreApi.Controllers.StoresController] Fetching all stores
```

Includes full date, timezone, thread, process — everything for production debugging.

### Sink 3: JSON File — For Log Aggregation

```json
{
  "@t": "2026-06-05T07:05:12.4560000Z",
  "@mt": "Fetching store {StoreId}",
  "StoreId": 999,
  "SourceContext": "StoreApi.Controllers.StoresController",
  "ThreadId": 10,
  "ThreadName": ".NET TP Worker",
  "MachineName": "TVDPF5X45TJ",
  "ProcessId": 25952,
  "RequestId": "0HNM2Q34OMNLU:00000001",
  "RequestPath": "/api/stores/999"
}
```

**Why JSON?** Tools like Grafana Loki, Elasticsearch, Splunk, Datadog can ingest this directly. You can search by `StoreId`, `ThreadId`, `RequestPath`, etc. as structured fields — not string matching.

---

## 6. Enrichers — Extra Context in Every Log

| Enricher | What It Adds | Why You Need It |
|---|---|---|
| `FromLogContext()` | RequestId, RequestPath, ConnectionId | Correlate logs within a single HTTP request |
| `WithThreadId()` | ThreadId (e.g., `12`) | See which thread handled the work (essential for debugging thread starvation) |
| `WithThreadName()` | ThreadName (e.g., `.NET TP Worker`) | Distinguish worker threads from IOCP threads |
| `WithMachineName()` | MachineName (e.g., `TVDPF5X45TJ`) | In multi-server deployments, know which server logged it |
| `WithProcessId()` | ProcessId (e.g., `25952`) | Distinguish between multiple app instances on the same server |
| `WithProcessName()` | ProcessName (e.g., `StoreApi`) | In polyglot deployments, identify the service |
| `WithEnvironmentName()` | EnvironmentName (e.g., `Development`) | Distinguish dev/staging/prod logs |

### How Enrichers Work Internally

```
Your log call:
  _logger.LogInformation("Fetching store {StoreId}", 42);

What Serilog creates internally:
  LogEvent {
    Timestamp: 2026-06-05T12:35:12.456Z,
    Level: Information,
    MessageTemplate: "Fetching store {StoreId}",
    Properties: {
      StoreId: 42,                              ← from your code
      ThreadId: 12,                             ← from enricher
      MachineName: "TVDPF5X45TJ",              ← from enricher
      ProcessId: 25952,                         ← from enricher
      SourceContext: "StoresController",         ← from ILogger<T>
      RequestId: "0HNM2Q34OMNLU:00000001",     ← from LogContext
      RequestPath: "/api/stores/42",             ← from LogContext
    }
  }
```

Enrichers run on **every** log event. They're fast (just reading from Thread.CurrentThread, Environment, etc.).

---

## 7. Request Logging Middleware

### The Problem with Default ASP.NET Logging

Default logging produces **6+ lines per request**:

```
info: Microsoft.AspNetCore.Hosting     Request starting HTTP/1.1 GET /api/stores
info: Microsoft.AspNetCore.Routing     Executing endpoint 'StoresController.GetAll'
info: Microsoft.AspNetCore.Mvc         Executing action method StoresController.GetAll
info: Microsoft.AspNetCore.Mvc         Executed action StoresController.GetAll in 45ms
info: Microsoft.AspNetCore.Routing     Executed endpoint 'StoresController.GetAll'
info: Microsoft.AspNetCore.Hosting     Request finished HTTP/1.1 GET /api/stores - 200 45ms
```

### Serilog's Solution — One Line Per Request

```csharp
app.UseSerilogRequestLogging(options =>
{
    // Custom message template
    options.MessageTemplate = 
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms";
    
    // Dynamic log level based on response
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        if (ex != null) return LogEventLevel.Error;           // Exception → ERROR
        if (httpContext.Response.StatusCode >= 500) return LogEventLevel.Error;  // 5xx → ERROR
        if (httpContext.Response.StatusCode >= 400) return LogEventLevel.Warning; // 4xx → WARNING
        if (elapsed > 3000) return LogEventLevel.Warning;     // Slow request → WARNING
        return LogEventLevel.Information;                      // Normal → INFO
    };
    
    // Add extra properties to the request log
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
        diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress?.ToString());
    };
});
```

**Result:** One clean log line per request:

```
[12:35:12.875 INF] [10] HTTP GET /api/stores responded 200 in 496.724ms
[12:35:18.639 WRN] [10] HTTP GET /api/stores/999 responded 404 in 19.466ms
```

Notice: 404s are logged as `WRN`, 500s would be `ERR`.

---

## 8. Structured Logging — The Key Concept

### ❌ String Interpolation (BAD)

```csharp
_logger.LogInformation($"Fetching store {id}");
// Output: "Fetching store 42"
// The '42' is baked into the string — lost as searchable data
```

### ✅ Message Templates (GOOD)

```csharp
_logger.LogInformation("Fetching store {StoreId}", id);
// Output text: "Fetching store 42"  
// But internally: MessageTemplate="Fetching store {StoreId}", Properties={StoreId: 42}
```

**Why this matters:**

With structured logging, in your log aggregation tool you can query:
```
StoreId = 42              ← find all logs about store 42
StoreId > 100             ← find logs about high-numbered stores  
StatusCode >= 500         ← find all errors
Elapsed > 3000            ← find slow requests
ThreadId = 12             ← trace everything thread 12 did
RequestId = "0HNM2Q..."   ← trace a single request across all services
```

With string interpolation, you can only do `message CONTAINS "42"` — which matches everything with "42" in it.

---

## 9. Log Levels & Filtering

### Log Levels (lowest to highest)

| Level | Use For | Example |
|---|---|---|
| `Verbose` | Ultra-detailed (rarely used) | Loop iteration details |
| `Debug` | Internal diagnostics | EF Core connection open/close |
| `Information` | Normal operations | "Fetching store 42", "Created product 5" |
| `Warning` | Something unexpected but handled | "Store 999 not found", "Slow query (3.2s)" |
| `Error` | Something failed | Exception during DB query |
| `Fatal` | App is dying | "Application terminated unexpectedly" |

### Filtering by Source (appsettings.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information",
        "System": "Warning"
      }
    }
  }
}
```

This means:
- **Your code** (`StoreApi.*`): Log at `Information` and above
- **ASP.NET Core internals**: Only `Warning` and above (suppresses noisy request pipeline logs)
- **EF Core SQL commands**: Log at `Information` (so you see the actual SQL queries)
- **System libraries**: Only `Warning` and above

---

## 10. Controller Logging Patterns

### Pattern: Inject ILogger via DI

```csharp
public class StoresController : ControllerBase
{
    private readonly ILogger<StoresController> _logger;

    public StoresController(AppDbContext db, ILogger<StoresController> logger)
    {
        _db = db;
        _logger = logger;  // DI injects the correct logger with SourceContext set
    }
}
```

`ILogger<StoresController>` automatically sets `SourceContext` = `"StoreApi.Controllers.StoresController"` in every log from this controller.

### Pattern: Log at Boundaries

```csharp
[HttpGet("{id}")]
public async Task<ActionResult<StoreResponse>> GetById(int id)
{
    _logger.LogInformation("Fetching store {StoreId}", id);       // Entry

    var store = await _db.Stores...FirstOrDefaultAsync();

    if (store is null)
    {
        _logger.LogWarning("Store {StoreId} not found", id);      // Abnormal exit
        return NotFound();
    }

    return Ok(store);                                              // Normal exit (logged by middleware)
}
```

**Rules:**
- Log **entry** to the method with key parameters
- Log **abnormal exits** (not found, validation failure) as `Warning`
- Don't log normal exits — `UseSerilogRequestLogging` handles that with timing
- Log **exceptions** as `Error` (or let global exception handler do it)
- **Never** log sensitive data (passwords, tokens, PII)

---

## 11. Log File Output Examples

### Console Output (what you see when running)

```
[12:35:12.456 INF] [12] StoreApi.Controllers.StoresController
      Fetching all stores
[12:35:12.859 INF] [10] StoreApi.Controllers.StoresController
      Retrieved 3 stores
[12:35:12.875 INF] [10] Serilog.AspNetCore.RequestLoggingMiddleware
      HTTP GET /api/stores responded 200 in 496.724ms
[12:35:18.629 WRN] [10] StoreApi.Controllers.StoresController
      Store 999 not found
[12:35:18.639 WRN] [10] Serilog.AspNetCore.RequestLoggingMiddleware
      HTTP GET /api/stores/999 responded 404 in 19.466ms
```

### Text File (logs/storeapi-20260605.log)

```
2026-06-05 12:35:12.456 +05:30 [INF] [Thread:12] [Process:25952] [StoreApi.Controllers.StoresController] Fetching all stores
2026-06-05 12:35:12.859 +05:30 [INF] [Thread:10] [Process:25952] [StoreApi.Controllers.StoresController] Retrieved 3 stores
```

### JSON File (logs/storeapi-json-20260605.log)

```json
{
  "@t": "2026-06-05T07:05:18.629Z",
  "@mt": "Store {StoreId} not found",
  "@l": "Warning",
  "StoreId": 999,
  "SourceContext": "StoreApi.Controllers.StoresController",
  "ThreadId": 10,
  "ThreadName": ".NET TP Worker",
  "MachineName": "TVDPF5X45TJ",
  "ProcessId": 25952,
  "ProcessName": "StoreApi",
  "EnvironmentName": "Development",
  "RequestId": "0HNM2Q34OMNLU:00000001",
  "RequestPath": "/api/stores/999"
}
```

---

## 12. Configuration via appsettings.json

Serilog reads from `appsettings.json` via `ReadFrom.Configuration()`. This lets you change log levels **without recompiling**.

### Production (appsettings.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information",
        "System": "Warning"
      }
    }
  }
}
```

### Development Override (appsettings.Development.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information",
        "System": "Warning"
      }
    }
  }
}
```

In Development, `Debug` level lets you see EF Core connection open/close, query compilation, etc. In Production, `Information` keeps logs clean.

---

## 13. Bootstrap Logger — Catching Startup Failures

```csharp
// BEFORE the host is built — this logger catches startup crashes
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting StoreApi application");
    var builder = WebApplication.CreateBuilder(args);
    // ... setup that might fail (bad connection string, missing config) ...
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    // Without bootstrap logger, this crash would be SILENT
}
finally
{
    Log.CloseAndFlush();  // Flush any buffered logs before process exits
}
```

**Without this pattern:** If your `appsettings.json` has a bad connection string, the app crashes silently. With bootstrap logger, you see:

```
[12:34:42 FTL] Application terminated unexpectedly
System.Data.SqlClient.SqlException: Cannot open database "StoreApiDb"...
```

---

## 14. Production Best Practices

| Practice | Why |
|---|---|
| **Use structured logging** (`{StoreId}` not `$"{id}"`) | Enables searching by property in log tools |
| **Set `Microsoft.AspNetCore` to `Warning`** | Suppresses noisy framework logs (6 lines → 1 per request) |
| **Use `UseSerilogRequestLogging`** | Single log line per request with timing |
| **Log at method boundaries** | Entry + abnormal exits + timing |
| **Never log sensitive data** | No passwords, tokens, PII in logs |
| **Use daily rolling files with retention** | `retainedFileCountLimit: 7` auto-cleans old logs |
| **JSON sink for aggregation** | Ship to ELK/Grafana Loki/Datadog for centralized search |
| **Bootstrap logger + try/catch** | Never lose startup crash logs |
| **Short expiry on debug level** | Don't leave Debug on in prod — generates massive volume |
| **Include ThreadId** | Critical for diagnosing thread starvation and concurrency issues |
| **Include RequestId** | Correlate all logs from a single HTTP request |

---

## Thread Tracking — Reading the Logs

When you see this in logs:

```
[12:35:12.456 INF] [12] StoresController — Fetching all stores
[12:35:12.850 INF] [10] EF Core — Executed DbCommand (6ms)
[12:35:12.859 INF] [10] StoresController — Retrieved 3 stores
[12:35:12.875 INF] [10] RequestLogging — HTTP GET /api/stores 200 in 496ms
```

This tells you:
- Thread **12** started processing the request
- The `await ToListAsync()` released thread 12
- Thread **10** picked up the continuation after SQL completed
- Thread **10** finished the response

This is the **async/await thread switching** in action (explained in KT-02).

---

## References
- [Serilog.AspNetCore GitHub](https://github.com/serilog/serilog-aspnetcore)
- [Serilog Configuration](https://github.com/serilog/serilog-settings-configuration)
- [Serilog Enrichers](https://github.com/serilog/serilog-enrichers-thread)
- [Compact JSON Formatter](https://github.com/serilog/serilog-formatting-compact)
- [Serilog Best Practices by Nicholas Blumhardt](https://nblumhardt.com/2021/06/customize-serilog-text-output/)
