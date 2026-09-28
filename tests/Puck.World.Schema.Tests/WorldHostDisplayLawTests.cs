using System.Text;
using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws for the host section's display fields, <c>colorSpace</c> and <c>paperWhiteNits</c>: a host that
/// authors neither writes neither and asks for SDR at the SDR white level; an authored HDR color space and paper white
/// round-trip by name; and a paper white outside the level the perceptual quantizer encodes is refused by name.</summary>
public sealed class WorldHostDisplayLawTests {
    private static WorldDefinition Hosted(WorldHostDefaults host) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        HostRaw: host
    );

    private static WorldHostDefaults Windowed => (WorldHostDefaults.Absent with {
        Height = 200,
        Width = 320,
    });

    private static bool TryLoad(WorldDefinition definition, out WorldDefinition? loaded, out string reason) {
        var admitted = WorldDefinitionLoader.TryLoadForAdmission(
            admission: out var admission,
            instanceIdentity: "display",
            reason: out reason,
            sourceName: "host-display",
            utf8: WorldDefinitionSerialization.Serialize(definition: definition)
        );

        loaded = admission?.Definition;

        return admitted;
    }

    [Fact]
    public void AHostThatAuthorsNoDisplayFieldsWritesNoneAndAsksForSdr() {
        var json = Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: Hosted(host: Windowed)));

        Assert.DoesNotContain(
            actualString: json,
            expectedSubstring: "colorSpace"
        );
        Assert.DoesNotContain(
            actualString: json,
            expectedSubstring: "paperWhiteNits"
        );
        Assert.True(
            condition: TryLoad(
                definition: Hosted(host: Windowed),
                loaded: out var loaded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: (loaded!.Host.ColorSpace, loaded.Host.PaperWhiteNits),
            expected: (DisplayColorSpace.Srgb, ((double?)null))
        );
    }
    [Fact]
    public void AnAuthoredHdrColorSpaceAndPaperWhiteRoundTripByName() {
        var host = (Windowed with {
            ColorSpace = DisplayColorSpace.Hdr10,
            PaperWhiteNits = 203.0,
        });
        var json = Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: Hosted(host: host)));

        Assert.Contains(
            actualString: json,
            expectedSubstring: "\"colorSpace\": \"Hdr10\""
        );
        Assert.True(
            condition: TryLoad(
                definition: Hosted(host: host),
                loaded: out var loaded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: (loaded!.Host.ColorSpace, loaded.Host.PaperWhiteNits),
            expected: (DisplayColorSpace.Hdr10, ((double?)203.0))
        );
    }
    [InlineData(79.0)]
    [InlineData(10_000.5)]
    [Theory]
    public void APaperWhiteOutsideTheEncodedLevelsIsRefusedByName(double nits) {
        Assert.False(condition: TryLoad(
            definition: Hosted(host: (Windowed with { PaperWhiteNits = nits })),
            loaded: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "host.paperWhiteNits"
        );
    }
}
