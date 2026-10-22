using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

/// <summary>
/// Puts the spatial UDFs on every connection EF uses. Hooking <c>CommandCreated</c> as well as
/// <c>ConnectionOpened</c> matters: when you hand EF an already-open <see cref="SqliteConnection"/> (common with
/// in-memory databases) EF never opens it, so only the command hook sees it. Microsoft.Data.Sqlite remembers
/// registered functions and re-applies them on every open, so each connection object is registered once.
/// </summary>
sealed class SpatialFunctionsInterceptor : IDbConnectionInterceptor, IDbCommandInterceptor
{
    static readonly ConditionalWeakTable<SqliteConnection, object> registered = new();

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Ensure(connection);

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Ensure(connection);
        return Task.CompletedTask;
    }

    public DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        if (result.Connection != null)
            Ensure(result.Connection);
        return result;
    }

    static void Ensure(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite && !registered.TryGetValue(sqlite, out _))
        {
            lock (registered)
            {
                if (!registered.TryGetValue(sqlite, out _))
                {
                    SqliteSpatial.RegisterFunctions(sqlite);
                    registered.Add(sqlite, new object());
                }
            }
        }
    }
}
