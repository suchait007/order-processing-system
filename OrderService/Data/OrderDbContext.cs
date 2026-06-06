using Microsoft.EntityFrameworkCore;
using OrderService.Models;

namespace OrderService.Data;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");

            entity.HasKey(order => order.Id);

            entity.Property(order => order.Id).HasColumnName("id");
            entity.Property(order => order.ProductId).HasColumnName("product_id");
            entity.Property(order => order.ProductName).HasColumnName("product_name").HasMaxLength(200);
            entity.Property(order => order.Quantity).HasColumnName("quantity");
            entity.Property(order => order.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2);
            entity.Property(order => order.TotalPrice).HasColumnName("total_price").HasPrecision(18, 2);
            entity.Property(order => order.Status).HasColumnName("status").HasMaxLength(50);
            entity.Property(order => order.CustomerName).HasColumnName("customer_name").HasMaxLength(200);
            entity.Property(order => order.CustomerEmail).HasColumnName("customer_email").HasMaxLength(200);
            entity.Property(order => order.CreatedAt).HasColumnName("created_at");
            entity.Property(order => order.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
