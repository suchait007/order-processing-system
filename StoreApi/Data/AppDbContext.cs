using Microsoft.EntityFrameworkCore;
using StoreApi.Models;

namespace StoreApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Schema is managed by Yuniql migrations (db/ folder)
        // EF Core is used only for querying — no migrations or seed data here

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasOne(p => p.Store)
                  .WithMany(s => s.Products)
                  .HasForeignKey(p => p.StoreId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
