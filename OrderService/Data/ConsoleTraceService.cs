using Yuniql.Extensibility;

namespace OrderService.Data;

public class ConsoleTraceService : ITraceService
{
    public bool IsDebugEnabled { get; set; }
    public bool IsTraceSensitiveData { get; set; }
    public bool IsTraceToDirectory { get; set; }
    public bool IsTraceToFile { get; set; }
    public string TraceDirectory { get; set; } = string.Empty;

    public void Debug(string message, object? payload = null)
    {
        if (IsDebugEnabled)
        {
            Console.WriteLine($"[DBG] {message}");
        }
    }

    public void Info(string message, object? payload = null) =>
        Console.WriteLine($"[INF] {message}");

    public void Warn(string message, object? payload = null) =>
        Console.WriteLine($"[WRN] {message}");

    public void Error(string message, object? payload = null) =>
        Console.WriteLine($"[ERR] {message}");

    public void Success(string message, object? payload = null) =>
        Console.WriteLine($"[OK ] {message}");
}
