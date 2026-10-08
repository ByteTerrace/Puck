using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PortalInputLawTests {
    // How far apart two rays may meet the destination's screen and still count as one point: the window's image is fitted
    // in floats, the forwarded click mapped in fixed point.
    private const float WindowTolerance = 5e-3f;

    // Where a ray meets the plane of the destination's simulation screen.
    private static Vector3 OnDestinationScreen(WorldServer beyond, Vector3 origin, Vector3 direction) {
        var screen = beyond.Definition.Screens.Single(predicate: static row => (row.Index == 0));
        var normal = Vector3.Cross(
            vector1: screen.Right,
            vector2: screen.Up
        );
        var t = (Vector3.Dot(
            vector1: (screen.Origin - origin),
            vector2: normal
        ) / Vector3.Dot(
            vector1: direction,
            vector2: normal
        ));

        return (origin + (direction * t));
    }

    // THE LAW: a window fits its eye to the camera the viewer's view renders with in the same frame, chase or first
    // person, so the texel the glass shows at a point and a click through that point look along one line: through an
    // offset chase camera, the ray the window renders the texel along and the ray the click forwards into the
    // destination meet the destination at one point. The session's frame source has the world capture its frame first,
    // so the fit never reads the camera of a frame ago; a window fitted at the viewer's body, at eye height, parts from
    // the click as well.
    [Fact]
    public void ThroughAChaseCamera_TheTexelAWindowShowsAndTheClickItForwards_MeetTheDestinationAtOnePoint() {
        using var scene = new PortalScene();

        Assert.True(condition: scene.Engage());

        var row = FrustumFitFixtures.DoorRow();
        var glass = WorldWindowFrustumFit.Glass(screen: row);
        // The glass point under the pointer, a third of the way across and two fifths down.
        const float X = 0.3f;
        const float Y = 0.4f;
        var point = ((glass.Origin + ((((2f * X) - 1f) * glass.HalfWidth) * glass.Right)) + (((1f - (2f * Y)) * glass.HalfHeight) * glass.Up));
        // A chase camera: well back from the glass, above and to the side of where a body's eye would be.
        var eye = (((glass.Origin + (glass.Normal * 5f)) + (glass.Up * 1.5f)) + (glass.Right * 0.8f));
        var chase = CameraSnapshot.LookAt(
            fieldOfViewRadians: 1f,
            position: eye,
            target: glass.Origin,
            viewportHeight: 480,
            viewportWidth: 640
        );
        var bodyEye = (scene.Boot.Server.Body(index: 0)!.Position + new Vector3(
            x: 0f,
            y: 1.6f,
            z: 0f
        ));

        Assert.True(
            condition: (Vector3.Distance(
                value1: eye,
                value2: bodyEye
            ) > 1f),
            userMessage: "the chase camera must stand apart from the body's eye"
        );

        // The click: the seat's pointer ray through the glass point, cast from the camera its view renders with,
        // forwarded by the host through the door into the destination's session.
        var direction = Vector3.Normalize(value: (point - eye));
        var channels = new ChannelValues();

        channels[0] = FixedQ4816.One;
        scene.Boot.Link.SubmitIntent(submission: new IntentSubmission(
            EntityIndex: 0,
            Intent: new PlayerIntent(
                Channels: channels,
                SourceRay: new SourceRay(
                    Direction: FixedVector3.FromVector3(value: direction),
                    Origin: FixedVector3.FromVector3(value: eye)
                )
            ),
            Principal: Principal.Seat(slot: 0),
            Tick: scene.Boot.Server.NextInputTick
        ));
        scene.Step();

        var beyond = scene.Beyond;
        SourceRay? forwarded = null;

        for (var ordinal = 0; (ordinal < beyond.GrantTable.SessionOrdinalBound); ordinal++) {
            if (beyond.GrantTable.TryReadSessionPointer(
                ordinal: ordinal,
                ray: out var ray,
                session: out _
            ) && (ray is not null)) {
                forwarded = ray;
            }
        }

        Assert.NotNull(@object: forwarded);

        var clicked = OnDestinationScreen(
            beyond: beyond,
            direction: forwarded.Value.Direction.ToVector3(),
            origin: forwarded.Value.Origin.ToVector3()
        );

        // The texel: the ray the window's dressed camera renders the glass point's image point along, the window fitted
        // as the binder fits it (WorldWindowFrustumFit.FitFrom) through the session's own frame source. On screen until
        // this frame is the camera the viewer rendered with a frame ago, a step to the side; the world's capture of this
        // frame publishes the chase camera, as the presenter's dress does.
        var stale = CameraSnapshot.LookAt(
            fieldOfViewRadians: 1f,
            position: (eye + (glass.Right * 1.5f)),
            target: glass.Origin,
            viewportHeight: 480,
            viewportWidth: 640
        );
        var region = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        Vector3 Shown(bool asksTheWorldFirst) {
            var viewports = new WorldSeatViewports();

            viewports.Publish(
                camera: in stale,
                height: 480,
                region: region,
                slot: 0,
                width: 640
            );

            var session = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(),
                effectiveCameraName: null,
                mirror: new WorldSessionMirror(placeholder: beyond.Definition)
            );

            session.SetWindowFit(fit: WorldWindowFrustumFit.FitFrom(
                destination: () => beyond.Definition,
                local: () => scene.Boot.Server.Definition,
                screen: () => row,
                viewports: viewports
            ));
            _ = new WorldSessionFrameSource(
                captureHostFirst: () => {
                    if (asksTheWorldFirst) {
                        viewports.BeginFrame();
                        viewports.Publish(
                            camera: in chase,
                            height: 480,
                            region: region,
                            slot: 0,
                            width: 640
                        );
                    }
                },
                inner: new SdfCompositionFrameSource(
                    dresser: session,
                    emitters: [session]
                )
            ).CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );
            Assert.True(condition: session.TryCamera(camera: out var dressed));
            Assert.NotEqual(
                actual: dressed.FrustumOffset,
                expected: Vector2.Zero
            );

            var texel = SourceRay.Through(
                camera: dressed,
                image: new FixedVector2(
                    X: FixedQ4816.FromDouble(value: X),
                    Y: FixedQ4816.FromDouble(value: Y)
                )
            );

            return OnDestinationScreen(
                beyond: beyond,
                direction: texel.Direction.ToVector3(),
                origin: texel.Origin.ToVector3()
            );
        }

        // A window fitted before the world captured this frame renders from the camera a frame ago, and parts from the
        // click; fitted after, from this frame's camera, it meets it.
        Laws.RefusalWithControl(
            lawId: "portal.window-eye-is-this-frames-render-camera",
            deniedOutcome: () => (Vector3.Distance(
                value1: Shown(asksTheWorldFirst: false),
                value2: clicked
            ) < WindowTolerance),
            controlOutcome: () => (Vector3.Distance(
                value1: Shown(asksTheWorldFirst: true),
                value2: clicked
            ) < WindowTolerance)
        );

        // A window fitted at the viewer's body, at eye height, parts from the click too.
        Assert.True(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out var counterpart,
            destination: beyond.Definition,
            local: scene.Boot.Server.Definition,
            screenIndex: row.Index,
            source: out var source
        ));
        Assert.True(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out var bodyFit,
            destination: counterpart,
            glass: glass,
            localEye: bodyEye,
            source: source
        ));

        var bodyTexel = SourceRay.Through(
            camera: bodyFit,
            image: new FixedVector2(
                X: FixedQ4816.FromDouble(value: X),
                Y: FixedQ4816.FromDouble(value: Y)
            )
        );

        Assert.True(condition: (Vector3.Distance(
            value1: OnDestinationScreen(
                beyond: beyond,
                direction: bodyTexel.Direction.ToVector3(),
                origin: bodyTexel.Origin.ToVector3()
            ),
            value2: clicked
        ) > WindowTolerance));
    }
}
