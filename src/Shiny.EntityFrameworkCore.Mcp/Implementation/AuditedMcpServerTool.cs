using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>
/// Wraps a generated tool with the audit line an MCP server needs to be deployable: which tool, by whom, with what
/// arguments (digest only), how it ended, how long it took.
/// </summary>
/// <remarks>
/// Arguments are logged as a SHA-256 digest, never as text: a filter can carry customer data, and an audit log that
/// quietly becomes a second copy of the database is its own incident. The digest still lets you prove two calls were
/// identical, or correlate a complaint with a call.
/// </remarks>
sealed class AuditedMcpServerTool(McpServerTool inner, ILogger logger) : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var name = this.ProtocolTool.Name;
        var user = request.User?.Identity?.Name ?? "(anonymous)";
        var digest = Digest(request.Params?.Arguments);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "MCP tool {Tool} by {User} args={ArgumentDigest} outcome={Outcome} in {ElapsedMs}ms",
                name, user, digest, result.IsError == true ? "error" : "success",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            // A refused scope can land here (it fails closed by throwing). It is the line you want when someone asks
            // "did the agent see that record" — so it is logged before the exception continues.
            logger.LogWarning(ex,
                "MCP tool {Tool} by {User} args={ArgumentDigest} outcome=failed in {ElapsedMs}ms",
                name, user, digest, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    internal static string Digest(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "none";

        var sb = new StringBuilder();
        foreach (var pair in arguments.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(pair.Key).Append('=').Append(pair.Value.GetRawText()).Append(';');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }
}
