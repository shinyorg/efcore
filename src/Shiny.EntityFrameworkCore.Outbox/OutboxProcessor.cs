using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// Drains one context type's outbox for the life of the host. Registered by <c>AddDbContextOutbox</c>; the work
/// itself lives in <see cref="OutboxRunner"/> (resolve it keyed by the context type to drive it directly).
/// </summary>
sealed class OutboxProcessor<TContext>(IServiceProvider services) : BackgroundService where TContext : DbContext
{
    readonly OutboxRunner runner = services.GetRequiredKeyedService<OutboxRunner>(typeof(TContext));
    readonly TimeProvider clock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    // Resolved rather than injected: logging is genuinely optional here (a MAUI or test host often registers none),
    // and a required ILogger<T> constructor parameter would make the whole registration unresolvable.
    readonly ILogger logger = (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger<OutboxProcessor<TContext>>()
        ?? NullLogger.Instance;

    /// <summary>
    /// Fails the host start when the context cannot hold an outbox — a non-relational provider, or a model without
    /// <c>AddOutbox()</c> — rather than logging the same poll error every few seconds forever.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.runner.EnsureSupported(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = this.runner.Options;
        var lastSweep = this.clock.GetUtcNow();
        var stopping = false;

        while (!stoppingToken.IsCancellationRequested && !stopping)
        {
            var dispatched = 0;
            try
            {
                dispatched = await this.runner.DrainOnce(stoppingToken).ConfigureAwait(false);
                await this.runner.PendingDepth(stoppingToken).ConfigureAwait(false);

                if (options.Retention != null && this.clock.GetUtcNow() - lastSweep >= options.RetentionInterval)
                {
                    lastSweep = this.clock.GetUtcNow();
                    var purged = await this.runner.PurgeExpired(stoppingToken).ConfigureAwait(false);
                    if (purged > 0)
                        this.logger.LogDebug("Outbox retention sweep removed {Count} processed message(s)", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                stopping = true;
            }
            catch (Exception ex)
            {
                // A poll that throws — the database went away, a migration has not run yet — must not kill the loop.
                this.logger.LogError(ex, "Outbox poll for {Context} failed; retrying after {PollInterval}", typeof(TContext).Name, options.PollInterval);
            }

            // A full batch means there is probably more waiting, so skip the wait and go straight round again.
            if (!stopping && dispatched < options.BatchSize)
            {
                try
                {
                    await Task.Delay(options.PollInterval, this.clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stopping = true;
                }
            }
        }
    }
}
