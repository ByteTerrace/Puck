using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Testing;

/// <summary>The portal-window canary's door: its apertures, its screen and the frustum a camera fits through it.</summary>
internal static class FrustumFitFixtures {
    // The screen row the door's glass face derives, as the presenter hands it to the binder.
    internal static WorldScreen DoorRow() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);
        var screen = DoorScreen(local: local);

        return Assert.Single(
            collection: WorldPrototypeFacets.Derive(
                definition: local,
                derivedFaceBase: WorldPrototypeFacets.DerivedFaceBase,
                derivedFaceScreens: local.Authoring.DerivedFaceScreens
            ).Faces,
            predicate: row => (row.Index == screen)
        );
    }
    internal static CameraSnapshot Fit(Vector3 eye) {
        var (source, destination) = Apertures();

        Assert.True(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out var camera,
            destination: destination,
            glass: WorldWindowFrustumFit.Glass(screen: DoorRow()),
            localEye: eye,
            source: source
        ));

        return camera;
    }

    internal const string Local = "tests/Puck.World.Canaries/portal-window/fixture.puck";

    // The screen index the door's glass face is seated at.
    internal static int DoorScreen(WorldDefinition local) => Assert.Single(collection: WorldFaceCatalog.For(definition: local).Rows).ScreenIndex;
    internal static (WorldFaceGeometry Source, WorldFaceGeometry Destination) Apertures() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);

        Assert.True(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out var destination,
            destination: AuthoredGameFixtures.Load(relativePath: Destination),
            local: local,
            screenIndex: DoorScreen(local: local),
            source: out var source
        ));

        return (source, destination);
    }

    internal const string Destination = "tests/Puck.World.Canaries/portal-window/beyond.puck";
}
