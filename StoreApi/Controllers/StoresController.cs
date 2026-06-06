using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StoreApi.Data;
using StoreApi.Models;

namespace StoreApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StoresController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<StoresController> _logger;

    public StoresController(AppDbContext db, ILogger<StoresController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StoreResponse>>> GetAll()
    {
        _logger.LogInformation("Fetching all stores");

        var stores = await _db.Stores
            .Select(s => new StoreResponse(
                s.Id, s.Name, s.Address, s.City, s.Phone, s.CreatedAt,
                s.Products.Count))
            .ToListAsync();

        _logger.LogInformation("Retrieved {StoreCount} stores", stores.Count);
        return Ok(stores);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<StoreResponse>> GetById(int id)
    {
        _logger.LogInformation("Fetching store {StoreId}", id);

        var store = await _db.Stores
            .Where(s => s.Id == id)
            .Select(s => new StoreResponse(
                s.Id, s.Name, s.Address, s.City, s.Phone, s.CreatedAt,
                s.Products.Count))
            .FirstOrDefaultAsync();

        if (store is null)
        {
            _logger.LogWarning("Store {StoreId} not found", id);
            return NotFound();
        }

        return Ok(store);
    }

    [HttpPost]
    public async Task<ActionResult<StoreResponse>> Create(CreateStoreRequest request)
    {
        _logger.LogInformation("Creating store {StoreName}", request.Name);

        var store = new Store
        {
            Name = request.Name,
            Address = request.Address,
            City = request.City,
            Phone = request.Phone
        };

        _db.Stores.Add(store);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created store {StoreId} ({StoreName})", store.Id, store.Name);

        var response = new StoreResponse(
            store.Id, store.Name, store.Address, store.City, store.Phone,
            store.CreatedAt, 0);

        return CreatedAtAction(nameof(GetById), new { id = store.Id }, response);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateStoreRequest request)
    {
        _logger.LogInformation("Updating store {StoreId}", id);

        var store = await _db.Stores.FindAsync(id);
        if (store is null)
        {
            _logger.LogWarning("Store {StoreId} not found for update", id);
            return NotFound();
        }

        store.Name = request.Name;
        store.Address = request.Address;
        store.City = request.City;
        store.Phone = request.Phone;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated store {StoreId} ({StoreName})", id, request.Name);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        _logger.LogInformation("Deleting store {StoreId}", id);

        var store = await _db.Stores.FindAsync(id);
        if (store is null)
        {
            _logger.LogWarning("Store {StoreId} not found for deletion", id);
            return NotFound();
        }

        _db.Stores.Remove(store);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted store {StoreId}", id);
        return NoContent();
    }
}
