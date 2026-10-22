using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>
/// Initializes and seeds every registered context type at host startup, one scope per context, in registration
/// order. Startup blocks until it finishes, so the app never serves requests against an unseeded database, and a
/// seeder that throws fails the host start.
/// </summary>
sealed class DbSeedHostedService(IServiceProvider services, IEnumerable<IDbSeedContextRunner> runners) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var runner in runners)
        {
            await using var scope = services.CreateAsyncScope();
            await runner.RunAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
