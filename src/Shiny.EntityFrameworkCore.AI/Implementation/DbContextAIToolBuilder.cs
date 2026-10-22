using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

sealed class DbContextAIToolBuilder : IDbContextAIToolBuilder
{
    readonly Dictionary<string, EntityAIRegistration> registrations = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, EntityAIRegistration> Registrations => this.registrations;

    public IDbContextAIToolBuilder AddEntity<T>(Action<IEntityAIToolBuilder<T>>? configure = null, string? name = null) where T : class
    {
        var slug = string.IsNullOrWhiteSpace(name) ? Slugify(typeof(T).Name) : name!;
        ValidateSlug(slug);

        var entityBuilder = new EntityAIToolBuilder<T>();
        configure?.Invoke(entityBuilder);

        if (!this.registrations.TryAdd(slug, entityBuilder.Build(slug)))
            throw new InvalidOperationException(
                $"An entity with slug '{slug}' is already registered. Slugs must be unique — pass a different name.");

        return this;
    }

    /// <summary>
    /// Runs the builder callback and refuses an empty registration. Shared by the DI path and the factory path so the
    /// check stays eager (at registration time, not first resolve).
    /// </summary>
    public static DbContextAIToolBuilder BuildOrThrow(Action<IDbContextAIToolBuilder> configure)
    {
        var builder = new DbContextAIToolBuilder();
        configure(builder);
        builder.ThrowIfEmpty("AddDbContextAITools / CreateAITools");
        return builder;
    }

    public void ThrowIfEmpty(string caller)
    {
        if (this.registrations.Count == 0)
            throw new InvalidOperationException(
                $"{caller} requires at least one AddEntity<T>() call. An empty registration would expose no tools.");
    }

    /// <summary>
    /// Asserts every service a <c>Where&lt;TService&gt;</c> scope will resolve is registered. A missing one is a startup
    /// error rather than a failed tool call in production — the scope fails closed either way, but failing closed at
    /// 3am is not the same as failing at boot.
    /// </summary>
    public void ValidateRequiredServices(IServiceCollection services, string caller)
    {
        foreach (var registration in this.registrations.Values)
        {
            foreach (var serviceType in registration.RequiredServiceTypes)
            {
                if (!services.Any(d => d.ServiceType == serviceType))
                    throw new InvalidOperationException(
                        $"'{registration.Slug}' registers a request-resolved scope needing '{serviceType}', but no such " +
                        $"service is registered. Register it before {caller} — a scope that cannot resolve its service " +
                        "refuses every tool call.");
            }
        }
    }

    // Tool names are what an MCP client prefixes and an LLM types; keep them to the portable character set.
    static void ValidateSlug(string slug)
    {
        var valid = slug.Length <= 48 && slug.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');
        if (!valid)
            throw new ArgumentException(
                $"Entity name '{slug}' is not a valid tool prefix. Use at most 48 letters, digits, '_' or '-'.", "name");
    }

    // OrderLine -> order_line, Customer -> customer.
    static string Slugify(string typeName)
    {
        var tick = typeName.IndexOf('`');
        if (tick > 0)
            typeName = typeName[..tick];

        var sb = new StringBuilder(typeName.Length + 4);
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(typeName[i - 1]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
