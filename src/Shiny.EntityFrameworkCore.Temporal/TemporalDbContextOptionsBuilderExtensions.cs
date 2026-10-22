using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Shiny.EntityFrameworkCore.Temporal.Internal;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// Options-builder extensions that switch on history recording for a context.
/// </summary>
public static class TemporalDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Installs the interceptors that write history for entities configured with <c>HasHistory()</c> and guard
    /// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> against them. Calling it again (for example from both
    /// <c>AddDbContext</c> and <c>OnConfiguring</c>) updates the settings without installing the interceptors twice.
    /// </summary>
    public static DbContextOptionsBuilder UseEntityHistory(this DbContextOptionsBuilder builder, Action<TemporalHistoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existing = builder.Options.FindExtension<TemporalHistoryOptionsExtension>();
        var options = existing?.Options.Clone() ?? new TemporalHistoryOptions();
        configure?.Invoke(options);
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(TemporalHistoryOptions.TimeProvider));
        ArgumentNullException.ThrowIfNull(options.SerializerOptions, nameof(TemporalHistoryOptions.SerializerOptions));

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new TemporalHistoryOptionsExtension(options));
        if (existing == null)
            builder.AddInterceptors(HistoryInterceptor.Instance, BulkWriteGuardInterceptor.Instance);

        return builder;
    }

    /// <inheritdoc cref="UseEntityHistory(DbContextOptionsBuilder, Action{TemporalHistoryOptions}?)"/>
    public static DbContextOptionsBuilder<TContext> UseEntityHistory<TContext>(this DbContextOptionsBuilder<TContext> builder, Action<TemporalHistoryOptions>? configure = null)
        where TContext : DbContext
    {
        UseEntityHistory((DbContextOptionsBuilder)builder, configure);
        return builder;
    }
}
