using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.World.Addons;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>An addon's placement face decodes through the document's own face contract: a face written the way a
/// document row writes it, its portal and filter included, reaches the addon door unchanged, and a repeated key is
/// refused rather than resolved to one of its occurrences.</summary>
public sealed class AddonPlacementFaceLawTests {
    private static readonly JsonTypeInfo<WorldPlacementFace> Contract = ((JsonTypeInfo<WorldPlacementFace>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldPlacementFace)));
    private static readonly WorldPlacementFace Face = new(
        Face: "display",
        Source: new WorldScreenSource.Producer(Id: WorldImageProducerSettings.TestPatternId),
        Portal: new WorldPlacementPortal(
            Destination: "hall",
            Arrival: WorldPortalArrival.Mapped,
            Counterpart: "door-back",
            Capacity: 2
        ),
        Filter: GpuSamplerFilter.Linear
    );

    private static bool Decode(string faceJson, out WorldMutation? mutation, out string error) => WorldAddonMutationDecoder.TryDecode(
        error: out error,
        kindOrdinal: WorldMutationKindCatalog.All().Single(predicate: static entry => (entry.Type == typeof(WorldMutation.UpsertPlacement))).Ordinal,
        mutation: out mutation,
        payload: Encoding.UTF8.GetBytes(s: $$"""{"id":"door","prototypeId":"door","position":[0,0,0],"yawDegrees":0,"scale":1,"faceSources":[{{faceJson}}]}"""),
        principal: Principal.Addon(name: "guest"),
        section: WorldSection.Placements
    );

    [Fact]
    public void AnAddonPlacementsFaceWithAFilterAndAPortalRoundTrips() {
        var decoded = Decode(
            error: out var error,
            faceJson: JsonSerializer.Serialize(
                jsonTypeInfo: Contract,
                value: Face
            ),
            mutation: out var mutation
        );

        Assert.True(
            condition: decoded,
            userMessage: error
        );

        var placement = Assert.IsType<WorldMutation.UpsertPlacement>(@object: mutation).Placement;

        Assert.Equal(
            actual: Assert.Single(collection: (placement.FaceSources ?? [])),
            expected: Face
        );
    }
    [Fact]
    public void AFaceCarryingARepeatedKeyIsRefused() {
        var face = JsonSerializer.Serialize(
            jsonTypeInfo: Contract,
            value: Face
        );
        var decoded = Decode(
            error: out var error,
            faceJson: string.Concat(
                str0: "{\"face\":\"other\",",
                str1: face.AsSpan(start: 1)
            ),
            mutation: out var mutation
        );

        Assert.False(condition: decoded);
        Assert.Null(@object: mutation);
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "duplicate"
        );
    }
}
