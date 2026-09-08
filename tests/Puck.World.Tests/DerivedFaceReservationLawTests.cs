using System.Globalization;

using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldServer.BootDerivedFaceScreens"/> — the derived-face screen band is reserved
/// once, at boot, and the render provider key set is frozen there. A live
/// <see cref="WorldMutation.SetAuthoringDefaults"/> may lower the reservation but never raise it, because
/// <see cref="WorldFaceCatalog"/> seats faces against the LIVE document while the renderer only holds the boot band:
/// a raise would seat rows at indices nothing can show, and <c>world.faces</c> would report them seated.
/// </summary>
public sealed class DerivedFaceReservationLawTests {
    // The fixture's default reservation IS WorldPlacementPolicy.MaxDerivedFaceScreens, so a server booted from it
    // has no room to raise INTO: every raise would be refused by the pre-existing static range check instead, and
    // this law would pass whether or not the boot-frozen gate existed at all. Booting deliberately below the ceiling
    // is what leaves a raise that only the boot-frozen gate can refuse.
    private const int BootReservation = 2;

    private static WorldFixture FreshServer() =>
        Fixtures.FreshServer(definition: (Fixtures.BuildDocument() with {
            AuthoringRaw = (Fixtures.StandardAuthoring with { DerivedFaceScreens = BootReservation }),
        }));

    [Fact]
    public void TheFixtureLeavesHeadroomAboveItsBootReservation() {
        // The instrument check: the raise below must be one the STATIC range admits, or the law proves nothing about
        // the boot-frozen gate.
        using var fixture = FreshServer();

        Assert.Equal(expected: BootReservation, actual: fixture.Server.BootDerivedFaceScreens);
        Assert.True(condition: ((BootReservation + 1) <= WorldPlacementPolicy.MaxDerivedFaceScreens), userMessage: "the raise under test must sit inside the static derivedFaceScreens range");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: (Fixtures.BuildDocument() with { AuthoringRaw = (Fixtures.StandardAuthoring with { DerivedFaceScreens = (BootReservation + 1) }) }),
            reason: out var reason,
            neighbours: null), userMessage: reason);
    }
    [Fact]
    public void RaisingTheReservationPastBoot_RefusesByName_LoweringItApplies() {
        using var fixture = FreshServer();

        Laws.RefusalWithControl(
            lawId: "derived-face-reservation.raise-past-boot",
            deniedOutcome: () => ApplyAndObserveChange(derivedFaceScreens: (BootReservation + 1), fixture: fixture),
            controlOutcome: () => ApplyAndObserveChange(derivedFaceScreens: (BootReservation - 1), fixture: fixture));
    }
    [Fact]
    public void TheRefusalNamesTheBandAndTheAskedForWidth() {
        using var fixture = FreshServer();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                refusals.Add(item: echo.Message);
            }
        };

        _ = ApplyAndObserveChange(derivedFaceScreens: (BootReservation + 1), fixture: fixture);

        Assert.Contains(collection: refusals, filter: reason =>
            (reason.Contains(comparisonType: StringComparison.Ordinal, value: "derivedFaceScreens") &&
            reason.Contains(value: (BootReservation + 1).ToString(provider: CultureInfo.InvariantCulture), comparisonType: StringComparison.Ordinal) &&
            reason.Contains(value: BootReservation.ToString(provider: CultureInfo.InvariantCulture), comparisonType: StringComparison.Ordinal)));
    }
    [Fact]
    public void SettingTheReservationToExactlyTheBootBandIsNotRefused() {
        // The boundary the refusal must NOT swallow: equal is not greater. Observed through the refusal echo rather
        // than through a document diff — re-stating the value the document already carries changes no bytes, so a
        // diff probe could not tell "applied" from "refused" here.
        using var fixture = FreshServer();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                refusals.Add(item: echo.Message);
            }
        };

        _ = ApplyAndObserveChange(derivedFaceScreens: BootReservation, fixture: fixture);

        Assert.Empty(collection: refusals);
    }

    // Submits the authoring row and reports whether the live document actually moved — the same
    // observe-the-document-not-the-reply shape MutationAllOrNothingLawTests uses.
    private static bool ApplyAndObserveChange(WorldFixture fixture, int derivedFaceScreens) {
        var before = fixture.DefinitionBytes();
        var authoring = (fixture.Server.Definition.Authoring with { DerivedFaceScreens = derivedFaceScreens });

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetAuthoringDefaults(Principal: WorldPrincipal.Console, Authoring: authoring));
        fixture.Step();

        return !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes());
    }
}
