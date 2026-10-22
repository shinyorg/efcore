using System.ComponentModel;
using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.AI.Tests.Fixtures;

public enum OrderStatus
{
    Pending,
    Shipped,
    Delivered,
    Cancelled
}

[Description("A customer order")]
public class Order
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";

    [Description("Customer display name")]
    public string CustomerName { get; set; } = "";

    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public int Quantity { get; set; }
    public double? Rating { get; set; }
    public DateTime PlacedAt { get; set; }
    public string? Notes { get; set; }
    public string? Secret { get; set; }
    public Address ShipTo { get; set; } = new();
    public byte[]? Attachment { get; set; }

    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
}

/// <summary>Mapped as an EF complex type.</summary>
public class Address
{
    public string City { get; set; } = "";
    public string? Zip { get; set; }
}

public class Customer
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public ContactInfo? Contact { get; set; }
    public List<Order> Orders { get; set; } = new();
}

/// <summary>Mapped as an EF owned type.</summary>
public class ContactInfo
{
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

/// <summary>Keyless — readable, never addressable by id.</summary>
public class SalesSummary
{
    public string TenantId { get; set; } = "";
    public decimal Total { get; set; }
}

/// <summary>Composite key — query is fine, id-based tools are refused.</summary>
public class OrderTag
{
    public int OrderId { get; set; }
    public string Tag { get; set; } = "";
}

/// <summary>Has no parameterless constructor — insert is refused at startup.</summary>
public class Invoice
{
    public Invoice(string number) => this.Number = number;

    public int Id { get; set; }
    public string Number { get; set; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => this.Set<Order>();
    public DbSet<Customer> Customers => this.Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.TenantId).HasMaxLength(64);
            e.Property(o => o.CustomerName).HasMaxLength(100);
            e.Property(o => o.Total).HasPrecision(18, 2).HasComment("Order total in USD");
            e.Property<string>("AuditTag").HasMaxLength(32);   // shadow — never exposed
            e.ComplexProperty(o => o.ShipTo, a =>
            {
                a.Property(x => x.City).HasMaxLength(64);
                a.Property(x => x.Zip).HasMaxLength(16);
            });
            e.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable(t => t.HasComment("People who buy things"));
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.TenantId).HasMaxLength(64);
            e.Property(c => c.Name).HasMaxLength(100);
            e.OwnsOne(c => c.Contact, c =>
            {
                c.Property(x => x.Email).HasMaxLength(100);
                c.Property(x => x.Phone).HasMaxLength(32);
            });
        });

        modelBuilder.Entity<SalesSummary>(e =>
        {
            e.HasNoKey();
            e.ToView("SalesSummaries");
            e.Property(x => x.Total).HasPrecision(18, 2);
        });

        modelBuilder.Entity<OrderTag>(e =>
        {
            e.HasKey(x => new { x.OrderId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(32);
        });

        modelBuilder.Entity<Invoice>(e => e.Property(x => x.Number).HasMaxLength(32));
    }
}
