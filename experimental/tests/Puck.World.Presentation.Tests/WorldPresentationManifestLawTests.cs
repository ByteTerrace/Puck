using System.Text.Json;

using Puck.Abstractions.Counting;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// Laws for the presentation manifest (<see cref="WorldPresentationManifest"/>): it is exactly the deduplicated union of
/// the state bindings a document's presentation sections author, with per-body templates kept apart and their
/// <c>$body</c> key as authored; a mirror installing a document registers the manifest's slots, and installing it again
/// — or installing a reloaded copy of it — registers nothing new, keeps every slot index, and allocates nothing; a
/// manifest slot on a cell advancing at the install presents its installed value whole before the next tick; each
/// template is recorded under the look, creation or population section that carries it, and a body's lease acquires
/// exactly those when the body arrives, so its first frame's reads read no cell and allocate no slot, and arriving
/// again allocates nothing; and the walk over the shipped flagship world completes with a plausible count.
/// </summary>
public sealed class WorldPresentationManifestLawTests {
    private static WorldPresentationBinding Number(string row, string? key = null, bool target = false) => new(
        Binding: new StateBinding(
            Key: key,
            Row: row,
            Target: target
        ),
        Conversion: WorldStateConversion.Number
    );
    [Fact]
    public void TheFlagshipWorldsManifestCompilesWithAPlausibleCount() {
        var definition = AuthoredGameFixtures.Nexus;
        var manifest = WorldPresentationManifest.Of(definition: definition);

        Assert.Same(
            actual: WorldPresentationManifest.Of(definition: definition),
            expected: manifest
        );
        Assert.InRange(
            actual: manifest.Bindings.Length,
            high: 256,
            low: 1
        );
        Assert.InRange(
            actual: manifest.BodyBindings.Length,
            high: 256,
            low: 1
        );
        Assert.Contains(
            collection: manifest.Bindings.ToArray(),
            expected: Number(row: "captureStation")
        );
        Assert.Contains(
            collection: manifest.BodyBindings.ToArray(),
            expected: Number(row: "swayClock")
        );
        Assert.Contains(
            collection: manifest.BodyBindings.ToArray(),
            expected: Number(row: "scale", key: StateBinding.BodyKey)
        );
    }
}
