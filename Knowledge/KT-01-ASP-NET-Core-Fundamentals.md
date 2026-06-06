# KT-01: ASP.NET Core & .NET Core — Fundamentals
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers new to .NET ecosystem  
**Status:** Living Document

---

## Table of Contents
1. [.NET Core vs ASP.NET Core](#1-net-core-vs-aspnet-core)
2. [Program.cs — The Entry Point](#2-programcs--the-entry-point)
3. [Dependency Injection (DI)](#3-dependency-injection-di)
4. [Middleware Pipeline](#4-middleware-pipeline)
5. [Controllers — API Logic](#5-controllers--api-logic)
6. [Models & DTOs](#6-models--dtos)
7. [Entity Framework Core (EF Core)](#7-entity-framework-core-ef-core)
8. [Yuniql — Database Migrations](#8-yuniql--database-migrations)
9. [Swagger — API Documentation](#9-swagger--api-documentation)
10. [Configuration System](#10-configuration-system)
11. [HTTPS & Development Certificates](#11-https--development-certificates)
12. [Ports & Launch Settings](#12-ports--launch-settings)
13. [Request Lifecycle Summary](#13-request-lifecycle-summary)

---

## 1. .NET Core vs ASP.NET Core

```
.NET Core (now just ".NET")
├── The runtime + base libraries (like JVM for Java)
├── Cross-platform (Windows, Linux, Mac)
└── You can build: console apps, desktop apps, web apps, APIs, etc.

ASP.NET Core
├── A web framework that runs ON TOP of .NET
├── Like Express.js for Node, or Spring Boot for Java
└── Handles HTTP, routing, middleware, controllers, etc.
```

- `.NET 8` = the runtime
- `ASP.NET Core` = the web framework
- They are separate but used together for web APIs

---

## 2. Program.cs — The Entry Point

```csharp
var builder = WebApplication.CreateBuilder(args);
```

This is where **everything starts**. It creates a `WebApplicationBuilder` which does:
- Sets up configuration (reads `appsettings.json`)
- Sets up dependency injection (DI) container
- Sets up logging
- Detects the environment (Development/Production)

Think of it like `new Express()` in Node.js or `SpringApplication.run()` in Java.

### Service Registration (before `Build()`)

```csharp
builder.Services.AddControllers();               // Register MVC controllers
builder.Services.AddEndpointsApiExplorer();       // API metadata for Swagger
builder.Services.AddSwaggerGen();                 // Swagger doc generation
builder.Services.AddDbContext<AppDbContext>(...);  // EF Core database
```

This is **Dependency Injection (DI)** registration. You tell the framework:
> "When someone asks for an `AppDbContext`, here's how to create one."

### Middleware Pipeline (after `Build()`)

```csharp
var app = builder.Build();

app.UseSwagger();           // Middleware 1: Serve Swagger JSON
app.UseSwaggerUI();         // Middleware 2: Serve Swagger UI
app.UseHttpsRedirection();  // Middleware 3: Redirect HTTP → HTTPS
app.UseAuthorization();     // Middleware 4: Check auth policies
app.MapControllers();       // Middleware 5: Route to controllers

app.Run();                  // Start listening for requests
```

---

## 3. Dependency Injection (DI)

This is **the most important pattern** in ASP.NET Core. Everything uses it.

```csharp
// Registration (Program.cs):
builder.Services.AddDbContext<AppDbContext>(...);

// Resolution (Controller constructor):
public StoresController(AppDbContext db) => _db = db;
```

You **never** do `new AppDbContext()`. The framework:
1. Sees the controller needs `AppDbContext`
2. Creates one (or reuses an existing one for this request)
3. Passes it to the constructor
4. Disposes it when the request ends

### Three Lifetimes

| Lifetime | Meaning | Example |
|---|---|---|
| `Scoped` | One instance per HTTP request | `DbContext` (default) |
| `Transient` | New instance every time it's requested | Lightweight stateless services |
| `Singleton` | One instance for the entire app lifetime | Caches, configuration |

---

## 4. Middleware Pipeline

**Middleware** = a chain of functions that every HTTP request passes through, in order:

```
Request → Swagger → HTTPS → Auth → Routing → Controller → Response
```

Each middleware can:
- Pass the request to the next one
- Short-circuit (e.g., return 401 immediately)
- Modify the request/response

**Order matters!** Authentication must come before Authorization, etc.

---

## 5. Controllers — API Logic

```csharp
[ApiController]
[Route("api/[controller]")]
public class StoresController : ControllerBase
```

| Attribute | What It Does |
|---|---|
| `[ApiController]` | Enables API behaviors: auto model validation, auto 400 on bad input, binding from body by default |
| `[Route("api/[controller]")]` | Sets the URL pattern. `[controller]` is replaced by the class name minus "Controller" → `api/stores` |
| `: ControllerBase` | Base class for API controllers (no view support — that's `Controller` for MVC) |

### Action Methods (HTTP Verb Mapping)

```csharp
[HttpGet]           // GET /api/stores
[HttpGet("{id}")]   // GET /api/stores/5
[HttpPost]          // POST /api/stores
[HttpPut("{id}")]   // PUT /api/stores/5
[HttpDelete("{id}")]// DELETE /api/stores/5
```

These attributes map **HTTP methods + URL patterns** to C# methods. ASP.NET Core handles:
- Parsing the `{id}` from the URL → passing it as a method parameter
- Deserializing the JSON request body → into your DTO object
- Serializing your return object → back to JSON

### Return Types

```csharp
return Ok(stores);                                            // 200 + JSON body
return NotFound();                                            // 404
return NoContent();                                           // 204 (success, no body)
return BadRequest(new { error = "..." });                     // 400
return CreatedAtAction(nameof(GetById), new { id }, response); // 201 + Location header
```

`CreatedAtAction` returns **201 Created** and adds a `Location: /api/stores/3` header pointing to the new resource. REST best practice.

---

## 6. Models & DTOs

### Entity (what's in the database)

```csharp
public class Store
{
    public int Id { get; set; }          // EF Core sees "Id" → makes it the primary key

    [Required]                           // Validation: can't be null/empty
    [MaxLength(200)]                     // Validation: max 200 chars
    public string Name { get; set; }
}
```

### DTOs (what goes over the wire)

```csharp
public record CreateStoreRequest(string Name, string? Address, ...);
public record StoreResponse(int Id, string Name, ..., int ProductCount);
```

**Why separate DTOs from entities?**
- Entity has navigation properties (`Products` collection) — you don't want infinite JSON loops
- Request DTO omits `Id` (server assigns it), `CreatedAt` (server sets it)
- Response DTO adds computed fields (`ProductCount`) that aren't in the DB

`record` = immutable class with built-in equality, `ToString()`, and deconstruction. Perfect for DTOs.

---

## 7. Entity Framework Core (EF Core)

### What is it?

An **Object-Relational Mapper** — maps C# classes to database tables:

```
C# Class     →    SQL Table
Store        →    [dbo].[Stores]
Product      →    [dbo].[Products]
property     →    column
```

### DbContext — The Database Session

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();
}
```

`DbSet<Store>` = the `Stores` table. It's queryable:

```csharp
// This C#:
await _db.Stores.Where(s => s.City == "Berlin").ToListAsync();

// Becomes this SQL:
SELECT * FROM Stores WHERE City = 'Berlin';
```

EF Core translates **LINQ queries** → **SQL** automatically. You write C#, it writes SQL.

### Relationships

```csharp
entity.HasOne(p => p.Store)          // A Product has ONE Store
      .WithMany(s => s.Products)     // A Store has MANY Products
      .HasForeignKey(p => p.StoreId) // FK column
      .OnDelete(DeleteBehavior.Cascade); // Delete store → delete its products
```

### How EF Core Connects to the DB

```
appsettings.json → ConnectionString
    ↓
Program.cs → builder.Services.AddDbContext<AppDbContext>(options =>
                 options.UseSqlServer(connectionString))
    ↓
DI Container creates AppDbContext per HTTP request
    ↓
Controller receives it via constructor injection
    ↓
Controller uses _db.Stores.Where(...) to query
    ↓
EF Core translates to SQL, sends to SQL Server, maps results back
```

---

## 8. Yuniql — Database Migrations

### Why Yuniql instead of EF Core Migrations?

EF Core can do migrations too (C# migration classes), but Yuniql uses **raw SQL scripts** — giving full control and no ORM translation surprises.

### Folder Structure

```
db/
├── _init/          # Runs once, before everything (rarely used)
├── _pre/           # Runs before every migration run
├── v0.00/          # Version 0.00 — initial schema
│   └── 01-create-tables.sql
├── v0.01/          # Version 0.01 — next change (you'd create this)
│   └── 01-add-column.sql
├── _post/          # Runs after every migration run
├── _draft/         # Work-in-progress (not versioned)
└── _erase/         # Tear-down scripts
```

### How It Works on Startup

1. Yuniql checks `__yuniql_schema_version` table in the DB
2. Sees which versions are already applied (e.g., `v0.00` ✅)
3. Runs any new versions in order (e.g., `v0.01`, `v0.02`...)
4. Records each version in the tracking table

### Integration in Program.cs

```csharp
app.UseYuniql(
    new SqlServerDataService(traceService),
    new SqlServerBulkImportService(traceService),
    traceService,
    new Configuration
    {
        Platform = "sqlserver",
        Workspace = yuniqlWorkspacePath,
        ConnectionString = connectionString,
        IsAutoCreateDatabase = true
    });
```

---

## 9. Swagger — API Documentation

```csharp
builder.Services.AddEndpointsApiExplorer(); // Discovers all endpoints
builder.Services.AddSwaggerGen();           // Generates OpenAPI spec

app.UseSwagger();    // Serves /swagger/v1/swagger.json
app.UseSwaggerUI();  // Serves /swagger/index.html (the interactive UI)
```

Swagger reads your controllers, attributes, and DTOs and auto-generates:
- Interactive API documentation
- A "Try it out" button to test APIs from the browser
- Request/response schemas

---

## 10. Configuration System

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost,1433;Database=StoreApiDb;..."
  }
}
```

ASP.NET Core has a **layered configuration system** (lowest to highest priority):

```
appsettings.json              (base, all environments)
appsettings.Development.json  (overrides for dev)
Environment variables         (overrides for production)
Command-line args             (highest priority)
```

Access in code:
```csharp
builder.Configuration.GetConnectionString("DefaultConnection")
```

---

## 11. HTTPS & Development Certificates

When you create a new ASP.NET Core project, it:
- Adds `app.UseHttpsRedirection()` — redirects HTTP → HTTPS
- On first run, uses a **self-signed development SSL certificate**
- Visual Studio prompts you to **trust** this dev cert

Management commands:
```bash
dotnet dev-certs https --trust    # trust the dev cert
dotnet dev-certs https --clean    # remove it
dotnet dev-certs https --check    # check if it exists
```

---

## 12. Ports & Launch Settings

There is **no fixed default port**. Each new project gets a randomly assigned port, stored in:

```
Properties/launchSettings.json
```

```json
"profiles": {
  "http": {
    "applicationUrl": "http://localhost:5116"
  },
  "https": {
    "applicationUrl": "https://localhost:7xxx;http://localhost:5116"
  }
}
```

- `http` profile: random port in **5000–5300** range
- `https` profile: random port in **7000–7300** range
- Without `launchSettings.json`, Kestrel defaults to **5000 (http)** and **5001 (https)**

---

## 13. Request Lifecycle Summary

```
1. Browser/Client sends: GET /api/stores/1

2. Kestrel (built-in web server) receives the request

3. Middleware pipeline runs:
   UseSwagger      → not /swagger, pass through
   UseHttpsRedirection → already HTTPS, pass through
   UseAuthorization → no [Authorize], pass through
   MapControllers  → matches StoresController.GetById(1)

4. DI creates StoresController, injects AppDbContext

5. GetById(1) runs:
   _db.Stores.Where(s => s.Id == 1)...
   → EF Core generates: SELECT * FROM Stores WHERE Id = 1
   → SQL Server returns row
   → EF Core maps to Store object
   → Controller maps to StoreResponse DTO

6. Return Ok(response)
   → ASP.NET serializes to JSON
   → Response: 200 { "id": 1, "name": "Downtown Electronics", ... }
```
