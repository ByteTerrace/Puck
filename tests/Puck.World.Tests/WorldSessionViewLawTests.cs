using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the <c>uploaded-sources</c> canary's session destination (<c>session.world.json</c>) frames its ball in the
/// frame a session view renders. The frame is composed as <c>WorldScreenBinder.RegisterSessionView</c> composes it, a
/// <see cref="WorldSessionSceneEmitter"/> over the destination's mirror through its own
/// <see cref="SdfCompositionFrameSource"/>, at the session's default extent. The camera stands outside every surface of
/// the program, and the fixed-point raycast of the view's central ray (<see cref="SdfFieldEvaluator.Raycast"/>) meets the
/// ball, the program's one instance, within the render far distance, so the screen's centre shows the ball. A session
/// view renders only static placements: a creation that declares frames or drivers rides the stamp pool, which no
/// session view renders, so such a ball would compose no instance and the screen would show the destination's sky.
/// </summary>
public sealed class WorldSessionViewLawTests {
    private const string SessionWorld = "tests/Puck.World.Canaries/uploaded-sources/session.world.json";

    [Fact]
    public void TheCanarysSessionViewFramesItsBall() {
        var definition = AuthoredGameFixtures.Load(relativePath: SessionWorld);
        var mirror = new WorldSessionMirror(placeholder: definition);
        var emitter = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: mirror
        );
        var source = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );
        var frame = source.CaptureFrame(
            deltaSeconds: 0f,
            height: WorldViewInstances.DefaultSessionHeight,
            interpolationAlpha: 0f,
            width: WorldViewInstances.DefaultSessionWidth
        );

        // The ball is the destination's one static placement, and it has no population to mirror.
        _ = Assert.Single(collection: frame.Program.Instances);

        var camera = frame.Views[0].Camera;
        var field = new SdfFieldEvaluator(program: frame.Program);
        var eye = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: camera.Position));

        Assert.True(condition: field.TryDistance(
            distance: out var eyeDistance,
            material: out _,
            position: eye
        ));
        Assert.True(
            condition: (eyeDistance > FixedQ4816.Zero),
            userMessage: $"the camera at {camera.Position} stands inside a surface ({((double)eyeDistance)} units)"
        );
        Assert.True(
            condition: field.Raycast(
                dir: FixedVector3.FromVector3(value: camera.Forward),
                hit: out _,
                maxDist: FixedQ4816.FromDouble(value: frame.FarDistance),
                origin: eye
            ),
            userMessage: $"the central ray from {camera.Position} along {camera.Forward} meets no surface"
        );
    }
}
