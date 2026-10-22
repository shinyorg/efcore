using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Carries <see cref="TemporalHistoryOptions"/> on the context options, so the (stateless, shared) interceptors
/// read the settings of the context they are running for rather than capturing one context's delegates.
/// </summary>
sealed class TemporalHistoryOptionsExtension(TemporalHistoryOptions options) : IDbContextOptionsExtension
{
    DbContextOptionsExtensionInfo? info;

    public TemporalHistoryOptions Options { get; } = options;

    public DbContextOptionsExtensionInfo Info => this.info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    public static TemporalHistoryOptions? Find(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<TemporalHistoryOptionsExtension>()?.Options;

    sealed class ExtensionInfo(TemporalHistoryOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        new TemporalHistoryOptionsExtension Extension => (TemporalHistoryOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => this.Extension.Options.AllowBulkWrites ? "EntityHistory(AllowBulkWrites) " : "EntityHistory ";

        // The bulk-write guard runs at query compilation, and compiled queries are cached per internal service
        // provider. Contexts that disagree on AllowBulkWrites must not share that cache, or one context's
        // permitted ExecuteDelete would be replayed from cache in another without passing the guard.
        public override int GetServiceProviderHashCode() => this.Extension.Options.AllowBulkWrites.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && o.Extension.Options.AllowBulkWrites == this.Extension.Options.AllowBulkWrites;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["Shiny:EntityHistory:AllowBulkWrites"] = this.Extension.Options.AllowBulkWrites.ToString();
    }
}
