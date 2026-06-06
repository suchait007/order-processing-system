using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using StoreApi.Data;
using Yuniql.AspNetCore;
using Yuniql.SqlServer;

// ──────────────────────────────────────────────
// Bootstrap logger — catches startup errors before the host is built
// ──────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting StoreApi application");

    var builder = WebApplication.CreateBuilder(args);

    // ──────────────────────────────────────────────
    // Replace default .NET logging with Serilog
    // ──────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithThreadId()
        .Enrich.WithThreadName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .Enrich.WithProcessId()
        .Enrich.WithProcessName()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{ThreadId:00}] {SourceContext}{NewLine}      {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/storeapi-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Thread:{ThreadId}] [Process:{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            new Serilog.Formatting.Compact.CompactJsonFormatter(),
            path: "logs/storeapi-json-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
    );

    // Add services to the container.
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Register EF Core (for querying only — schema managed by Yuniql)
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

    var app = builder.Build();

    // ──────────────────────────────────────────────
    // Serilog request logging middleware
    // Replaces default Microsoft request logging with a single structured log per request
    // ──────────────────────────────────────────────
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms";
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            if (ex != null) return LogEventLevel.Error;
            if (httpContext.Response.StatusCode >= 500) return LogEventLevel.Error;
            if (httpContext.Response.StatusCode >= 400) return LogEventLevel.Warning;
            if (elapsed > 3000) return LogEventLevel.Warning;
            return LogEventLevel.Information;
        };
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
            diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        };
    });

    // Run Yuniql migrations on startup
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
    var yuniqlWorkspacePath = Path.Combine(AppContext.BaseDirectory, "db");

    var yuniqlTraceService = new ConsoleTraceService { IsDebugEnabled = app.Environment.IsDevelopment() };

    app.UseYuniql(
        new SqlServerDataService(yuniqlTraceService),
        new SqlServerBulkImportService(yuniqlTraceService),
        yuniqlTraceService,
        new Configuration
        {
            Platform = "sqlserver",
            Workspace = yuniqlWorkspacePath,
            ConnectionString = connectionString,
            IsAutoCreateDatabase = true,
            IsDebug = app.Environment.IsDevelopment()
        });

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
