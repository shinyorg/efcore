using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Shiny.EntityFrameworkCore.Seeding.Tests.Infrastructure;

public class Country
{
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class Setting
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
}

public class SeedDbContext(DbContextOptions<SeedDbContext> options) : DbContext(options)
{
    public DbSet<Country> Countries => this.Set<Country>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Country>(e =>
        {
            e.ToTable("Countries");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(10).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
        });
}

/// <summary>A second context type, so per-context hosted seeding can be proven.</summary>
public class OtherDbContext(DbContextOptions<OtherDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => this.Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(50).ValueGeneratedNever();
            e.Property(x => x.Value).HasMaxLength(100);
        });
}

/// <summary>A hand-written, provider-neutral migration so <c>Database.Migrate</c> has real work to do.</summary>
[DbContext(typeof(SeedDbContext))]
[Migration("20260101000000_Initial")]
public class InitialMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateTable(
            name: "Countries",
            columns: table => new
            {
                Code = table.Column<string>(maxLength: 10, nullable: false),
                Name = table.Column<string>(maxLength: 100, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Countries", x => x.Code));

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("Countries");
}
