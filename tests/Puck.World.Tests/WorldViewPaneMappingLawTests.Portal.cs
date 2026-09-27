using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// A pick through a portal (rendering plan P13's portal check): the portal-window canary's door, whose glass shows a
// window session onto its destination, is a screen standing in the world, the world's view is a pane, and a display
// point on the marker's image walks through the pane into the world, onto the glass, and through the camera the session
// last rendered with into the destination, where no screen stands under the depth-one policy, so the walk ends in the
// destination's world, on the marker's surface.
public sealed partial class WorldViewPaneMappingLawTests {
    // The door's window, seen by a seat whose eye is the window's eye, the view on the display's left half, and the
    // session's emitter dressed through the window it fits.
    private (WorldSessionSceneEmitter Session, string Name, Vector2 Marker) ShowPortal(bool dress) {
        var row = WorldWindowFrustumFitLawTests.DoorRow();
        var name = WorldViewNames.Session(screen: row.Index);
        var screens = new WorldScreenMappingSet();
        var eye = WorldWindowFrustumFitLawTests.Eyes[0];
        var seat = CameraSnapshot.LookAt(
            fieldOfViewRadians: 1f,
            position: eye,
            target: (eye - Vector3.UnitZ),
            viewportHeight: Display,
            viewportWidth: (Display / 2)
        );

        screens.Reconcile(
            cameras: [],
            screens: [row]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [new WorldView(
            Demand: WorldViewDemand.Screen,
            FilmsWorld: false,
            Height: 0.25,
            Name: name,
            Refresh: RenderGraphRefresh.EveryFrame,
            Width: 0.25
        )]));
        screens.Publish(images: new PatternImages(Height: 1, Width: 1));
        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: seat, Region: Left));

        var session = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: AuthoredGameFixtures.Load(relativePath: WorldWindowFrustumFitLawTests.Destination))
        );

        session.SetWindowCamera(camera: WorldWindowFrustumFitLawTests.Fit(eye: eye));

        if (dress) {
            _ = new SdfCompositionFrameSource(
                dresser: session,
                emitters: [session]
            ).CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );
        }

        m_host.ViewScenes = new SessionScenes(Name: name, Session: session);

        // The display point the seat shows the marker at: its image point on the left half of the display.
        var direction = (WorldWindowFrustumFitLawTests.Marker - seat.Position);
        var forward = Vector3.Dot(vector1: direction, vector2: seat.Forward);
        var across = ((((Vector3.Dot(vector1: direction, vector2: seat.Right) / forward) / (seat.AspectRatio * seat.TanHalfFieldOfView)) + 1f) * 0.5f);
        var down = ((1f - ((Vector3.Dot(vector1: direction, vector2: seat.Up) / forward) / seat.TanHalfFieldOfView)) * 0.5f);

        return (session, name, new Vector2(x: (across * (Display / 2)), y: (down * Display)));
    }

    [Fact]
    public void APickThroughAPortalReachesTheDestinationsSurfaceThroughTheCameraItsWindowRendered() {
        var (_, name, marker) = ShowPortal(dress: true);

        Frame(pane: null);
        Frame(pane: null);

        var set = m_instances.Instances;
        var walk = m_host.Walk(point: Point(x: marker.X, y: marker.Y))!;

        Assert.Equal(expected: RenderGraphHitEnd.World, actual: walk.End);
        Assert.Equal(expected: set.IndexOf(name: name), actual: walk.Instance);
        Assert.Equal(expected: 2, actual: walk.Steps.Count);
        Assert.Equal(expected: set.IndexOf(name: WorldViewGraphs.WorldInstance), actual: walk.Steps[1].Instance);
        Assert.Equal(expected: SourceHandle.Instance(name: name), actual: walk.Steps[1].Mapping.Source);
        Assert.True(condition: walk.Steps[1].Hit.IsOnSource);
        Assert.Empty(collection: ((IRenderGraphHitScene)m_host).Placements(instance: walk.Instance));

        // The marker's near surface: half a unit from its centre, toward the window's eye.
        Assert.True(condition: walk.Surface.HasValue);

        var surface = walk.Surface.GetValueOrDefault().ToVector3();

        Assert.Equal(
            expected: 0.5f,
            actual: Vector3.Distance(value1: surface, value2: WorldWindowFrustumFitLawTests.Marker),
            tolerance: 0.02f
        );
        Assert.True(condition: (surface.Z > WorldWindowFrustumFitLawTests.Marker.Z));
    }
    // A session that has rendered nothing has no camera to continue through, so the pick ends on the glass.
    [Fact]
    public void APickThroughAPortalWhoseWindowHasNotRenderedEndsOnTheGlass() {
        var (_, name, marker) = ShowPortal(dress: false);

        Frame(pane: null);
        Frame(pane: null);

        var walk = m_host.Walk(point: Point(x: marker.X, y: marker.Y))!;

        Assert.Equal(expected: RenderGraphHitEnd.NoCamera, actual: walk.End);
        Assert.Equal(expected: SourceHandle.Instance(name: name), actual: walk.Steps[^1].Mapping.Source);
        Assert.Null(@object: walk.Surface);
    }

    // Answers for the session as WorldScreenBinder does: its emitter's camera and surface.
    private sealed record SessionScenes(string Name, WorldSessionSceneEmitter Session) : IWorldViewScenes {
        public bool TryCamera(string view, out CameraSnapshot camera) {
            if (string.Equals(
                a: view,
                b: Name,
                comparisonType: StringComparison.Ordinal
            )) {
                return Session.TryCamera(camera: out camera);
            }

            camera = default;

            return false;
        }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
            if (string.Equals(
                a: view,
                b: Name,
                comparisonType: StringComparison.Ordinal
            )) {
                return Session.TrySurface(
                    point: out point,
                    ray: ray
                );
            }

            point = default;

            return false;
        }
    }
}
