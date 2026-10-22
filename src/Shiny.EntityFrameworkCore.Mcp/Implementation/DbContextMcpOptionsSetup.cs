using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>
/// Adds the tools, resources and prompts to the SDK's options. It runs when the options are built — the first moment a
/// container exists to open a <c>DbContext</c> (and read its model) from.
/// </summary>
sealed class DbContextMcpOptionsSetup(DbContextMcpPrimitives primitives) : IConfigureOptions<McpServerOptions>
{
    public void Configure(McpServerOptions options)
    {
        options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in primitives.Tools)
            options.ToolCollection.Add(tool);

        if (primitives.Resources.Count > 0)
        {
            options.ResourceCollection ??= new McpServerResourceCollection();
            foreach (var resource in primitives.Resources)
                options.ResourceCollection.Add(resource);

            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Resources ??= new ResourcesCapability();
        }

        if (primitives.Prompts.Count > 0)
        {
            options.PromptCollection ??= new McpServerPrimitiveCollection<McpServerPrompt>();
            foreach (var prompt in primitives.Prompts)
                options.PromptCollection.Add(prompt);

            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Prompts ??= new PromptsCapability();
        }
    }
}
