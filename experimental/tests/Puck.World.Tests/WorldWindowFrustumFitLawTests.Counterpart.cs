using System.Numerics;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;
using static Puck.World.Testing.FrustumFitFixtures;

namespace Puck.World.Tests;

public sealed partial class WorldWindowFrustumFitLawTests {
    // THE LAW: a window's rays start past the glass its counterpart face draws, so the window never shows the face it
    // looks through, which shows a window of its own when the counterpart is a return portal. For every eye, the centre
    // ray of the fit a window renders through starts past the counterpart's glass by at least the clearance, measured
    // along the counterpart's normal, on whichever side the camera looks from. The red leg is the bare frustum fit, whose
    // rays start on the mapped glass, short of the counterpart's glass, so a drawn counterpart would fill the window.
    [Fact]
    public void AWindowsRaysStartPastItsCounterpartsOwnGlass() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);
        var destination = AuthoredGameFixtures.Load(relativePath: Destination);

        destination = destination with {
            AuthoringRaw = (destination.Authoring with { DerivedFaceScreens = 1 }),
            PlacementRowsRaw = [.. destination.Placements.Select(selector: static placement => ((placement.Id == "arch")
                ? (placement with {
                    FaceSources = [new WorldPlacementFace(
                        Face: "glass",
                        Source: WorldPortalFallback.SourceOf(session: new WorldScreenSource.Session(Destination: "return"))
                    )],
                })
                : placement))],
        };

        Assert.Single(collection: WorldPrototypeFacets.Seated(definition: destination));

        Assert.True(condition: WorldFaceCatalog.For(definition: destination).TryFind(
            faceName: "glass",
            placementId: "arch",
            row: out var counterpart
        ));

        var frame = WorldFaceGeometry.FromFrame(frame: counterpart.Frame);

        var (back, front) = WorldPrototypeFacets.GlassSpan(frame: counterpart.Frame);
        float Start(CameraStart camera) => Vector3.Dot(
            vector1: ((camera.Position + (camera.Forward * camera.Near)) - frame.Origin),
            vector2: frame.Normal
        );

        foreach (var eye in Eyes) {
            Assert.True(condition: WorldWindowFrustumFit.TryFitFromEye(
                camera: out var window,
                destination: destination,
                eye: eye,
                local: local,
                screen: DoorRow()
            ));
            // Looking against the normal the rays run toward the glass's back, and along it toward its front.
            var against = (Vector3.Dot(vector1: window.Forward, vector2: frame.Normal) < 0f);
            var start = Start(camera: new CameraStart(window.Position, window.Forward, window.Near));

            Assert.True(
                condition: (against
                    ? (start <= ((back - WorldWindowFrustumFit.GlassClearance) + 1e-4f))
                    : (start >= ((front + WorldWindowFrustumFit.GlassClearance) - 1e-4f))),
                userMessage: $"from eye {eye} the window's rays start at {start} along the counterpart's normal, inside its glass's span from {back} to {front}"
            );

            var bare = Fit(eye: eye);
            var bareStart = Start(camera: new CameraStart(bare.Position, bare.Forward, bare.Near));

            Assert.True(condition: (against
                ? (bareStart > back)
                : (bareStart < front)));
        }
    }
    // THE LAW: a counterpart with no seated screen draws no glass to skip. Advancing past a hypothetical slab would
    // discard real geometry immediately beyond the mapped aperture, even though the arch's opening is empty.
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(37f, 2f)]
    [InlineData(180f, 0.5f)]
    public void AnUnseatedCounterpartDoesNotAdvanceTheNearPlane(float yaw, float scale) {
        var local = AuthoredGameFixtures.Load(relativePath: Local);
        var destination = AuthoredGameFixtures.Load(relativePath: Destination);

        destination = destination with {
            PlacementRowsRaw = [.. destination.Placements.Select(selector: placement => ((placement.Id == "arch")
                ? (placement with { Scale = scale, YawDegrees = yaw })
                : placement))],
        };

        Assert.Empty(collection: WorldPrototypeFacets.Seated(definition: destination));
        Assert.True(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out var counterpart,
            destination: destination,
            local: local,
            screenIndex: DoorScreen(local: local),
            source: out var source
        ));

        foreach (var eye in Eyes) {
            Assert.True(condition: WorldWindowFrustumFit.TryFitWindow(
                camera: out var bare,
                destination: counterpart,
                glass: WorldWindowFrustumFit.Glass(screen: DoorRow()),
                localEye: eye,
                source: source
            ));
            Assert.True(condition: WorldWindowFrustumFit.TryFitFromEye(
                camera: out var window,
                destination: destination,
                eye: eye,
                local: local,
                screen: DoorRow()
            ));
            Assert.Equal(actual: window, expected: bare);
        }
    }

    private readonly record struct CameraStart(Vector3 Position, Vector3 Forward, float Near);
}
