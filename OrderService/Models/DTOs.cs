using System.ComponentModel.DataAnnotations;

namespace OrderService.Models;

public record CreateOrderRequest(
    [Range(1, int.MaxValue)] int ProductId,
    [Range(1, int.MaxValue)] int Quantity,
    [MaxLength(200)] string? CustomerName,
    [EmailAddress][MaxLength(200)] string? CustomerEmail);

public record OrderResponse(
    Guid Id,
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    string Status,
    string? CustomerName,
    string? CustomerEmail,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record ProductInfo(
    int Id,
    string Name,
    decimal Price,
    int StockQuantity,
    string StoreName);
