using Microsoft.EntityFrameworkCore.Infrastructure;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Infrastructure;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// Turns on the Shiny SQLite features inside <c>UseSqlite(..., o =&gt; ...)</c>. Each is independent: spatial needs no
/// native code, full-text uses SQLite's built-in FTS5, and only vectors load the bundled <c>sqlite-vec</c> binary.
/// </summary>
public static class SqliteMobileDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// NetTopologySuite geometry columns (EWKB in a <c>BLOB</c>), LINQ translation of NTS predicates and the geodesic
    /// <c>EF.Functions.GeoDistanceMeters</c>/<c>IsWithinMeters</c> helpers, and R*Tree spatial indexes - all without
    /// <c>mod_spatialite</c>, so it runs on iOS, Android and Mac Catalyst. Use this instead of EF's
    /// <c>UseNetTopologySuite()</c>.
    /// </summary>
    public static SqliteDbContextOptionsBuilder UseShinySpatial(this SqliteDbContextOptionsBuilder builder)
        => Update(builder, e => e.WithSpatial());

    /// <summary>
    /// <c>float[]</c>/<c>ReadOnlyMemory&lt;float&gt;</c> vector columns, <c>EF.Functions.VectorDistance</c>, <c>vec0</c>
    /// vector indexes and <c>NearestTo</c> KNN queries, backed by the bundled sqlite-vec. Registers sqlite-vec as a
    /// SQLite auto-extension immediately, so it is available on every connection opened from now on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sqlite-vec native binary could not be loaded for this platform.</exception>
    public static SqliteDbContextOptionsBuilder UseShinyVectors(this SqliteDbContextOptionsBuilder builder)
    {
        SqliteVec.RegisterAutoExtension();
        return Update(builder, e => e.WithVectors());
    }

    /// <summary>
    /// FTS5 external-content full-text indexes (<c>HasFullTextIndex</c>) with <c>EF.Functions.FullTextMatch</c> and
    /// <c>EF.Functions.FullTextRank</c>.
    /// </summary>
    public static SqliteDbContextOptionsBuilder UseShinyFullText(this SqliteDbContextOptionsBuilder builder)
        => Update(builder, e => e.WithFullText());

    static SqliteDbContextOptionsBuilder Update(SqliteDbContextOptionsBuilder builder, Func<SqliteMobileOptionsExtension, SqliteMobileOptionsExtension> change)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var optionsBuilder = ((IRelationalDbContextOptionsBuilderInfrastructure)builder).OptionsBuilder;
        var extension = optionsBuilder.Options.FindExtension<SqliteMobileOptionsExtension>() ?? new SqliteMobileOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(change(extension));
        return builder;
    }
}
