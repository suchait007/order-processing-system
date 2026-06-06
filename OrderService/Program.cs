using System.Net;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Services;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using Serilog.Events;
using Yuniql.AspNetCore;
using Yuniql.PostgreSql;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting OrderService application");

    var builder = WebApplication.CreateBuilder(args);

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
            path: "logs/orderservice-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Thread:{ThreadId}] [Process:{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            new Serilog.Formatting.Compact.CompactJsonFormatter(),
            path: "logs/orderservice-json-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7));

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddDbContext<OrderDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:Configuration"];
    });

    builder.Services.AddSingleton<KafkaProducerService>();
    builder.Services.AddSingleton<RedisCacheService>();
    builder.Services.AddHostedService<KafkaConsumerService>();

    var storeApiBaseUrl = builder.Configuration["StoreApi:BaseUrl"]
        ?? throw new InvalidOperationException("StoreApi base URL is not configured.");

    var retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    builder.Services.AddHttpClient<StoreApiClient>(client =>
        {
            client.BaseAddress = new Uri(storeApiBaseUrl);
            client.DefaultRequestHeaders.Accept.Clear();
        })
        .AddPolicyHandler(retryPolicy);

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms";
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            if (ex is not null) return LogEventLevel.Error;
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

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
    var yuniqlWorkspacePath = Path.Combine(AppContext.BaseDirectory, "db");
    var yuniqlTraceService = new ConsoleTraceService { IsDebugEnabled = app.Environment.IsDevelopment() };

    app.UseYuniql(
        new PostgreSqlDataService(yuniqlTraceService),
        new PostgreSqlBulkImportService(yuniqlTraceService),
        yuniqlTraceService,
        new Configuration
        {
            Platform = "postgresql",
            Workspace = yuniqlWorkspacePath,
            ConnectionString = connectionString,
            IsAutoCreateDatabase = true,
            IsDebug = app.Environment.IsDevelopment()
        });

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
