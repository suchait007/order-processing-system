using System.ComponentModel.DataAnnotations;

namespace StoreApi.Models;

// --- Store DTOs ---

public record CreateStoreRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(500)] string? Address,
    [MaxLength(100)] string? City,
    [MaxLength(20)] string? Phone
);

public record UpdateStoreRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(500)] string? Address,
    [MaxLength(100)] string? City,
    [MaxLength(20)] string? Phone
);

public record StoreResponse(
    int Id,
    string Name,
    string? Address,
    string? City,
    string? Phone,
    DateTime CreatedAt,
    int ProductCount
);

// --- Product DTOs ---

public record CreateProductRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(1000)] string? Description,
    decimal Price,
    int StockQuantity,
    [MaxLength(100)] string? Category,
    int StoreId
);

public record UpdateProductRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(1000)] string? Description,
    decimal Price,
    int StockQuantity,
    [MaxLength(100)] string? Category,
    int StoreId
);

public record ProductResponse(
    int Id,
    string Name,
    string? Description,
    decimal Price,
    int StockQuantity,
    string? Category,
    DateTime CreatedAt,
    int StoreId,
    string StoreName
);
