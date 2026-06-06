# KT-03: Tachyon Platform — NuGet Packages & Technology Stack
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Source:** `Platform/Platform/` codebase (paket.dependencies + .csproj files)  
**Audience:** New developers onboarding to the Platform team  
**Status:** Living Document

---

## Table of Contents
1. [Logging](#1-logging)
2. [HTTP / API / Auth](#2-http--api--auth)
3. [Database](#3-database)
4. [Messaging](#4-messaging)
5. [Monitoring / Observability](#5-monitoring--observability)
6. [Caching](#6-caching)
7. [Testing](#7-testing)
8. [Serialization](#8-serialization)
9. [Azure SDKs](#9-azure-sdks)
10. [Other Notable Frameworks](#10-other-notable-frameworks)
11. [Build & Tooling](#11-build--tooling)
12. [Quick Start — Recommended for New Projects](#12-quick-start--recommended-for-new-projects)

---

## 1. Logging

| Package | Version | Purpose |
|---|---|---|
| `Serilog.AspNetCore` | latest | Primary logging framework for ASP.NET Core |
| `Serilog.Enrichers.Thread` | latest | Adds ThreadId to every log entry |
| `Serilog.Formatting.Compact` | latest | JSON structured log output (machine-readable) |
| `Serilog.Sinks.Console` | latest | Writes logs to console/stdout |
| `log4net` | 3.3.0 / 2.0.15 | Legacy logging (older services) |
| `Log4net.Appender.Serilog` | latest | Bridges legacy log4net logs into Serilog pipeline |
| `Microsoft.Extensions.Logging` | framework | .NET built-in logging abstractions |
| `Microsoft.Extensions.Logging.Abstractions` | framework | ILogger interfaces |
| `Microsoft.Extensions.Logging.Console` | framework | Built-in console logger |
| `Microsoft.Extensions.Logging.ApplicationInsights` | 2.15.0 | Routes logs to Azure Application Insights |

**Architecture Note:**
- New services use **Serilog** directly
- Legacy services use **log4net** bridged into Serilog via `Log4net.Appender.Serilog`
- All services output structured JSON logs for centralized log aggregation

---

## 2. HTTP / API / Auth

| Package | Version | Purpose |
|---|---|---|
| `Swashbuckle.AspNetCore` | latest | Swagger/OpenAPI documentation |
| `Swashbuckle.AspNetCore.Annotations` | latest | Swagger attribute-based docs (e.g., `[SwaggerOperation]`) |
| `Swashbuckle.AspNetCore.Filters` | latest | Request/response example filters for Swagger |
| `Swashbuckle.AspNetCore.SwaggerGen` | latest | Swagger JSON generation |
| `Microsoft.AspNetCore.Mvc.NewtonsoftJson` | ~> 6.0.14 | Newtonsoft.Json as the JSON serializer for API responses |
| `Microsoft.AspNetCore.Mvc.Versioning` | 4.1.1 | API versioning support (`/v1/`, `/v2/`) |
| `Microsoft.Extensions.Http` | framework | IHttpClientFactory for outbound HTTP calls |
| `Microsoft.Extensions.Http.Polly` | latest | Polly integration for retry/circuit breaker on HTTP calls |
| `Microsoft.AspNetCore.Authentication.Certificate` | 3.1.7 | mTLS client certificate authentication |
| `Microsoft.AspNetCore.Authentication.AzureAD.UI` | 3.1.6 | Azure AD authentication |
| `Microsoft.IdentityModel.Abstractions` | >= 7.6.2 | JWT/token validation abstractions |
| `Microsoft.AspNetCore.Http.Abstractions` | framework | HttpContext, HttpRequest, HttpResponse interfaces |
| `Microsoft.AspNetCore.TestHost` | ~> 6.0.19 / 8.0.0 | In-memory test server for integration tests |

---

## 3. Database

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.EntityFrameworkCore` | ~> 8.0 | ORM — maps C# classes to database tables |
| `Microsoft.EntityFrameworkCore.Abstractions` | ~> 8.0 | EF Core interfaces |
| `Microsoft.EntityFrameworkCore.Relational` | ~> 8.0 | Relational database support (SQL generation) |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | ~> 8.0 | EF Core provider for PostgreSQL |
| `Npgsql` | 8.0.6 | Low-level PostgreSQL driver |
| `Dapper` | latest | Lightweight micro-ORM for raw SQL (used where EF Core is too heavy) |
| `Microsoft.Data.SqlClient` | latest | SQL Server ADO.NET driver |
| `EntityFramework` | legacy | Legacy EF6 (older services) |
| `Yuniql.Core` | ~> 1.3.15 | Database migration engine (core library) |
| `Yuniql.PostgreSql` | ~> 1.3.15 | Yuniql PostgreSQL provider |

**Architecture Note:**
- **SQL Server** services: database-per-tenant model, connection string per tenant
- **PostgreSQL** services: shared database, row-level tenant isolation (`tenant_id` column)
- **Yuniql** handles all schema migrations (raw SQL scripts, not EF migrations)
- **Dapper** used for performance-critical queries; **EF Core** for general CRUD

---

## 4. Messaging

| Package | Version | Purpose |
|---|---|---|
| `Confluent.Kafka` | 2.5.2 | Kafka producer/consumer client |
| `Chr.Avro.Confluent` | latest | Avro serialization for Kafka (schema registry integration) |
| `Apache.Avro` | latest | Avro serialization/deserialization |
| `Azure.Messaging.ServiceBus` | latest | Azure Service Bus messaging |
| `Azure.Data.SchemaRegistry` | latest | Azure Schema Registry for event schemas |
| `System.Memory.Data` | ~> 9.0 | Memory-efficient data handling for messaging |

**Architecture Note:**
- **Kafka** is the primary event streaming platform
- Events are keyed by `TenantId` for partitioning
- **Avro** is used for schema evolution in Kafka topics
- **Azure Service Bus** used for specific Azure-integrated workflows

---

## 5. Monitoring / Observability

| Package | Version | Purpose |
|---|---|---|
| `OpenTelemetry` | latest | Distributed tracing & metrics standard |
| `OpenTelemetry.Extensions.Hosting` | latest | Host integration for OpenTelemetry |
| `OpenTelemetry.Instrumentation.AspNetCore` | latest | Auto-instruments incoming HTTP requests |
| `OpenTelemetry.Instrumentation.Runtime` | latest | .NET runtime metrics (GC, threads, etc.) |
| `OpenTelemetry.Exporter.Console` | latest | Export traces to console (dev) |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | latest | Export traces via OTLP (to Jaeger, Grafana, etc.) |
| `Microsoft.ApplicationInsights.AspNetCore` | latest | Azure Application Insights APM |
| `Datadog.Trace.Bundle` | latest | Datadog APM tracing |

**Architecture Note:**
- **OpenTelemetry** is the standard for distributed tracing
- Traces are exported via **OTLP** to Grafana/Jaeger
- **Application Insights** used in Azure-hosted services
- **Datadog** used for some production monitoring

---

## 6. Caching

| Package | Version | Purpose |
|---|---|---|
| `StackExchange.Redis` | latest | Redis client (low-level) |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | latest | Redis as `IDistributedCache` |
| `Microsoft.Extensions.Caching.Abstractions` | framework | `IMemoryCache`, `IDistributedCache` interfaces |
| `System.Runtime.Caching` | framework | Legacy in-memory caching |

**Architecture Note:**
- **Redis** is used for distributed caching and pub/sub
- `IDistributedCache` abstraction allows swapping Redis for other providers
- Some services use `IMemoryCache` for lightweight local caching

---

## 7. Testing

| Package | Version | Purpose |
|---|---|---|
| `xunit` | latest | Primary unit test framework |
| `xunit.extensibility.core` | latest | xUnit extensibility |
| `xunit.runner.visualstudio` | latest | xUnit Visual Studio test runner |
| `Moq` | ~> 4.18 / 4.20 | Mocking framework |
| `FluentAssertions` | 6.7 / 6.12 / 7.2 | Readable assertion library (`result.Should().Be(...)`) |
| `Microsoft.NET.Test.Sdk` | 16.4–17.5 | Test SDK for `dotnet test` |
| `coverlet.collector` | 3.1.2 / 3.2.0 | Code coverage collection |
| `Testcontainers` | 3.5 | Spin up Docker containers for integration tests |
| `Testcontainers.PostgreSql` | ~> 3.1.0 | PostgreSQL container for tests |
| `Testcontainers.Redis` | ~> 3.1.0 | Redis container for tests |
| `Microsoft.AspNetCore.TestHost` | ~> 6.0 / 8.0 | In-memory test server |
| `NUnit` | 3.12.0 | Secondary test framework (some older tests) |
| `NUnit3TestAdapter` | 3.15.1 | NUnit Visual Studio adapter |
| `MSTest.TestAdapter` | 2.2.10 | MSTest adapter (some older tests) |
| `MSTest.TestFramework` | 2.2.10 | MSTest framework |
| `FsUnit.xUnit` | latest | F# unit testing extensions |

**Architecture Note:**
- **xUnit** is the standard for new tests
- **NUnit** and **MSTest** exist in legacy test projects
- **Testcontainers** used for integration tests with real PostgreSQL/Redis
- **FluentAssertions** preferred over raw `Assert.Equal()`

---

## 8. Serialization

| Package | Version | Purpose |
|---|---|---|
| `Newtonsoft.Json` | 12.0.3 – 13.0.4 | Primary JSON library (widely used) |
| `Microsoft.AspNetCore.Mvc.NewtonsoftJson` | ~> 6.0.14 | Newtonsoft as API JSON serializer |
| `System.Text.Json` | framework | Built-in .NET JSON (used in newer code) |
| `YamlDotNet` | 15.3.0 | YAML parsing/generation |
| `Microsoft.OData.Edm` | 7.10 | OData entity data model |
| `Simple.OData.Client` | latest | OData client for consuming OData APIs |

---

## 9. Azure SDKs

| Package | Version | Purpose |
|---|---|---|
| `Azure.Identity` | latest | Azure Managed Identity, DefaultAzureCredential |
| `Azure.Storage.Blobs` | latest | Azure Blob Storage |
| `Azure.Storage.Queues` | 12.4.2 | Azure Queue Storage |
| `Azure.Security.KeyVault.Secrets` | latest | Azure Key Vault — secrets |
| `Azure.Security.KeyVault.Keys` | latest | Azure Key Vault — encryption keys |
| `Azure.Security.KeyVault.Certificates` | latest | Azure Key Vault — certificates |
| `Azure.Data.Tables` | latest | Azure Table Storage |
| `Microsoft.AspNetCore.AzureAppServices.HostingStartup` | 3.1.8 | Azure App Service integration |

---

## 10. Other Notable Frameworks

| Package | Version | Purpose |
|---|---|---|
| `Polly` | latest | Resilience: retry, circuit breaker, timeout, bulkhead |
| `Polly.Extensions` | latest | DI integration for Polly policies |
| `KubernetesClient` | latest | Kubernetes API client (for K8s-aware services) |
| `MailKit` | latest | Email sending (SMTP) |
| `FirebaseAdmin` | latest | Firebase push notifications |
| `Giraffe` | 6.4.0 | F# web framework (some services written in F#) |
| `JetBrains.Annotations` | 2020.1.0 | Code analysis annotations |
| `System.CommandLine` | 2.0.0-beta1 | CLI argument parsing |
| `System.Drawing.Common` | latest | Image/graphics processing |

---

## 11. Build & Tooling

| Package / Tool | Version | Purpose |
|---|---|---|
| `Fake.Core.Target` | latest | F# Make — build automation |
| `Fake.DotNet.Cli` | latest | dotnet CLI integration for FAKE |
| `Fake.DotNet.NuGet` | latest | NuGet commands in FAKE |
| `Fake.Tools.Git` | latest | Git commands in FAKE |
| `Microsoft.Build` | 17.11.4 | MSBuild SDK |
| `Microsoft.Build.Framework` | 17.11.4 | MSBuild framework |
| `Microsoft.Build.Tasks.Core` | 17.11.4 | MSBuild tasks |
| `Microsoft.Build.Utilities.Core` | 17.11.4 | MSBuild utilities |
| `Paket` | (dependency manager) | Alternative to NuGet — used at repo root level |

---

## 12. Quick Start — Recommended for New Projects

Based on what the team actively uses, here's the baseline for a new ASP.NET Core service:

```bash
# Logging
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Enrichers.Thread
dotnet add package Serilog.Formatting.Compact
dotnet add package Serilog.Sinks.Console

# Database
dotnet add package Microsoft.EntityFrameworkCore.SqlServer  # or Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Dapper                                   # for raw SQL perf queries
dotnet add package Yuniql.Core                              # migrations

# API
dotnet add package Swashbuckle.AspNetCore                   # Swagger (included in .NET 8 template)
dotnet add package Microsoft.AspNetCore.Mvc.Versioning      # API versioning

# Resilience
dotnet add package Microsoft.Extensions.Http.Polly          # retry/circuit breaker
dotnet add package Polly

# Caching
dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis

# Observability
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol

# Testing
dotnet add package xunit
dotnet add package Moq
dotnet add package FluentAssertions
dotnet add package Testcontainers.PostgreSql
```

---

## References
- Source: `Platform/Platform/paket.dependencies`
- Source: `Platform/Platform/Server/Code/**/*.csproj`
- Source: `Platform/Platform/Cloud/Code/**/*.csproj`
