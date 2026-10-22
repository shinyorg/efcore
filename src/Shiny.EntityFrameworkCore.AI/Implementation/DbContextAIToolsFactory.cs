using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

static class DbContextAIToolsFactory
{
    /// <summary>
    /// Opens one context to read the EF model, binds every registration against it (throwing on anything the model
    /// contradicts), and generates the tools. The context is disposed before any tool runs.
    /// </summary>
    public static DbContextAITools<TContext> Build<TContext>(IDbContextSource source, DbContextAIToolBuilder builder) where TContext : DbContext
    {
        IModel model;
        string? providerName;
        using (var lease = source.Open())
        {
            model = ReadModel(lease.Context);
            providerName = lease.Context.Database.ProviderName;
        }

        var functions = new List<AIFunction>();
        var descriptors = new List<EntityAIDescriptor>();
        foreach (var registration in builder.Registrations.Values)
        {
            var binding = registration.Bind(model);
            var tools = binding.CreateTools(source);
            functions.AddRange(tools);
            descriptors.Add(binding.Describe(tools.Select(t => t.Name).ToArray()));
        }

        return new DbContextAITools<TContext>(functions, descriptors, providerName);
    }

    // The design-time model still carries the column/table comments (HasComment) used as descriptions; the runtime
    // model strips them. A compiled model has no design-time model, so fall back to the runtime one.
    static IModel ReadModel(DbContext context)
    {
        try
        {
            return context.GetService<IDesignTimeModel>().Model;
        }
        catch (InvalidOperationException)
        {
            return context.Model;
        }
    }
}
