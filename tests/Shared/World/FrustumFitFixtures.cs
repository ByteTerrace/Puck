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

    // The destination's marker: a ball of radius 0.5 six units behind the arch.
    internal static readonly Vector3 Marker = new(x: 0f, y: 1.5f, z: -6f);
    // The first two are the canary's eyes: the camera its seat's view renders with at each of its two body poses.
    internal static readonly Vector3[] Eyes = [
        new(x: 1f, y: 1.6f, z: 6f),
        new(x: -1f, y: 1.6f, z: 6f),
        new(x: 0.4f, y: 2.1f, z: 3f),
    ];
}
