using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Vectors;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Infrastructure;

/// <summary>Which of the three features a context turned on.</summary>
sealed record SqliteMobileFeatures(bool Spatial, bool Vectors, bool FullText)
{
    public bool Any => this.Spatial || this.Vectors || this.FullText;
}

/// <summary>
/// One options extension carries all three feature flags, so the shared pieces (sidecar conventions, migrations SQL
/// generator, query rewriter) are registered exactly once however many features are enabled, while each feature's
/// own type mappings / translators are only added when that feature is on.
/// </summary>
sealed class SqliteMobileOptionsExtension : IDbContextOptionsExtension
{
    SqliteMobileOptionsExtension(SqliteMobileFeatures features) => this.Features = features;

    public SqliteMobileOptionsExtension() : this(new SqliteMobileFeatures(false, false, false))
    {
    }

    public SqliteMobileFeatures Features { get; }

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public SqliteMobileOptionsExtension WithSpatial() => new(this.Features with { Spatial = true });
    public SqliteMobileOptionsExtension WithVectors() => new(this.Features with { Vectors = true });
    public SqliteMobileOptionsExtension WithFullText() => new(this.Features with { FullText = true });

    public void ApplyServices(IServiceCollection services)
    {
        var builder = new EntityFrameworkRelationalServicesBuilder(services);
        builder.TryAdd<ISingletonOptions, SqliteMobileFeaturesOptions>(p => p.GetRequiredService<SqliteMobileFeaturesOptions>());
        services.TryAddSingleton<SqliteMobileFeaturesOptions>();

        builder.TryAdd<IConventionSetPlugin, SidecarConventionSetPlugin>();
        if (this.Features.Spatial)
        {
            builder.TryAdd<IRelationalTypeMappingSourcePlugin, GeometryTypeMappingSourcePlugin>();
            builder.TryAdd<IMethodCallTranslatorPlugin, SpatialMethodCallTranslatorPlugin>();
            builder.TryAdd<IMemberTranslatorPlugin, SpatialMemberTranslatorPlugin>();
        }

        if (this.Features.Vectors)
        {
            builder.TryAdd<IRelationalTypeMappingSourcePlugin, VectorTypeMappingSourcePlugin>();
            builder.TryAdd<IMethodCallTranslatorPlugin, VectorMethodCallTranslatorPlugin>();
        }

        builder.TryAdd<IMethodCallTranslatorPlugin, SidecarMethodCallTranslatorPlugin>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IInterceptor, SidecarQueryExpressionInterceptor>());
        if (this.Features.Spatial)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IInterceptor, SpatialFunctionsInterceptor>());

        // Sidecar DDL flows through table annotations into both EnsureCreated and migrations.
        services.Replace(ServiceDescriptor.Singleton<IRelationalAnnotationProvider, SidecarRelationalAnnotationProvider>());
        services.Replace(ServiceDescriptor.Singleton<IMigrationsAnnotationProvider, SidecarMigrationsAnnotationProvider>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, SidecarMigrationsSqlGenerator>());
    }

    public void Validate(IDbContextOptions options)
    {
    }

    sealed class ExtensionInfo(SqliteMobileOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        SqliteMobileOptionsExtension Typed => (SqliteMobileOptionsExtension)this.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
        {
            get
            {
                var parts = new List<string>();
                if (this.Typed.Features.Spatial)
                    parts.Add("ShinySpatial");
                if (this.Typed.Features.Vectors)
                    parts.Add("ShinyVectors");
                if (this.Typed.Features.FullText)
                    parts.Add("ShinyFullText");
                return string.Join(' ', parts) + ' ';
            }
        }

        public override int GetServiceProviderHashCode() => this.Typed.Features.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && o.Typed.Features == this.Typed.Features;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["Shiny:Sqlite:Spatial"] = this.Typed.Features.Spatial ? "1" : "0";
            debugInfo["Shiny:Sqlite:Vectors"] = this.Typed.Features.Vectors ? "1" : "0";
            debugInfo["Shiny:Sqlite:FullText"] = this.Typed.Features.FullText ? "1" : "0";
        }
    }
}

/// <summary>Singleton-options bridge so internal services can see the enabled features.</summary>
sealed class SqliteMobileFeaturesOptions : ISingletonOptions
{
    public SqliteMobileFeatures Features { get; private set; } = new(false, false, false);

    public void Initialize(IDbContextOptions options)
        => this.Features = options.FindExtension<SqliteMobileOptionsExtension>()?.Features ?? this.Features;

    public void Validate(IDbContextOptions options)
    {
        var features = options.FindExtension<SqliteMobileOptionsExtension>()?.Features;
        if (features != null && features != this.Features)
            throw new InvalidOperationException(
                "The Shiny SQLite features (spatial/vectors/full-text) changed for a DbContext that shares an internal service provider. Configure them consistently.");
    }
}
