using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests.Infrastructure;

public class Product
{
    public string Sku { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class GateDbContext(DbContextOptions<GateDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => this.Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("Products");
            e.HasKey(x => x.Sku);
            e.Property(x => x.Sku).HasMaxLength(40).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
        });
}

/// <summary>A second context sharing a database, to prove gate markers are per context type.</summary>
public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options);

[DbContext(typeof(GateDbContext))]
[Migration("20260101000000_Initial")]
public class InitialMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateTable(
            name: "Products",
            columns: table => new
            {
                Sku = table.Column<string>(maxLength: 40, nullable: false),
                Name = table.Column<string>(maxLength: 100, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Products", x => x.Sku));

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("Products");
}
