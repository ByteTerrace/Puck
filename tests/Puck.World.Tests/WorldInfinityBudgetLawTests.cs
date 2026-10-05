using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldInfinityBudgetLawTests {
    [Fact]
    public void TheBudgetFollowsLiveRootPlansAndWithdrawsOnlyTheRetiredRoot() {
        using var row = HostRow.Build(name: "infinity-budget");
        var probe = new WorldRenderProbe();
        var roster = new PlayerRoster(definition: row.Server.Definition, link: row.Instance.Link,
            seatBindings: new WorldSeatBindings(definition: row.Server.Definition));
        var registry = new CommandRegistry(modules: [new WorldPopulationCommandModule(roster: roster,
            population: row.Server.Population, server: row.Server, link: row.Instance.Link, renderProbe: probe)]);
        var first = new object();
        var second = new object();
        var spec = new InfinityViewSpec(Anchor: Vector3.Zero, Fallback: Vector3.Zero, FarDistance: 100f,
            Kind: InfinityViewKind.World, Levers: InfinityViewLevers.None, Mask: null,
            MinimumTier: QualityTier.Low, Name: "lobby", Orientation: Quaternion.Identity, Refresh: 1, Scale: 1f);
        var nested = WorldInfinityViewPlan.Resolve(roots: [spec], childrenOf: (_, _) => [spec with { Name = "too-deep" }], nestingDepth: 1);

        Assert.Single(collection: nested.Views);
        var fallback = Assert.Single(collection: nested.Fallbacks);

        probe.RegisterInfinityPlan(name: "world", owner: first, plan: nested);
        probe.RegisterInfinityPlan(second, "session", WorldInfinityViewPlan.Resolve([spec], (_, _) => [], nestingDepth: 1));
        string Budget() {
            var answer = registry.Submit(line: "world.budget");

            Assert.False(condition: answer.IsError, userMessage: answer.Output);
            return answer.Output;
        }
        var before = Budget();

        Assert.Contains(actualString: before, expectedSubstring: $"world: infinity views: 1 of {WorldInfinityViewPlan.MaxViews}");
        Assert.Contains(actualString: before, expectedSubstring: $"session: infinity views: 1 of {WorldInfinityViewPlan.MaxViews}");
        Assert.Contains(fallback.Reason, before);
        // Another camera of the same root updates the entry rather than charging another scene plan.
        probe.RegisterInfinityPlan(first, "world", WorldInfinityViewPlan.Empty);
        var updated = Budget();

        Assert.Contains(actualString: updated, expectedSubstring: $"world: infinity views: 0 of {WorldInfinityViewPlan.MaxViews}");
        Assert.DoesNotContain(fallback.Reason, updated);
        probe.UnregisterInfinityPlan(owner: first);
        var retired = Budget();

        Assert.DoesNotContain(actualString: retired, expectedSubstring: "world: infinity views:");
        Assert.Contains(actualString: retired, expectedSubstring: $"session: infinity views: 1 of {WorldInfinityViewPlan.MaxViews}");
        probe.UnregisterInfinityPlan(owner: second);
        Assert.Contains("infinity views: none", Budget());
        // Authoritative costs remain readable when no presentation probe is composed.
        var headless = new CommandRegistry(modules: [new WorldPopulationCommandModule(roster: roster,
            population: row.Server.Population, server: row.Server, link: row.Instance.Link)]).Submit(line: "world.budget");

        Assert.False(condition: headless.IsError, userMessage: headless.Output);
        Assert.Contains("infinity unavailable: no renderer", headless.Output);
        Assert.Contains("state ", headless.Output);
    }
}
