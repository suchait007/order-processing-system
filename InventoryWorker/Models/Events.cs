namespace InventoryWorker.Models;

// Consumed from OrderService via "order-placed" topic
public sealed record OrderPlacedEvent(
    Guid OrderId,
    int ProductId,
    int Quantity,
    DateTime Timestamp);

// Published to "inventory-updated" topic — consumed by OrderService
public sealed record InventoryUpdatedEvent(
    Guid OrderId,
    int ProductId,
    string Status,
    bool InStock,
    int RemainingStock,
    DateTime Timestamp);

// Published to "low-stock-alert" topic
public sealed record LowStockAlertEvent(
    int ProductId,
    string ProductName,
    int CurrentStock,
    int Threshold,
    DateTime Timestamp);
