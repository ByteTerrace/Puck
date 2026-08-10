using Xunit;

namespace Puck.World.Tests;

/// <summary>Locks the traveler-follow screen band into the same document policy the renderer consumes.</summary>
public sealed class AwaySeatScreenReservationLawTests {
    [Fact]
    public void AuthoredScreenInsideAwaySeatBand_IsRefusedBeforeComposition() {
        var source = Fixtures.BuildDocument();
        var collided = source with {
            Screens = [source.Screens[0] with { Index = WorldPlacementPolicy.AwaySeatScreenBase }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: collided, reason: out var reason, neighbours: null));
        Assert.Contains(expectedSubstring: "reserved traveler-follow range", actualString: reason);
    }

    [Fact]
    public void DerivedFaceReservationCannotEnterAwaySeatBand() {
        var source = Fixtures.BuildDocument();
        var collided = source with {
            Authoring = source.Authoring with { DerivedFaceScreens = (WorldPlacementPolicy.MaxDerivedFaceScreens + 1) },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: collided, reason: out var reason, neighbours: null));
        Assert.Contains(expectedSubstring: $"authoring.derivedFaceScreens {WorldPlacementPolicy.MaxDerivedFaceScreens + 1} is outside 0..{WorldPlacementPolicy.MaxDerivedFaceScreens}", actualString: reason);
    }

    [Fact]
    public void HeadroomMustFitBesideAuthoredDerivedAndAwaySeatScreens() {
        var source = Fixtures.BuildDocument();
        var overflow = source with {
            Authoring = source.Authoring with { AuthoringHeadroomScreens = 24 },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: overflow, reason: out var reason, neighbours: null));
        Assert.Contains(expectedSubstring: "only 23 remain", actualString: reason);

        var boundary = source with {
            Authoring = source.Authoring with { DerivedFaceScreens = WorldPlacementPolicy.MaxDerivedFaceScreens },
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: boundary, reason: out var boundaryReason, neighbours: null), userMessage: boundaryReason);
    }
}
