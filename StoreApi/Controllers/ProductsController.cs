using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StoreApi.Data;
using StoreApi.Models;

namespace StoreApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(AppDbContext db, ILogger<ProductsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductResponse>>> GetAll(
        [FromQuery] string? category = null,
        [FromQuery] int? storeId = null)
    {
        _logger.LogInformation("Fetching products with filters Category={Category}, StoreId={StoreId}", category, storeId);

        var query = _db.Products.Include(p => p.Store).AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(p => p.Category == category);

        if (storeId.HasValue)
            query = query.Where(p => p.StoreId == storeId.Value);

        var products = await query
            .Select(p => new ProductResponse(
                p.Id, p.Name, p.Description, p.Price, p.StockQuantity,
                p.Category, p.CreatedAt, p.StoreId, p.Store.Name))
            .ToListAsync();

        _logger.LogInformation("Retrieved {ProductCount} products", products.Count);
        return Ok(products);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductResponse>> GetById(int id)
    {
        _logger.LogInformation("Fetching product {ProductId}", id);

        var product = await _db.Products
            .Include(p => p.Store)
            .Where(p => p.Id == id)
            .Select(p => new ProductResponse(
                p.Id, p.Name, p.Description, p.Price, p.StockQuantity,
                p.Category, p.CreatedAt, p.StoreId, p.Store.Name))
            .FirstOrDefaultAsync();

        if (product is null)
        {
            _logger.LogWarning("Product {ProductId} not found", id);
            return NotFound();
        }

        return Ok(product);
    }

    [HttpPost]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request)
    {
        _logger.LogInformation("Creating product {ProductName} for store {StoreId}", request.Name, request.StoreId);

        var store = await _db.Stores.FindAsync(request.StoreId);
        if (store is null)
        {
            _logger.LogWarning("Cannot create product — store {StoreId} not found", request.StoreId);
            return BadRequest(new { error = $"Store with ID {request.StoreId} not found." });
        }

        var product = new Product
        {
            Name = request.Name,
            Description = request.Description,
            Price = request.Price,
            StockQuantity = request.StockQuantity,
            Category = request.Category,
            StoreId = request.StoreId
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created product {ProductId} ({ProductName}) in store {StoreId}",
            product.Id, product.Name, product.StoreId);

        var response = new ProductResponse(
            product.Id, product.Name, product.Description, product.Price,
            product.StockQuantity, product.Category, product.CreatedAt,
            product.StoreId, store.Name);

        return CreatedAtAction(nameof(GetById), new { id = product.Id }, response);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateProductRequest request)
    {
        _logger.LogInformation("Updating product {ProductId}", id);

        var product = await _db.Products.FindAsync(id);
        if (product is null)
        {
            _logger.LogWarning("Product {ProductId} not found for update", id);
            return NotFound();
        }

        var storeExists = await _db.Stores.AnyAsync(s => s.Id == request.StoreId);
        if (!storeExists)
        {
            _logger.LogWarning("Cannot update product {ProductId} — store {StoreId} not found", id, request.StoreId);
            return BadRequest(new { error = $"Store with ID {request.StoreId} not found." });
        }

        product.Name = request.Name;
        product.Description = request.Description;
        product.Price = request.Price;
        product.StockQuantity = request.StockQuantity;
        product.Category = request.Category;
        product.StoreId = request.StoreId;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated product {ProductId} ({ProductName})", id, request.Name);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        _logger.LogInformation("Deleting product {ProductId}", id);

        var product = await _db.Products.FindAsync(id);
        if (product is null)
        {
            _logger.LogWarning("Product {ProductId} not found for deletion", id);
            return NotFound();
        }

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted product {ProductId}", id);
        return NoContent();
    }
}
