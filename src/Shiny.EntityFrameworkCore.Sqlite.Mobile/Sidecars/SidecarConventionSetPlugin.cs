using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Infrastructure;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Vectors;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

sealed class SidecarConventionSetPlugin(SqliteMobileFeaturesOptions options) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        var convention = new SidecarConvention(options.Features);

        // First in both lists: sidecar entity types must exist before EF's own finalizing conventions run (they assign
        // type mappings and table mappings), and validation must see the design-time model, before the runtime-model
        // convention swaps it out.
        conventionSet.ModelFinalizingConventions.Insert(0, convention);
        conventionSet.ModelFinalizedConventions.Insert(0, convention);

        if (options.Features.Vectors)
            conventionSet.PropertyAddedConventions.Add(new VectorColumnConvention());

        return conventionSet;
    }
}
