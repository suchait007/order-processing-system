using InventoryWorker.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting InventoryWorker application");

    var builder = Host.CreateDefaultBuilder(args);

    builder.UseSerilog((context, services, configuration) => configuration
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
            path: "logs/inventory-worker-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Thread:{ThreadId}] [Process:{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            new Serilog.Formatting.Compact.CompactJsonFormatter(),
            path: "logs/inventory-worker-json-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7));

    builder.ConfigureServices((context, services) =>
    {
        // Redis distributed cache
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = context.Configuration["Redis:Configuration"];
        });

        // Services
        services.AddSingleton<KafkaProducerService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<RedisCacheService>();

        // Background Kafka consumer
        services.AddHostedService<OrderPlacedConsumer>();
    });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "InventoryWorker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
