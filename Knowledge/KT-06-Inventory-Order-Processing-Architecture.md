# KT-06: Real-Time Inventory & Order Processing System — Full Architecture
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers learning event-driven microservices with .NET 8  
**Status:** Living Document

---

## Table of Contents
1. [System Overview](#1-system-overview)
2. [Architecture Diagram](#2-architecture-diagram)
3. [The Three Services](#3-the-three-services)
4. [Infrastructure Stack](#4-infrastructure-stack)
5. [Technology Map — What, Where, Why](#5-technology-map--what-where-why)
6. [Event-Driven Flow — Step by Step](#6-event-driven-flow--step-by-step)
7. [StoreApi — Deep Dive](#7-storeapi--deep-dive)
8. [OrderService — Deep Dive](#8-orderservice--deep-dive)
9. [InventoryWorker — Deep Dive](#9-inventoryworker--deep-dive)
10. [Kafka Topics & Event Contracts](#10-kafka-topics--event-contracts)
11. [Redis Caching Strategy](#11-redis-caching-strategy)
12. [Database Design — SQL Server + PostgreSQL](#12-database-design--sql-server--postgresql)
13. [Idempotency & Reliability Patterns](#13-idempotency--reliability-patterns)
14. [Resilience with Polly](#14-resilience-with-polly)
15. [Serilog — Unified Logging Across Services](#15-serilog--unified-logging-across-services)
16. [Docker Compose — Infrastructure](#16-docker-compose--infrastructure)
17. [How to Run Everything](#17-how-to-run-everything)
18. [API Reference](#18-api-reference)
19. [Design Tradeoffs & What to Improve](#19-design-tradeoffs--what-to-improve)

---

## 1. System Overview

This is a **3-service event-driven microservices system** built for learning real-world patterns used in production .NET platforms.

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Real-Time Inventory & Order Processing           │
│                                                                     │
│   StoreApi          OrderService          InventoryWorker           │
│   (Product CRUD)    (Order placement)     (Stock management)        │
│   SQL Server        PostgreSQL            Dapper → SQL Server       │
│   port 5116         port 5200             (no HTTP — background)    │
│                                                                     │
│   Connected via: Kafka (events) + Redis (caching)                   │
└─────────────────────────────────────────────────────────────────────┘
```

**What it does:**
- A customer places an order via OrderService
- OrderService publishes an event to Kafka
- InventoryWorker consumes that event, decrements stock, and publishes back
- OrderService updates the order status based on the inventory response
- All of this happens asynchronously via Kafka message passing

---

## 2. Architecture Diagram

```
                         ┌──────────────┐
                         │   Customer   │
                         └──────┬───────┘
                                │
                    POST /api/orders { productId, qty }
                                │
                                ▼
                    ┌───────────────────────┐
                    │    OrderService        │
                    │    (.NET 8 Web API)    │
                    │    PostgreSQL (EF Core)│
                    │    Port 5200          │
                    └───┬───────────┬───────┘
                        │           │
           ┌────────────┘           └─────────────────┐
           │                                          │
           ▼                                          ▼
    ┌──────────────┐                          ┌──────────────┐
    │  Redis Cache  │◄─── cache product ──────│   StoreApi    │
    │  (6379)       │     price + stock        │  (.NET 8 API) │
    │               │                          │  SQL Server   │
    │ - product info│    HTTP GET /api/products │  Port 5116    │
    │ - order status│◄─── Polly retry ────────│               │
    │ - stock levels│                          └───────────────┘
    └──────────────┘                                  ▲
           ▲                                          │
           │                                   Dapper (direct SQL)
           │                                          │
           │           ┌────────────────────┐         │
           │           │      Kafka         │         │
           │           │   (9092)           │         │
           │           │                    │         │
           │           │ ┌────────────────┐ │         │
           └───────────┤ │ order-placed   │ ├─────────┤
                       │ └────────────────┘ │         │
                       │ ┌────────────────┐ │         │
                       │ │inventory-updated│ │         │
                       │ └────────────────┘ │    ┌────┴──────────────┐
                       │ ┌────────────────┐ │    │ InventoryWorker   │
                       │ │low-stock-alert │ │    │ (.NET 8 Worker)   │
                       │ └────────────────┘ │    │ Dapper + Redis    │
                       └────────────────────┘    │ (no HTTP API)     │
                                                 └───────────────────┘
```

---

## 3. The Three Services

### StoreApi — The Product Catalog

| Aspect | Detail |
|---|---|
| **Type** | ASP.NET Core Web API |
| **Port** | 5116 |
| **Database** | SQL Server (StoreApiDb) |
| **ORM** | EF Core (read queries only) |
| **Migrations** | Yuniql (SQL Server) |
| **Logging** | Serilog |
| **Purpose** | Product catalog + Store management (CRUD) |

**Owns:** `Stores` table, `Products` table, `ProcessedInventoryEvents` table

### OrderService — Order Placement & Tracking

| Aspect | Detail |
|---|---|
| **Type** | ASP.NET Core Web API |
| **Port** | 5200 |
| **Database** | PostgreSQL (OrdersDb) |
| **ORM** | EF Core |
| **Migrations** | Yuniql (PostgreSQL) |
| **Cache** | Redis (product info + order status) |
| **Messaging** | Kafka producer + consumer |
| **Resilience** | Polly (HTTP retry) |
| **Logging** | Serilog |

**Owns:** `orders` table

### InventoryWorker — Background Stock Management

| Aspect | Detail |
|---|---|
| **Type** | .NET Worker Service (no HTTP) |
| **Database** | Connects to StoreApi's SQL Server via **Dapper** |
| **Cache** | Redis (stock levels) |
| **Messaging** | Kafka consumer + producer |
| **Logging** | Serilog |
| **Purpose** | Consumes orders, decrements stock, publishes results |

**Owns:** No database of its own — writes to StoreApi's DB using Dapper

---

## 4. Infrastructure Stack

All infrastructure runs in Docker via `infra/docker-compose.yml`:

```
┌────────────────────────────────────────────────────────────────┐
│                    Docker Compose Stack                         │
│                                                                │
│  ┌────────────┐  ┌────────────┐  ┌─────────────────────────┐  │
│  │ SQL Server │  │ PostgreSQL │  │ Redis                   │  │
│  │ :1433      │  │ :5432      │  │ :6379                   │  │
│  │ StoreApiDb │  │ OrdersDb   │  │ product/order/stock     │  │
│  └────────────┘  └────────────┘  │ cache                   │  │
│                                   └─────────────────────────┘  │
│  ┌────────────────────────────────────────────────────────────┐│
│  │ Kafka Ecosystem                                            ││
│  │  Zookeeper :2181  │  Kafka :9092  │  Schema Registry :8081 ││
│  └────────────────────────────────────────────────────────────┘│
│                                                                │
│  ┌──────────────────┐  ┌──────────────────┐                   │
│  │ Kafka UI  :8080  │  │ Redis Commander  │                   │
│  │ (web dashboard)  │  │ :8082            │                   │
│  └──────────────────┘  └──────────────────┘                   │
└────────────────────────────────────────────────────────────────┘
```

| Service | Image | Port | Purpose |
|---|---|---|---|
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` | 1433 | StoreApi database |
| PostgreSQL | `postgres:16-alpine` | 5432 | OrderService database |
| Redis | `redis:7-alpine` | 6379 | Distributed cache |
| Zookeeper | `confluentinc/cp-zookeeper:7.7.1` | 2181 | Kafka coordination |
| Kafka | `confluentinc/cp-kafka:7.7.1` | 9092 | Message broker |
| Schema Registry | `confluentinc/cp-schema-registry:7.7.1` | 8081 | Schema management |
| Kafka UI | `provectuslabs/kafka-ui:latest` | 8080 | Web dashboard |
| Redis Commander | `rediscommander/redis-commander` | 8082 | Redis web UI |

---

## 5. Technology Map — What, Where, Why

| Technology | Used In | How It's Used | What You Learn |
|---|---|---|---|
| **EF Core** | StoreApi, OrderService | ORM for database queries (read + write) | Entity mapping, DbContext, LINQ queries, migrations |
| **Dapper** | InventoryWorker | Direct SQL for perf-critical stock decrements | Micro-ORM, raw SQL, `OUTPUT` clause, transactions |
| **Kafka Producer** | OrderService, InventoryWorker | Publish events asynchronously | Event-driven architecture, topic partitions |
| **Kafka Consumer** | OrderService, InventoryWorker | Consume events from topics | Consumer groups, offsets, manual commits |
| **Redis Cache** | OrderService, InventoryWorker | Cache product info, order status, stock levels | Distributed caching, TTL, cache-aside pattern |
| **Serilog** | All 3 services | Structured logging to console + file + JSON | Enrichers, sinks, request logging, correlation |
| **Swagger** | StoreApi, OrderService | API documentation & testing UI | OpenAPI spec generation |
| **Yuniql** | StoreApi, OrderService | Database schema migrations (version folders) | SQL-first migrations, auto-create DB |
| **Polly** | OrderService | Retry with exponential backoff on HTTP calls | Resilience patterns, transient fault handling |
| **HTTP Client** | OrderService → StoreApi | Fetch product info from StoreApi | Service-to-service communication |
| **SQL Server** | StoreApi, InventoryWorker | Relational database for products + stores | T-SQL, indexes, identity columns |
| **PostgreSQL** | OrderService | Relational database for orders | PostgreSQL syntax, UUID primary keys |
| **Worker Service** | InventoryWorker | Long-running background process (no HTTP) | `BackgroundService`, `IHostedService` |

---

## 6. Event-Driven Flow — Step by Step

### The Happy Path (order succeeds)

```
Step 1: Customer → POST /api/orders { productId: 1, quantity: 5 }
        ┌─────────────────────────────────────────────────┐
        │ OrderService receives the request               │
        │                                                 │
        │ 1. Check Redis for product price (cache hit?)   │
        │ 2. If miss → HTTP GET StoreApi/api/products/1   │
        │    └── Polly retries up to 3x on failure        │
        │ 3. Cache product info in Redis (5 min TTL)      │
        │ 4. Create Order in PostgreSQL (status: Pending) │
        │ 5. Cache order status in Redis                  │
        │ 6. Publish to Kafka topic "order-placed"        │
        │ 7. Return 201 { orderId, status: "Pending" }    │
        └─────────────────────────┬───────────────────────┘
                                  │
                    Kafka topic: "order-placed"
                    { orderId, productId: 1, quantity: 5, timestamp }
                                  │
                                  ▼
        ┌─────────────────────────────────────────────────┐
        │ InventoryWorker consumes the event              │
        │                                                 │
        │ 1. Idempotency check: was this OrderId already  │
        │    processed? (ProcessedInventoryEvents table)   │
        │ 2. If new → atomic SQL UPDATE:                  │
        │    UPDATE Products                              │
        │    SET StockQuantity = StockQuantity - 5        │
        │    OUTPUT INSERTED.StockQuantity                │
        │    WHERE Id = 1 AND StockQuantity >= 5          │
        │ 3. Record in ProcessedInventoryEvents table     │
        │ 4. Update Redis cache with new stock level      │
        │ 5. If stock crossed below 10 → publish          │
        │    "low-stock-alert" to Kafka                   │
        │ 6. Publish "inventory-updated" to Kafka         │
        │    { orderId, status: "Confirmed", inStock: true}│
        │ 7. Commit Kafka offset (manual commit)          │
        └─────────────────────────┬───────────────────────┘
                                  │
                    Kafka topic: "inventory-updated"
                    { orderId, productId, status, inStock, remainingStock }
                                  │
                                  ▼
        ┌─────────────────────────────────────────────────┐
        │ OrderService KafkaConsumerService consumes it   │
        │                                                 │
        │ 1. Deserialize InventoryUpdatedEvent            │
        │ 2. Look up order in PostgreSQL                  │
        │ 3. Update status: "Pending" → "Confirmed"       │
        │ 4. Cache new status in Redis                    │
        │ 5. Commit Kafka offset                          │
        └─────────────────────────────────────────────────┘

Step 5: Customer → GET /api/orders/{id}/status
        → Reads from Redis cache → { status: "Confirmed" } (fast, no DB hit)
```

### The Failure Path (out of stock)

```
InventoryWorker:
  SQL UPDATE affects 0 rows (StockQuantity < requested)
  → Publishes "inventory-updated" { status: "OutOfStock", inStock: false }

OrderService:
  → Updates order status to "OutOfStock"
```

---

## 7. StoreApi — Deep Dive

### Project Structure
```
StoreApi/
├── Program.cs                      # Host setup, Serilog, EF Core, Yuniql, Swagger
├── StoreApi.csproj                 # .NET 8, EF Core SQL Server, Serilog, Yuniql
├── appsettings.json                # SQL Server connection string
├── Controllers/
│   ├── StoresController.cs         # CRUD for stores
│   ├── ProductsController.cs       # CRUD for products (with filtering)
│   └── WeatherForecastController.cs # Default template (can remove)
├── Models/
│   ├── Store.cs                    # Store entity
│   ├── Product.cs                  # Product entity (with FK to Store)
│   └── DTOs.cs                     # Request/response records
├── Data/
│   ├── AppDbContext.cs             # EF Core DbContext
│   └── ConsoleTraceService.cs      # Yuniql trace adapter
├── db/                             # Yuniql migration scripts
│   ├── v0.00/01-create-tables.sql  # Initial schema + seed data
│   ├── v0.01/01-add-processed-events.sql  # Idempotency table
│   ├── _init/ _pre/ _post/ _draft/ _erase/
└── logs/                           # Serilog output files
```

### How EF Core Is Used Here

EF Core is used **only for querying** — schema changes are managed by Yuniql.

```csharp
// AppDbContext.cs — Maps C# classes to SQL Server tables
public class AppDbContext : DbContext
{
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Relationship: Product belongs to Store
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasOne(p => p.Store)
                  .WithMany(s => s.Products)
                  .HasForeignKey(p => p.StoreId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
```

**Key concept:** EF Core's `DbSet<T>` gives you LINQ-to-SQL queries:
```csharp
// This C# code becomes a SQL query automatically
var products = await _db.Products
    .Include(p => p.Store)              // JOIN with Stores table
    .Where(p => p.Category == "Peripherals")  // WHERE clause
    .Select(p => new ProductResponse(...))     // SELECT projection
    .ToListAsync();                            // Execute query
```

### How Yuniql Migrations Work

Yuniql runs SQL scripts in version order on startup:

```
db/
├── v0.00/01-create-tables.sql    ← Runs first (creates Stores + Products)
├── v0.01/01-add-processed-events.sql  ← Runs second (adds idempotency table)
├── _init/    ← Runs once when DB is first created
├── _pre/     ← Runs before every migration
├── _post/    ← Runs after every migration
├── _draft/   ← Work-in-progress scripts (not committed)
└── _erase/   ← Drops everything (for clean resets)
```

Configured in `Program.cs`:
```csharp
app.UseYuniql(
    new SqlServerDataService(traceService),
    new SqlServerBulkImportService(traceService),
    traceService,
    new Configuration
    {
        Platform = "sqlserver",
        Workspace = yuniqlWorkspacePath,  // Points to db/ folder
        ConnectionString = connectionString,
        IsAutoCreateDatabase = true,      // Creates DB if it doesn't exist
    });
```

---

## 8. OrderService — Deep Dive

### Project Structure
```
OrderService/
├── Program.cs                         # Host setup, EF Core (Npgsql), Kafka, Redis, Polly
├── OrderService.csproj                # .NET 8, Confluent.Kafka, Redis, Polly, Serilog
├── appsettings.json                   # PostgreSQL + Kafka + Redis + StoreApi URLs
├── Controllers/
│   └── OrdersController.cs            # POST/GET/DELETE orders
├── Models/
│   ├── Order.cs                       # Order entity
│   └── DTOs.cs                        # CreateOrderRequest, OrderResponse, ProductInfo
├── Data/
│   ├── OrderDbContext.cs              # EF Core with PostgreSQL snake_case mapping
│   └── ConsoleTraceService.cs         # Yuniql trace adapter
├── Services/
│   ├── KafkaProducerService.cs        # Publishes "order-placed" events
│   ├── KafkaConsumerService.cs        # Consumes "inventory-updated" events
│   ├── RedisCacheService.cs           # Caches product info + order status
│   └── StoreApiClient.cs             # HTTP client to fetch products from StoreApi
└── db/
    └── v0.00/01-create-orders-table.sql
```

### Service Registration in Program.cs

```csharp
// EF Core with PostgreSQL
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

// Redis distributed cache
builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = "localhost:6379");

// Kafka (singleton — producers/consumers are thread-safe)
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddHostedService<KafkaConsumerService>();  // Background consumer

// HTTP client with Polly retry policy
builder.Services.AddHttpClient<StoreApiClient>(client =>
    client.BaseAddress = new Uri("http://localhost:5116"))
    .AddPolicyHandler(retryPolicy);  // 3 retries, exponential backoff
```

### How the Kafka Producer Works

When an order is created, `KafkaProducerService` publishes an event:

```csharp
public async Task PublishOrderPlacedAsync(Order order, CancellationToken ct)
{
    var payload = new OrderPlacedEvent(order.Id, order.ProductId, order.Quantity, DateTime.UtcNow);
    var message = new Message<string, string>
    {
        Key = order.Id.ToString(),       // Kafka key — used for partitioning
        Value = JsonSerializer.Serialize(payload)  // JSON payload
    };

    var result = await _producer.ProduceAsync("order-placed", message, ct);
    // result contains: Topic, Partition, Offset — confirms delivery
}
```

**Key concept:** The message **Key** determines which Kafka partition the message goes to. Same key = same partition = ordered processing for that order.

### How the Kafka Consumer Works

`KafkaConsumerService` is a `BackgroundService` that runs continuously:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    consumer.Subscribe("inventory-updated");  // Subscribe to topic

    while (!stoppingToken.IsCancellationRequested)
    {
        var result = consumer.Consume(stoppingToken);  // Blocks until message

        // Process: update order status in PostgreSQL + Redis
        order.Status = inventoryEvent.InStock ? "Confirmed" : "OutOfStock";
        await dbContext.SaveChangesAsync();

        consumer.Commit(result);  // Manual commit — only after success
    }
}
```

**Key concepts:**
- `BackgroundService` runs for the lifetime of the application
- `EnableAutoCommit = false` — offsets only committed after successful processing
- `AutoOffsetReset.Earliest` — starts from beginning if no prior offset exists
- `GroupId = "order-service"` — consumer group for load balancing

### How Redis Caching Works

```csharp
// Cache-aside pattern in the controller:
var product = await _cacheService.GetProductInfoAsync(productId);
if (product is null)
{
    product = await _storeApiClient.GetProductAsync(productId);  // HTTP call
    await _cacheService.SetProductInfoAsync(productId, product);  // Cache it
}
```

**Cache keys used:**
- `product-info:{productId}` — product name, price, stock (TTL: 5 minutes)
- `order-status:{orderId}` — current order status string (TTL: 30 minutes)

### How Polly Retry Works

```csharp
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()         // 5xx, 408, network errors
    .WaitAndRetryAsync(3, retryAttempt =>
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));  // 2s, 4s, 8s
```

This wraps the `HttpClient` used by `StoreApiClient`. If StoreApi is temporarily down, the call retries 3 times with exponential backoff before throwing.

---

## 9. InventoryWorker — Deep Dive

### Project Structure
```
InventoryWorker/
├── Program.cs                         # Host.CreateDefaultBuilder + Serilog
├── InventoryWorker.csproj             # .NET 8 Worker SDK, Dapper, Kafka, Redis
├── appsettings.json                   # SQL Server + Kafka + Redis config
├── Services/
│   ├── OrderPlacedConsumer.cs         # BackgroundService — Kafka consumer
│   ├── InventoryService.cs            # Dapper SQL — stock decrement + idempotency
│   ├── KafkaProducerService.cs        # Publishes inventory-updated + low-stock-alert
│   └── RedisCacheService.cs           # Caches stock levels
└── Models/
    └── Events.cs                      # Event record DTOs
```

### Why Worker Service (not Web API)?

InventoryWorker has **no HTTP endpoints**. It's a pure background process that:
- Consumes Kafka messages
- Writes to SQL Server
- Publishes Kafka messages

The `Microsoft.NET.Sdk.Worker` SDK gives you a `Host` without the HTTP pipeline overhead (no Kestrel, no routing, no middleware). It's ideal for background consumers, scheduled jobs, and message processors.

```csharp
// Program.cs — simpler than Web API
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices((context, services) =>
{
    services.AddHostedService<OrderPlacedConsumer>();  // The main work
    // ... register other services
});
var host = builder.Build();
await host.RunAsync();
```

### How Dapper Works (vs EF Core)

**EF Core** (used in StoreApi + OrderService):
- Maps C# objects to tables automatically
- Generates SQL from LINQ queries
- Tracks changes, manages relationships
- Higher overhead, more abstraction

**Dapper** (used in InventoryWorker):
- You write raw SQL
- Maps results to C# objects
- No change tracking, no overhead
- Fast and explicit

```csharp
// Dapper — you write the SQL, Dapper maps the result
var result = await connection.QuerySingleOrDefaultAsync<StockUpdateRow>(
    @"UPDATE [dbo].[Products]
      SET [StockQuantity] = [StockQuantity] - @Quantity
      OUTPUT INSERTED.[StockQuantity] AS NewStock,
             INSERTED.[Name] AS ProductName,
             (INSERTED.[StockQuantity] + @Quantity) AS PreviousStock
      WHERE [Id] = @ProductId
        AND [StockQuantity] >= @Quantity",
    new { ProductId = productId, Quantity = quantity },  // Parameters
    transaction);  // Inside a transaction
```

**Key SQL concepts used here:**
- `OUTPUT INSERTED.*` — returns the new values after UPDATE in one atomic statement
- `WHERE StockQuantity >= @Quantity` — prevents overselling at the database level
- Transaction wraps the idempotency check + stock update atomically

### The InventoryService — Full Flow

```
DecrementStockAsync(orderId, productId, quantity):
│
├── Open SqlConnection + Begin Transaction
│
├── 1. Idempotency Check:
│      SELECT COUNT(1) FROM ProcessedInventoryEvents WHERE OrderId = @OrderId
│      └── If already processed → return AlreadyProcessed (skip)
│
├── 2. Atomic Stock Decrement:
│      UPDATE Products SET StockQuantity -= @Qty
│      OUTPUT INSERTED.StockQuantity, INSERTED.Name
│      WHERE Id = @ProductId AND StockQuantity >= @Qty
│      │
│      ├── If 0 rows affected:
│      │   ├── Check if product exists
│      │   ├── Record failure in ProcessedInventoryEvents
│      │   └── Return InsufficientStock or ProductNotFound
│      │
│      └── If rows affected:
│          ├── Record success in ProcessedInventoryEvents
│          └── Return Succeeded(newStock, previousStock, productName)
│
└── Commit Transaction
```

### Low-Stock Alert — Threshold Crossing

Alerts only fire when stock **crosses below** the threshold, not every time it's below:

```csharp
// Only alert when crossing: 12 → 8 (crosses 10), NOT 8 → 3 (already below)
if (result.NewStock < _lowStockThreshold && result.PreviousStock >= _lowStockThreshold)
{
    await kafkaProducer.PublishLowStockAlertAsync(
        new LowStockAlertEvent(productId, productName, newStock, threshold, timestamp));
}
```

This prevents flooding Kafka with repeated alerts after stock drops below the threshold.

---

## 10. Kafka Topics & Event Contracts

### Topics

| Topic | Producer | Consumer | Purpose |
|---|---|---|---|
| `order-placed` | OrderService | InventoryWorker | New order needs stock decrement |
| `inventory-updated` | InventoryWorker | OrderService | Stock result → update order status |
| `low-stock-alert` | InventoryWorker | (future consumer) | Stock below threshold warning |

### Event Schemas

```json
// order-placed (produced by OrderService)
{
  "orderId": "a1b2c3d4-...",
  "productId": 1,
  "quantity": 5,
  "timestamp": "2026-06-05T09:30:00Z"
}

// inventory-updated (produced by InventoryWorker)
{
  "orderId": "a1b2c3d4-...",
  "productId": 1,
  "status": "Confirmed",      // or "OutOfStock" or "ProductNotFound"
  "inStock": true,
  "remainingStock": 145,
  "timestamp": "2026-06-05T09:30:01Z"
}

// low-stock-alert (produced by InventoryWorker)
{
  "productId": 1,
  "productName": "Wireless Mouse",
  "currentStock": 8,
  "threshold": 10,
  "timestamp": "2026-06-05T09:30:01Z"
}
```

### Kafka Concepts Used

| Concept | How It's Used |
|---|---|
| **Topics** | Logical channels for different event types |
| **Message Key** | OrderId or ProductId — determines partition placement |
| **Consumer Group** | `order-service`, `inventory-worker` — each gets all messages |
| **Manual Commit** | Offset committed only after successful processing |
| **Auto Offset Reset** | `Earliest` — read from beginning if no prior offset |
| **Idempotent Producer** | InventoryWorker enables `EnableIdempotence = true` |
| **At-Least-Once Delivery** | Default Kafka guarantee — handled via idempotency table |

---

## 11. Redis Caching Strategy

### What's Cached

| Cache Key Pattern | Service | TTL | Purpose |
|---|---|---|---|
| `product-info:{id}` | OrderService | 5 min | Avoid repeated HTTP calls to StoreApi |
| `order-status:{id}` | OrderService | 30 min | Fast status reads without DB hit |
| `stock-level:{id}` | InventoryWorker | 10 min | Current stock level after updates |

### Caching Patterns Used

**Cache-Aside (OrderService):**
```
1. Check cache → if hit, return cached value
2. If miss → fetch from source (HTTP or DB)
3. Store in cache with TTL
4. Return value
```

**Write-Through (InventoryWorker):**
```
1. Write to database (source of truth)
2. Update cache with new value
3. Cache is always fresh after writes
```

**Important:** Redis is **never** used to make stock availability decisions. SQL Server is always the source of truth for the write path.

---

## 12. Database Design — SQL Server + PostgreSQL

### Why Two Different Databases?

This mirrors real-world microservices where each service owns its own database:

| Database | Engine | Used By | Why This Engine |
|---|---|---|---|
| StoreApiDb | SQL Server | StoreApi + InventoryWorker | Platform team's standard, T-SQL features |
| OrdersDb | PostgreSQL | OrderService | PostgreSQL learning, UUID support, different dialect |

### StoreApiDb Schema (SQL Server)

```sql
Stores
├── Id (INT, identity, PK)
├── Name (NVARCHAR 200)
├── Address, City, Phone
└── CreatedAt (DATETIME2)

Products
├── Id (INT, identity, PK)
├── Name (NVARCHAR 200)
├── Description (NVARCHAR 1000)
├── Price (DECIMAL 18,2)
├── StockQuantity (INT)         ← Decremented by InventoryWorker
├── Category (NVARCHAR 100)
├── CreatedAt (DATETIME2)
└── StoreId (INT, FK → Stores)  ← CASCADE DELETE

ProcessedInventoryEvents         ← Idempotency table
├── OrderId (UNIQUEIDENTIFIER, PK)
├── ProductId (INT)
├── Success (BIT)
└── ProcessedAt (DATETIME2)
```

### OrdersDb Schema (PostgreSQL)

```sql
orders
├── id (UUID, PK, default gen_random_uuid())
├── product_id (INTEGER)
├── product_name (VARCHAR 200)
├── quantity (INTEGER)
├── unit_price (DECIMAL 18,2)
├── total_price (DECIMAL 18,2)
├── status (VARCHAR 50)         ← Pending → Confirmed / OutOfStock / Cancelled
├── customer_name (VARCHAR 200)
├── customer_email (VARCHAR 200)
├── created_at (TIMESTAMPTZ)
└── updated_at (TIMESTAMPTZ)
```

**Notice:** PostgreSQL uses `snake_case` columns and `UUID` primary keys, while SQL Server uses `PascalCase` and `INT IDENTITY`. The EF Core `OnModelCreating` in OrderService maps between C# PascalCase properties and PostgreSQL snake_case columns.

---

## 13. Idempotency & Reliability Patterns

### The Problem

Kafka provides **at-least-once** delivery. The same message can be delivered multiple times due to:
- Consumer crashes before committing offset
- Network issues during offset commit
- Consumer group rebalancing

Without protection, a single order could decrement stock multiple times.

### The Solution — Idempotency Table

```
┌────────────────────────────────────────────────────────────┐
│  ProcessedInventoryEvents table in SQL Server               │
│                                                            │
│  OrderId (PK)  │  ProductId  │  Success  │  ProcessedAt    │
│  a1b2c3d4...   │  1          │  1        │  2026-06-05...  │
│  e5f6g7h8...   │  2          │  0        │  2026-06-05...  │
└────────────────────────────────────────────────────────────┘
```

**Flow:**
```
Message arrives → BEGIN TRANSACTION
  1. SELECT COUNT(*) FROM ProcessedInventoryEvents WHERE OrderId = @id
     → If exists: SKIP (already processed), re-publish result, COMMIT
  2. If not exists: proceed with stock UPDATE
  3. INSERT INTO ProcessedInventoryEvents (record the processing)
  4. COMMIT TRANSACTION
  5. Publish Kafka event
  6. Commit Kafka offset
```

The idempotency check and stock update happen in the **same SQL transaction**, ensuring atomicity.

### Manual Offset Commits

Both Kafka consumers use `EnableAutoCommit = false`:

```csharp
_consumerConfig = new ConsumerConfig
{
    EnableAutoCommit = false,  // We control when offsets are committed
};

// In the consume loop:
try
{
    await ProcessMessage(result.Message.Value);
    consumer.Commit(result);  // Only after successful processing
}
catch (Exception ex)
{
    _logger.LogError(ex, "Processing failed");
    // Don't commit — message will be redelivered
}
```

---

## 14. Resilience with Polly

### Where It's Used

Polly is used in **OrderService** to handle transient failures when calling StoreApi:

```csharp
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()    // 5xx responses, 408 Timeout, network errors
    .WaitAndRetryAsync(
        3,                         // Retry up to 3 times
        retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
        // Wait: 2s → 4s → 8s (exponential backoff)
    );

builder.Services.AddHttpClient<StoreApiClient>(client =>
    client.BaseAddress = new Uri("http://localhost:5116"))
    .AddPolicyHandler(retryPolicy);  // Wraps every HTTP call
```

### What Happens on Failure

```
Attempt 1: GET http://localhost:5116/api/products/1 → 503 Service Unavailable
  Wait 2 seconds...
Attempt 2: GET http://localhost:5116/api/products/1 → 503 Service Unavailable
  Wait 4 seconds...
Attempt 3: GET http://localhost:5116/api/products/1 → 200 OK ✓
  Return product data
```

If all 3 retries fail, the exception propagates to the controller, which returns an error to the caller.

---

## 15. Serilog — Unified Logging Across Services

All 3 services use the same Serilog configuration pattern:

### Three Log Outputs (Sinks)

| Sink | Format | Purpose |
|---|---|---|
| Console | Human-readable with thread ID | Development debugging |
| File (text) | Timestamped with thread + process | Production troubleshooting |
| File (JSON) | Compact JSON (machine-readable) | Log aggregation tools (ELK, Seq) |

### Enrichers — Context in Every Log

```csharp
.Enrich.WithThreadId()          // Which thread handled this
.Enrich.WithProcessId()         // Which process (useful in multi-instance)
.Enrich.WithMachineName()       // Which machine
.Enrich.WithEnvironmentName()   // Development / Production
```

### Request Logging (StoreApi + OrderService only)

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = 
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms";
    
    // Dynamic log levels based on response
    options.GetLevel = (ctx, elapsed, ex) =>
    {
        if (ex is not null) return Error;
        if (ctx.Response.StatusCode >= 500) return Error;
        if (ctx.Response.StatusCode >= 400) return Warning;
        if (elapsed > 3000) return Warning;  // Slow requests
        return Information;
    };
});
```

### Log File Locations

```
StoreApi/logs/storeapi-20260605.log
StoreApi/logs/storeapi-json-20260605.log
OrderService/logs/orderservice-20260605.log
OrderService/logs/orderservice-json-20260605.log
InventoryWorker/logs/inventory-worker-20260605.log
InventoryWorker/logs/inventory-worker-json-20260605.log
```

---

## 16. Docker Compose — Infrastructure

### Starting Everything

```bash
cd infra/
docker-compose up -d
```

### Checking Health

```bash
docker-compose ps          # All containers should be "Up (healthy)"
docker-compose logs kafka  # Check Kafka startup
```

### Web Dashboards

| Dashboard | URL | What You See |
|---|---|---|
| **Kafka UI** | http://localhost:8080 | Topics, messages, consumer groups, lag |
| **Redis Commander** | http://localhost:8082 | All cached keys and values |
| **Swagger (StoreApi)** | http://localhost:5116/swagger | StoreApi endpoints |
| **Swagger (OrderService)** | http://localhost:5200/swagger | OrderService endpoints |

### Resetting Everything

```bash
docker-compose down -v    # Stops containers AND deletes all data volumes
docker-compose up -d      # Fresh start
```

---

## 17. How to Run Everything

### Step 1: Start Infrastructure
```bash
cd "C:\Users\...\Practice\code\infra"
docker-compose up -d
```

Wait ~30 seconds for all services to become healthy.

### Step 2: Run the Services (3 terminals)

```bash
# Terminal 1 — StoreApi
cd StoreApi
dotnet run

# Terminal 2 — OrderService
cd OrderService
dotnet run

# Terminal 3 — InventoryWorker
cd InventoryWorker
dotnet run
```

### Step 3: Test the Flow

```bash
# 1. Create a store + product (if not seeded)
# StoreApi seeds data automatically via Yuniql

# 2. Place an order
curl -X POST http://localhost:5200/api/orders \
  -H "Content-Type: application/json" \
  -d '{"productId": 1, "quantity": 5, "customerName": "Test User"}'

# 3. Check order status (should go from Pending → Confirmed)
curl http://localhost:5200/api/orders/{orderId}

# 4. Check Kafka UI for events
# http://localhost:8080 → Topics → order-placed / inventory-updated

# 5. Check Redis Commander for cached values
# http://localhost:8082 → Keys → product-info:1, stock-level:1
```

---

## 18. API Reference

### StoreApi (http://localhost:5116)

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/stores` | List all stores |
| GET | `/api/stores/{id}` | Get store by ID |
| POST | `/api/stores` | Create store |
| PUT | `/api/stores/{id}` | Update store |
| DELETE | `/api/stores/{id}` | Delete store |
| GET | `/api/products` | List products (filter by ?category, ?storeId) |
| GET | `/api/products/{id}` | Get product by ID |
| POST | `/api/products` | Create product |
| PUT | `/api/products/{id}` | Update product |
| DELETE | `/api/products/{id}` | Delete product |

### OrderService (http://localhost:5200)

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/orders` | List orders (filter by ?status) |
| GET | `/api/orders/{id}` | Get order by ID (with Redis-cached status) |
| POST | `/api/orders` | Place new order → triggers Kafka flow |
| DELETE | `/api/orders/{id}` | Cancel order |

### InventoryWorker (no HTTP API)

| Kafka Topic | Direction | Event |
|---|---|---|
| `order-placed` | Consumes | Triggers stock decrement |
| `inventory-updated` | Produces | Reports result to OrderService |
| `low-stock-alert` | Produces | Warns when stock drops below threshold |

---

## 19. Design Tradeoffs & What to Improve

### Intentional Tradeoffs (for learning simplicity)

| Decision | Tradeoff | Production Alternative |
|---|---|---|
| InventoryWorker writes to StoreApi's DB | Breaks service ownership boundary | Give InventoryWorker its own DB, or expose a StoreApi endpoint |
| No outbox pattern | If Kafka publish fails after DB update, order stays Pending | Transactional outbox: write events to DB, background publisher |
| No dead-letter queue | Poison messages block the consumer | DLQ topic for messages that fail N times |
| Hardcoded passwords | Insecure | Environment variables, Docker secrets, or vault |
| No authentication | Anyone can call the APIs | JWT tokens, API keys |
| Single Kafka partition | No parallelism | Multiple partitions + consumer instances |

### What to Add Next

1. **OpenTelemetry** — Distributed tracing across all 3 services (trace a request from order to inventory to status update)
2. **Health checks** — `/health` endpoint in each service that checks DB + Kafka + Redis connectivity
3. **Dockerfiles** — Containerize the .NET services for full Docker Compose deployment
4. **API Gateway** — Single entry point (YARP or Ocelot) that routes to StoreApi + OrderService
5. **Authentication** — JWT bearer tokens with a shared auth service
6. **Integration tests** — Test the full Kafka flow end-to-end
7. **Kubernetes deployment** — Deploy to your Docker Desktop K8s cluster (connects to your GitOps learning path)
