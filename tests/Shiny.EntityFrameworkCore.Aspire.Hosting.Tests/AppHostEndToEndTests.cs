using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests;

/// <summary>
/// The real eventing path: a DistributedApplication with a Postgres container (and a data volume, so a second
/// AppHost start sees the same data), the gate subscribed to the database's ResourceReadyEvent, and a dependent
/// resource that <c>WaitFor</c>s the database.
/// </summary>
public class AppHostEndToEndTests
{
    sealed record RunResult(List<bool> Invocations, bool MarkerPresentWhenDependentStarted, int Products);

    static async Task<RunResult> RunAppHostAsync(string volume, CancellationToken ct)
    {
        var invocations = new List<bool>();
        var builder = DistributedApplicationTestingBuilder.Create();

        // A fixed password: Postgres keeps the one it was initialized with, and the volume outlives this AppHost run.
        var password = builder.AddParameter("pg-password", "Gate_Test_Passw0rd!", secret: true);
        var db = builder
            .AddPostgres("pg", password: password)
            .WithDataVolume(volume)
            .AddDatabase("appdb");

        db.WithDbContextSeeder<GateDbContext>(
            (o, cs) => o.UseNpgsql(cs).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
            async (ctx, gate, token) =>
            {
                invocations.Add(gate.Recreate);
                await Task.Delay(TimeSpan.FromSeconds(2), token); // long enough that a non-waiting dependent would win
                ctx.Products.Add(new Product { Sku = "e2e", Name = "seeded by the AppHost" });
            });

        builder.AddExecutable("dependent", "sleep", Environment.CurrentDirectory, "60").WaitFor(db);

        await using var app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceAsync("dependent", KnownResourceStates.Running, ct);

        var connectionString = await app.GetConnectionStringAsync("appdb", ct);
        var options = new DbContextOptionsBuilder<GateDbContext>().UseNpgsql(connectionString).Options;
        await using var check = new GateDbContext(options);
        var marker = await DbSeedHistory.FindAsync(check, "aspire:appdb:GateDbContext", ct);
        var products = await check.Products.CountAsync(ct);

        await app.StopAsync(ct);
        return new RunResult(invocations, marker != null, products);
    }

    [Fact]
    public async Task PostgresAppHost_SeedsOnFirstStart_BeforeDependentsStart_AndSkipsOnRestart()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var volume = $"shiny-efcore-gate-{Guid.NewGuid():N}";

        try
        {
            var first = await RunAppHostAsync(volume, timeout.Token);
            Assert.Equal([false], first.Invocations);
            Assert.True(first.MarkerPresentWhenDependentStarted);
            Assert.Equal(1, first.Products);

            var second = await RunAppHostAsync(volume, timeout.Token);
            Assert.Empty(second.Invocations);
            Assert.Equal(1, second.Products);
        }
        finally
        {
            await RemoveVolumeAsync(volume);
        }
    }

    static async Task RemoveVolumeAsync(string volume)
    {
        // The container is gone once the app stops, but give DCP a moment to release the volume.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var process = Process.Start(new ProcessStartInfo("docker", ["volume", "rm", "-f", volume])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                return;
            await Task.Delay(1000);
        }
    }
}
