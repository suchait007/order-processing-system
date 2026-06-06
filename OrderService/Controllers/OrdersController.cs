using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using OrderService.Services;

namespace OrderService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderDbContext _dbContext;
    private readonly RedisCacheService _cacheService;
    private readonly StoreApiClient _storeApiClient;
    private readonly KafkaProducerService _kafkaProducerService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        OrderDbContext dbContext,
        RedisCacheService cacheService,
        StoreApiClient storeApiClient,
        KafkaProducerService kafkaProducerService,
        ILogger<OrdersController> logger)
    {
        _dbContext = dbContext;
        _cacheService = cacheService;
        _storeApiClient = storeApiClient;
        _kafkaProducerService = kafkaProducerService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<OrderResponse>>> GetAll([FromQuery] string? status = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching orders with status filter {Status}", status);

        var query = _dbContext.Orders.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(order => order.Status == status);
        }

        var orders = await query
            .OrderByDescending(order => order.CreatedAt)
            .Select(order => MapOrder(order))
            .ToListAsync(cancellationToken);

        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching order {OrderId}", id);

        var cachedStatus = await _cacheService.GetOrderStatusAsync(id, cancellationToken);
        var order = await _dbContext.Orders.AsNoTracking().FirstOrDefaultAsync(current => current.Id == id, cancellationToken);

        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found", id);
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(cachedStatus))
        {
            order.Status = cachedStatus;
        }
        else
        {
            await _cacheService.SetOrderStatusAsync(order.Id, order.Status, cancellationToken);
        }

        return Ok(MapOrder(order));
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating order for ProductId={ProductId}, Quantity={Quantity}", request.ProductId, request.Quantity);

        var product = await _cacheService.GetProductInfoAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            product = await _storeApiClient.GetProductAsync(request.ProductId, cancellationToken);
            if (product is null)
            {
                _logger.LogWarning("Cannot create order because product {ProductId} was not found", request.ProductId);
                return NotFound(new { error = $"Product with ID {request.ProductId} not found." });
            }

            await _cacheService.SetProductInfoAsync(product.Id, product, cancellationToken: cancellationToken);
        }

        var order = new Order
        {
            ProductId = product.Id,
            ProductName = product.Name,
            Quantity = request.Quantity,
            UnitPrice = product.Price,
            TotalPrice = product.Price * request.Quantity,
            Status = "Pending",
            CustomerName = request.CustomerName,
            CustomerEmail = request.CustomerEmail,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheService.SetOrderStatusAsync(order.Id, order.Status, cancellationToken);
        await _kafkaProducerService.PublishOrderPlacedAsync(order, cancellationToken);

        _logger.LogInformation("Created order {OrderId} for product {ProductId}", order.Id, order.ProductId);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, MapOrder(order));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling order {OrderId}", id);

        var order = await _dbContext.Orders.FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found for cancellation", id);
            return NotFound();
        }

        order.Status = "Cancelled";
        order.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheService.SetOrderStatusAsync(order.Id, order.Status, cancellationToken);

        _logger.LogInformation("Cancelled order {OrderId}", id);
        return NoContent();
    }

    private static OrderResponse MapOrder(Order order) =>
        new(
            order.Id,
            order.ProductId,
            order.ProductName,
            order.Quantity,
            order.UnitPrice,
            order.TotalPrice,
            order.Status,
            order.CustomerName,
            order.CustomerEmail,
            order.CreatedAt,
            order.UpdatedAt);
}
