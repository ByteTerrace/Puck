using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
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
/// view also draws its destination's stamp pool: the same ball given a frame that moves its shape animates, rides the
/// pool and still composes as the program's one instance, its root packed at its placement.
/// </summary>
public sealed class WorldSessionViewLawTests {
    private const string SessionWorld = "tests/Puck.World.Canaries/uploaded-sources/session.world.json";

    // The frame a session view of a destination renders, composed as WorldScreenBinder.RegisterSessionView composes it.
    private static SdfFrame SessionFrame(WorldDefinition definition) {
        var mirror = new WorldSessionMirror(placeholder: definition);
        var emitter = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: mirror
        );
        var source = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        return source.CaptureFrame(
            deltaSeconds: 0f,
            height: WorldViewInstances.DefaultSessionHeight,
            interpolationAlpha: 0f,
            width: WorldViewInstances.DefaultSessionWidth
        );
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SessionHistoryUsesTheDestinationsOwnRenderSetting(bool temporal) {
        var definition = AuthoredGameFixtures.Load(relativePath: SessionWorld);
        var frame = SessionFrame(definition: definition with { RenderRaw = definition.Render with { Temporal = temporal } });

        Assert.Equal(expected: temporal, actual: Assert.Single(collection: frame.Views).Temporal);
    }
    [InlineData(160, 144)]
    [InlineData(2048, 256)]
    [Theory]
    public void AuthoredSessionResolutionDeterminesTheFirstCameraAspect(int width, int height) {
        var emitter = new WorldSessionSceneEmitter(effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: AuthoredGameFixtures.Load(relativePath: SessionWorld)));
        var source = new WorldSessionFrameSource(inner: new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]),
            captureHostFirst: static () => { }, resolution: new WorldScreenResolution(Height: height, Width: width));

        foreach (var requested in new[] { (1280u, 720u), (4096u, 4096u), (32u, 32u) }) {
            var frame = source.CaptureFrame(deltaSeconds: 0f, height: requested.Item2, interpolationAlpha: 0f, width: requested.Item1);

            Assert.Equal(expected: (width / ((float)height)), actual: frame.Views[0].Camera.AspectRatio);
        }
    }
    [Fact]
    public void ASessionViewOfAnAnimatedCreationIncludesItsInstance() {
        var definition = AuthoredGameFixtures.Load(relativePath: SessionWorld);
        var ball = Assert.Single(collection: definition.Creations);
        var shape = Assert.Single(collection: (ball.Document.Shapes ?? []));
        var animated = (ball with {
            Document = (ball.Document with {
                Frames = [new FrameDocument(
                    Name: "lift",
                    Transforms: [new FrameTransformDocument(
                        Id: shape.Id,
                        Position: (shape.Position.Value + Vector3.UnitY),
                        Rotation: shape.Rotation,
                        Scale: shape.Scale
                    )]
                )],
            }),
        });
        var placement = Assert.Single(collection: definition.Placements);

        Assert.True(condition: WorldPlacementStamper.IsAnimated(creation: animated));

        var frame = SessionFrame(definition: (definition with { CreationsRaw = [animated] }));

        // The animated ball rides the destination's stamp pool, packed past the avatar catalog's slots: its instance is
        // the program's one, and its root sits at its placement.
        _ = Assert.Single(collection: frame.Program.Instances);
        Assert.Equal(
            actual: frame.DynamicTransforms[WorldRigCatalog.DynamicTransformCapacity].Position,
            expected: placement.Position.Value
        );
    }
    [Fact]
    public void TheCanarysSessionViewFramesItsBall() {
        var frame = SessionFrame(definition: AuthoredGameFixtures.Load(relativePath: SessionWorld));

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
