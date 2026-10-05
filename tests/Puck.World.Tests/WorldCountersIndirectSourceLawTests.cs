using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.SdfVm;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the residency's actual host schedule source reports recorded work through
/// <c>world.counters</c>, without advertising unobserved GPU proof events as zero-valued host counts.
/// </summary>
public sealed class WorldCountersIndirectSourceLawTests {
    [Fact]
    public void TheIndirectSourceReportsScheduledWorkWithoutInventingProofEvents() {
        var work = new WorkCounterSet(name: SdfIndirectWork.SourceName, kinds: SdfIndirectWork.Kinds);
        work.Add(kind: SdfIndirectWork.Rays, amount: 128);
        work.Add(kind: SdfIndirectWork.Probes, amount: 2);

        var services = new ServiceCollection();
        _ = services.AddWorldCounters();
        _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: work);

        using var provider = services.BuildServiceProvider();
        var result = new CommandRegistry(modules: provider.GetServices<ICommandModule>()).Submit(line: "world.counters sdf.indirect --json");

        Assert.False(condition: result.IsError);
        Assert.StartsWith(expectedStartString: "[world.counters: ", actualString: result.Output);
        Assert.EndsWith(expectedEndString: "]", actualString: result.Output);

        using var document = JsonDocument.Parse(json: result.Output["[world.counters: ".Length..^1]);
        var source = Assert.Single(collection: document.RootElement.GetProperty(propertyName: "sources").EnumerateArray());
        Assert.Equal(expected: "sdf.indirect", actual: source.GetProperty(propertyName: "name").GetString());

        var counts = source.GetProperty(propertyName: "counts");
        Assert.Equal(expected: 128L, actual: counts.GetProperty(propertyName: "indirect.rays.scheduled").GetInt64());
        Assert.Equal(expected: 2L, actual: counts.GetProperty(propertyName: "indirect.probes.scheduled").GetInt64());
        Assert.Equal(expected: 0L, actual: counts.GetProperty(propertyName: "indirect.light.regions").GetInt64());

        var legend = document.RootElement.GetProperty(propertyName: "kinds");
        var rays = legend.GetProperty(propertyName: "indirect.rays.scheduled");
        Assert.Equal(expected: "rays", actual: rays.GetProperty(propertyName: "unit").GetString());
        Assert.Equal(expected: "deterministic", actual: rays.GetProperty(propertyName: "class").GetString());

        foreach (var retired in new[] { "indirect.proofs.issued", "indirect.proofs.reused", "indirect.proofs.deferred" }) {
            Assert.False(condition: counts.TryGetProperty(propertyName: retired, value: out _), userMessage: $"Host counts advertise the unobserved event '{retired}'.");
            Assert.False(condition: legend.TryGetProperty(propertyName: retired, value: out _), userMessage: $"The legend advertises the unobserved event '{retired}'.");
        }
    }
}
