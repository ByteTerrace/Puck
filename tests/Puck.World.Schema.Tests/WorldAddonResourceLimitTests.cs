using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldAddonResourceLimitTests {
    private sealed class CountOnly<T>(int count) : IReadOnlyList<T> {
        public int Count => count;

        public T this[int index] => throw new InvalidOperationException(message: "Oversized collection elements must not be inspected.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException(message: "Oversized collection must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static WorldAddonRow Addon(int index) => new($"guest{index}", "guest.wasm",
        "sha256/0000000000000000000000000000000000000000000000000000000000000000", 0, true);
    private static string Validate(params WorldAddonRow[] addons) {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240), AddonsRaw: addons);

        return (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    }

    [Fact]
    public void DocumentAddonCountAndFuelAreBoundedIncludingDisabledRowsAndDefaultBudgets() {
        var maximum = Enumerable.Range(count: WorldAddonLimits.Addons, start: 0).Select(selector: Addon).ToArray();

        Assert.Equal(string.Empty, Validate(maximum));
        Assert.Contains("addon count", Validate([.. maximum, Addon(index: maximum.Length) with { Enabled = false }]));
        Assert.Contains("fuel", Validate(Addon(index: 0) with { Fuel = (WorldAddonLimits.FuelPerAddon + 1) }));
        var fuel = Enumerable.Range(count: ((int)(WorldAddonLimits.FuelPerDocument / WorldAddonLimits.FuelPerAddon)), start: 0)
            .Select(selector: index => Addon(index: index) with { Fuel = WorldAddonLimits.FuelPerAddon }).ToArray();

        Assert.Equal(string.Empty, Validate(fuel));
        Assert.Contains("document addon fuel", Validate([.. fuel, Addon(index: fuel.Length)]));
        Assert.Equal(string.Empty, Validate(Addon(index: 0)));
    }
    [Fact]
    public void RequestsAndMemoryWatchesAreBoundedAcrossTheWholeDocument() {
        var requests = Enumerable.Repeat(new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: GrantSubject.All),
            (WorldAddonLimits.RequestsPerDocument / 2)).ToArray();
        var watches = Enumerable.Repeat(new WorldAddonMemoryWatch(Address: 0, Length: 1, Screen: 0),
            (WorldAddonLimits.MemoryWatchesPerDocument / 2)).ToArray();
        var first = Addon(index: 0) with { Requests = requests, MemoryWatches = watches };
        var second = Addon(index: 1) with { Requests = requests, MemoryWatches = watches };

        Assert.Equal(string.Empty, Validate(first, second));
        Assert.Contains("document addon requests", Validate(first, second with { Requests = [.. requests, requests[0]] }));
        Assert.Contains("document addon memory watches", Validate(first, second with { MemoryWatches = [.. watches, watches[0]] }));
        Assert.Equal(string.Empty, Validate(first));
    }
    [Fact]
    public void OversizedCollectionsRefuseBeforeElementValidation() {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240),
            AddonsRaw: new CountOnly<WorldAddonRow>(count: (WorldAddonLimits.Addons + 1)));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "addon count");
        Assert.Contains("document addon requests", Validate(Addon(index: 0) with {
            Requests = new CountOnly<WorldCapabilityRequest>(count: (WorldAddonLimits.RequestsPerDocument + 1)),
        }));
        Assert.Contains("document addon memory watches", Validate(Addon(index: 0) with {
            MemoryWatches = new CountOnly<WorldAddonMemoryWatch>(count: (WorldAddonLimits.MemoryWatchesPerDocument + 1)),
        }));
    }
}
