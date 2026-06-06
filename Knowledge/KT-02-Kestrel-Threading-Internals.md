# KT-02: Kestrel Internals — Request Mapping & Threading Deep Dive
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers wanting to understand ASP.NET Core performance internals  
**Status:** Living Document

---

## Table of Contents
1. [Network Layer — Before Kestrel](#1-network-layer--before-kestrel)
2. [Kestrel Architecture — Three Layers](#2-kestrel-architecture--three-layers)
3. [System.IO.Pipelines — The Secret Weapon](#3-systemiopipelines--the-secret-weapon)
4. [Threading Model — The Deep Part](#4-threading-model--the-deep-part)
5. [What Happens Thread-by-Thread](#5-what-happens-thread-by-thread)
6. [async/await — Why It Matters](#6-asyncawait--why-it-matters)
7. [Concurrency — How Many Requests](#7-concurrency--how-many-requests)
8. [Thread Starvation — The Danger](#8-thread-starvation--the-danger)
9. [Connection Management](#9-connection-management)
10. [Full Picture — Packet to Response](#10-full-picture--packet-to-response)

---

## 1. Network Layer — Before Kestrel

```
Client sends HTTP request
    ↓
OS Network Stack (TCP/IP)
    ↓
OS accepts TCP connection on port 5116
    ↓
Data arrives in OS kernel buffer (socket)
    ↓
Kestrel is notified via I/O completion
```

Kestrel uses **asynchronous I/O** at the OS level:

| OS | Mechanism | Description |
|---|---|---|
| **Windows** | `IO Completion Ports (IOCP)` | The fastest async I/O mechanism on Windows |
| **Linux** | `epoll` | Event-driven I/O notification |
| **macOS** | `kqueue` | BSD-style event notification |

These are **kernel-level event notification systems**. Kestrel doesn't poll — the OS **pushes** events to it.

---

## 2. Kestrel Architecture — Three Layers

```
┌─────────────────────────────────────────────────┐
│                  KESTREL                         │
│                                                  │
│  ┌──────────────────────────────────────────┐    │
│  │  Transport Layer (Connections)            │    │
│  │  - Listens on socket                      │    │
│  │  - Accepts TCP connections                │    │
│  │  - Reads/writes raw bytes                 │    │
│  │  - Uses System.IO.Pipelines               │    │
│  └──────────────┬───────────────────────────┘    │
│                 │                                 │
│  ┌──────────────▼───────────────────────────┐    │
│  │  Protocol Layer (HTTP Parsing)            │    │
│  │  - Parses HTTP/1.1 or HTTP/2 or HTTP/3    │    │
│  │  - Headers, method, URL, body             │    │
│  │  - Creates HttpContext                     │    │
│  └──────────────┬───────────────────────────┘    │
│                 │                                 │
│  ┌──────────────▼───────────────────────────┐    │
│  │  Application Layer (Middleware)            │    │
│  │  - Hands HttpContext to middleware pipeline│    │
│  │  - Eventually reaches your controller      │    │
│  └──────────────────────────────────────────┘    │
└─────────────────────────────────────────────────┘
```

---

## 3. System.IO.Pipelines — The Secret Weapon

This is what makes Kestrel **extremely fast**. 

### ❌ Traditional Approach (like Node.js streams)
```
1. Allocate byte[] buffer (say 4KB)
2. Read from socket into buffer
3. Parse what you can
4. If incomplete, allocate bigger buffer, copy old data, read more
5. LOTS of memory allocations and copies
```

### ✅ Pipelines (what Kestrel uses)
```
1. Use a Pipe = (PipeWriter + PipeReader)
2. Writer writes to a chain of memory segments (no single buffer)
3. Reader reads across segments without copying
4. Memory is rented from a pool, returned after use
5. ZERO-COPY, minimal allocations
```

```csharp
// Conceptually what Kestrel does internally:
var pipe = new Pipe();

// Transport writes raw bytes from socket
await socket.ReceiveAsync(pipe.Writer.GetMemory());
pipe.Writer.Advance(bytesRead);
await pipe.Writer.FlushAsync();

// HTTP parser reads without copying
var result = await pipe.Reader.ReadAsync();
ParseHttpHeaders(result.Buffer); // reads across memory segments
pipe.Reader.AdvanceTo(consumed, examined);
```

This is why Kestrel can handle **millions of requests/sec** with low memory.

---

## 4. Threading Model — The Deep Part

### Thread Pool Basics

.NET uses a **managed ThreadPool** with two types of threads:

```
ThreadPool
├── Worker Threads (CPU-bound work)
│   - Run your controller code
│   - Run middleware
│   - Default: starts with Environment.ProcessorCount threads
│   - Grows on demand (adds ~1-2 threads/sec if all busy)
│
└── I/O Completion Port (IOCP) Threads (I/O-bound work)
    - Handle async I/O callbacks
    - Socket reads, file reads, DB responses
    - OS signals completion → IOCP thread picks it up
```

### ThreadPool Sizing

```csharp
// Check current thread pool settings:
ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);

// Typical defaults on 8-core machine:
// Min workers: 8     (= processor count)
// Max workers: 32767
// Min IOCP: 8
// Max IOCP: 1000
```

### ThreadPool Growth Algorithm

```
If all min threads are busy:
  → Queue builds up
  → Every ~500ms, ThreadPool adds ONE new thread
  → This is intentionally slow (thread creation is expensive ~1MB stack)
  → This is why you should NEVER block threads with .Result or .Wait()
```

---

## 5. What Happens Thread-by-Thread

```
Timeline for a single request: GET /api/stores

IOCP Thread 1:
  │ OS notifies: "data arrived on socket"
  │ Kestrel reads raw bytes via Pipeline
  │ Parses HTTP headers
  │ Creates HttpContext
  │ Posts work to ThreadPool
  └─ IOCP thread is FREE (returns to pool)

Worker Thread A:
  │ Picks up the HttpContext
  │ Runs middleware pipeline:
  │   → UseHttpsRedirection (sync, fast)
  │   → UseAuthorization (sync, fast)
  │   → MapControllers → StoresController.GetAll()
  │
  │ Controller code:
  │   await _db.Stores.ToListAsync()
  │         ↓
  │   EF Core sends SQL to SQL Server
  │   await = "I'm done for now, release this thread"
  └─ Worker Thread A is FREE (returns to pool)

         ... SQL Server is processing ...
         ... no thread is held/blocked! ...

IOCP Thread 2 (could be same or different):
  │ OS notifies: "SQL Server response arrived"
  │ Posts continuation to ThreadPool
  └─ IOCP thread is FREE

Worker Thread B (could be A again, or different):
  │ Picks up the continuation (after await)
  │ EF Core maps SQL rows → C# objects
  │ Controller maps to DTOs → serializes to JSON
  │ Kestrel writes response to Pipeline
  │ Response bytes queued for socket write
  └─ Worker Thread B is FREE

IOCP Thread 3:
  │ OS notifies: "socket write complete"
  │ Connection kept alive (HTTP keep-alive) or closed
  └─ Done
```

---

## 6. async/await — Why It Matters

### Key Insight: `await` Doesn't Block a Thread

```csharp
// This does NOT hold a thread during the SQL query:
var stores = await _db.Stores.ToListAsync();

// What actually happens:
// 1. EF Core sends SQL command to SQL Server (async I/O)
// 2. The current thread is RELEASED back to the pool
// 3. No thread exists for this request while SQL is running
// 4. When SQL responds, a NEW thread picks up where we left off
```

### The Numbers

```
Synchronous:  100 concurrent requests = 100 threads blocked waiting for DB
Asynchronous: 100 concurrent requests = ~4-8 threads actively doing work
```

### The Compiler Magic

When you write:
```csharp
public async Task<List<Store>> GetStores()
{
    var stores = await _db.Stores.ToListAsync();
    return stores;
}
```

The C# compiler rewrites this into a **state machine**:
```
State 0: Execute up to the await → start async I/O → RETURN (release thread)
State 1: When I/O completes → resume from here → return result
```

The method is literally **split in half** at the `await` point. Two different threads may run each half.

---

## 7. Concurrency — How Many Requests

```
┌────────────────────────────────────────────────┐
│  Request Queue                                  │
│  (unbounded by default)                         │
│                                                  │
│  Request 1 ─┐                                   │
│  Request 2 ─┤                                   │
│  Request 3 ─┼──→ ThreadPool (worker threads)    │
│  Request 4 ─┤    ├── Thread 1: processing req 1 │
│  Request 5 ─┘    ├── Thread 2: processing req 3 │
│  ...             ├── Thread 3: processing req 5 │
│                  └── Thread 4-N: available       │
│                                                  │
│  Requests 2, 4: awaiting DB (NO thread held)    │
└────────────────────────────────────────────────┘
```

A well-written async ASP.NET Core app on an 8-core machine can handle **thousands of concurrent requests** with just 8-16 threads because most time is spent waiting for I/O (DB, HTTP calls, file reads), during which **no thread is consumed**.

---

## 8. Thread Starvation — The Danger

### ❌ What NOT To Do

```csharp
// NEVER do this in ASP.NET Core:
var stores = _db.Stores.ToList();               // Sync — blocks thread during DB call
var data = httpClient.GetAsync(url).Result;     // .Result blocks thread waiting for I/O
var result = someAsyncMethod().GetAwaiter().GetResult(); // Same problem
```

### ✅ What To Do

```csharp
var stores = await _db.Stores.ToListAsync();
var data = await httpClient.GetAsync(url);
var result = await someAsyncMethod();
```

### What Happens When You Block Threads

```
8-core machine, 8 min threads

1. 8 requests arrive, each blocks a thread on .Result
2. All 8 threads are stuck waiting for I/O
3. Request 9 arrives → queued, no thread available
4. ThreadPool slowly adds 1 thread every 500ms
5. That thread also blocks → starvation
6. App appears frozen, latency spikes to seconds
7. Eventually ThreadPool adds enough threads, but at massive perf cost
```

### Symptoms of Thread Starvation
- Requests randomly take 500ms–5s longer than normal
- CPU is LOW but latency is HIGH (threads are blocked, not working)
- ThreadPool thread count keeps climbing
- Intermittent timeouts under moderate load

---

## 9. Connection Management

### HTTP/1.1 vs HTTP/2

```
Client A ──TCP──→ Kestrel
                  ├── Connection 1 (HTTP/1.1 keep-alive)
                  │   ├── Request 1 → Response 1
                  │   ├── Request 2 → Response 2 (reuses connection, sequential)
                  │   └── Request 3 → Response 3
                  │
Client B ──TCP──→ ├── Connection 2
                  │   └── Request 1 → Response 1
                  │
Client C ──TCP──→ └── Connection 3 (HTTP/2)
                      ├── Stream 1: Request 1 → Response 1  ┐
                      ├── Stream 2: Request 2 → Response 2  ├ multiplexed (parallel)
                      └── Stream 3: Request 3 → Response 3  ┘
```

- **HTTP/1.1**: One request at a time per connection (pipelining rarely used). Browsers open 6 parallel connections to work around this.
- **HTTP/2**: Multiple requests multiplexed over a single TCP connection. Much more efficient.

### Default Kestrel Limits

| Setting | Default |
|---|---|
| Max concurrent connections | Unlimited |
| Max concurrent requests (HTTP/2) | 100 per connection |
| Max request body size | 30 MB |
| Keep-alive timeout | 130 seconds |
| Request headers timeout | 30 seconds |
| Max request header size | 32 KB |

---

## 10. Full Picture — Packet to Response

```
 TCP packet arrives at port 5116
         │
         ▼
 OS Kernel (IOCP / epoll)
         │
         ▼
 IOCP Thread: reads socket → Pipeline buffer
         │
         ▼
 HTTP Parser: bytes → HttpContext
 (method, path, headers, body stream)
         │
         ▼
 Worker Thread: runs middleware pipeline
         │
    ┌────┴────────────────────────────┐
    │  Swagger? → No → pass through   │
    │  HTTPS redirect? → pass through │
    │  Authorization? → pass through  │
    │  Routing? → matches controller  │
    └────┬────────────────────────────┘
         │
         ▼
 DI creates controller + injects DbContext
         │
         ▼
 Controller.GetAll() runs
    │
    │  await _db.Stores.ToListAsync()
    │      │
    │      ▼
    │  EF Core → SQL → sends to SQL Server
    │  Thread RELEASED ← (no thread held)
    │
    │  ... SQL Server processes ...
    │
    │  IOCP Thread: SQL response arrives
    │      │
    │      ▼
    │  Worker Thread: picks up continuation
    │  Maps SQL rows → C# objects → JSON
    │
    ▼
 Response bytes written to Pipeline
         │
         ▼
 IOCP Thread: flushes Pipeline → socket
         │
         ▼
 TCP packet sent to client
```

---

## Summary Table

| Concept | What It Means |
|---|---|
| **Kestrel** | Raw, fast, async HTTP server built into ASP.NET Core |
| **IOCP threads** | Handle I/O notifications from OS (socket, DB, file) |
| **Worker threads** | Run your actual code (controllers, middleware) |
| **`await`** | Releases the thread during I/O — doesn't block |
| **Pipelines** | Zero-copy, pooled memory for reading/writing bytes |
| **ThreadPool** | Manages a dynamic pool of threads, grows slowly on demand |
| **State machine** | What the compiler turns async/await into internally |
| **Thread starvation** | What happens when you block threads with `.Result` |

The entire design is built around **never blocking threads**. That's what makes ASP.NET Core one of the [fastest web frameworks](https://www.techempower.com/benchmarks/) in existence.

---

## References
- [Kestrel web server in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel)
- [System.IO.Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)
- [.NET ThreadPool](https://learn.microsoft.com/en-us/dotnet/standard/threading/the-managed-thread-pool)
- [TechEmpower Benchmarks](https://www.techempower.com/benchmarks/)
- [Diagnosing Thread Pool Starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation)
