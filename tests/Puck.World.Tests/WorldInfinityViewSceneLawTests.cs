using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the scene an infinity view renders (<see cref="WorldInfinityViewScene"/>) is the destination's
/// world drawn as a session screen draws it, or only the named prototypes of a world for far geometry
/// (<c>WorldSessionSceneEmitter</c>'s <c>onlyPrototypes</c>); its consuming views share one scene and each takes the camera
/// the presentation fitted this frame, at a quality that leaves soft shadows and ambient occlusion off unless the
/// layer's levers turn them on, and its own far distance.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldInfinityViewSceneLawTests {
    private const string Destination = "tests/Puck.World.Canaries/uploaded-sources/session.puck";

    private static InfinityViewSpec Spec(InfinityViewLevers levers = InfinityViewLevers.None) => new(
        Anchor: new Vector3(x: 11f, y: 4f, z: -6f),
        Fallback: Vector3.Zero,
        FarDistance: 777f,
        Kind: InfinityViewKind.World,
        Levers: levers,
        Mask: new InfinityViewMask(Axis: -Vector3.UnitZ, HalfAngle: 0.2f),
        MinimumTier: QualityTier.Low,
        Name: "lobby",
        Orientation: Quaternion.Identity,
        Refresh: 1,
        Scale: 1f
    );
    private static SdfFrame Frame(WorldDefinition definition, InfinityViewSpec spec, Func<InfinityViewFrame> fitted, IReadOnlySet<string>? only = null) {
        var emitter = new WorldSessionSceneEmitter(
            domains: new WorldValueDomainGuard(),
            effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: definition),
            onlyPrototypes: only
        );
        var scene = new WorldInfinityViewScene(inner: emitter, spec: spec, views: () => {
            var frame = fitted();

            return (frame.Visible ? [new SdfViewSnapshot(Camera: frame.Camera, Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))] : []);
        });

        return new SdfCompositionFrameSource(dresser: scene, emitters: [emitter]).CaptureFrame(
            deltaSeconds: 0f,
            height: 144u,
            interpolationAlpha: 0f,
            width: 160u
        );
    }
    private static InfinityViewFrame Fitted(InfinityViewSpec spec) => InfinityViewFit.Fit(
        spec: spec,
        tier: QualityTier.High,
        viewer: CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 900f, y: 0f, z: 0f), target: new Vector3(x: 900f, y: 0f, z: -1f), viewportHeight: 720f, viewportWidth: 1280f),
        viewerHeight: 720u,
        viewerWidth: 1280u
    );

    [Fact]
    public void TheViewRendersFromTheFittedCameraAtItsOwnFarDistance() {
        var spec = Spec();
        var fitted = Fitted(spec: spec);
        var frame = Frame(definition: AuthoredGameFixtures.Load(relativePath: Destination), spec: spec, fitted: () => fitted);
        var view = Assert.Single(collection: frame.Views);

        Assert.Equal(expected: fitted.Camera, actual: view.Camera);
        Assert.Equal(expected: spec.Anchor, actual: view.Camera.Position);
        Assert.Equal(expected: 777f, actual: frame.FarDistance);
    }
    [Fact]
    public void TwoFittedConsumersShareOneSceneAndKeepTheirNestedSkyBindings() {
        var spec = Spec();
        var first = Fitted(spec: spec).Camera;
        var second = new CameraSnapshot(Position: new Vector3(x: -11f, y: 7f, z: 3f), Right: first.Right,
            Up: first.Up, Forward: first.Forward, TanHalfFieldOfView: first.TanHalfFieldOfView, AspectRatio: first.AspectRatio);
        var nested = new SdfSkyViewBinding(Layer: "nested", Producer: "camera$sky$nested", Parameters: default);
        SdfViewSnapshot[] views = [
            new(Camera: first, Region: new NormalizedRect(X: 0, Y: 0, Width: 1, Height: 1)) { Quality = new SdfViewQuality { IndirectMethod = SdfIndirectMethod.Screen } },
            new(Camera: second, Region: new NormalizedRect(X: 0, Y: 0, Width: 1, Height: 1)) { SkyViews = [nested], Quality = new SdfViewQuality { IndirectMethod = SdfIndirectMethod.Cone } },
        ];
        using var emitter = new WorldSessionSceneEmitter(
            domains: new WorldValueDomainGuard(), effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: AuthoredGameFixtures.Load(relativePath: Destination)));
        var scene = new WorldInfinityViewScene(inner: emitter, views: () => views, spec: spec);
        var frame = new SdfCompositionFrameSource(dresser: scene, emitters: [emitter]).CaptureFrame(
            deltaSeconds: 0, interpolationAlpha: 0, width: 160, height: 144);

        Assert.Equal(expected: new[] { first, second }, actual: frame.Views.Select(selector: static view => view.Camera));
        Assert.Equal(expected: nested, actual: Assert.Single(collection: frame.Views[1].SkyViews));
        Assert.Equal(expected: new[] { SdfIndirectMethod.Screen, SdfIndirectMethod.Cone }, actual: frame.Views.Select(selector: static view => view.Quality.IndirectMethod));
        Assert.All(collection: frame.Views, action: static view => Assert.True(condition: view.Quality.DisableSoftShadows));
    }
    [Fact]
    public void ShadowsAndAmbientOcclusionStayOffUnlessTheViewsLeversTurnThemOn() {
        var definition = AuthoredGameFixtures.Load(relativePath: Destination);

        foreach (var (levers, occlusionOff, shadowsOff) in new[] {
            (InfinityViewLevers.None, true, true),
            (InfinityViewLevers.Shadows, true, false),
            (InfinityViewLevers.AmbientOcclusion, false, true),
            (InfinityViewLevers.Shadows | InfinityViewLevers.AmbientOcclusion, false, false),
        }) {
            var spec = Spec(levers: levers);
            var view = Assert.Single(collection: Frame(definition: definition, spec: spec, fitted: () => Fitted(spec: spec)).Views);

            Assert.Equal(expected: occlusionOff, actual: view.Quality.DisableAmbientOcclusion);
            Assert.Equal(expected: shadowsOff, actual: view.Quality.DisableSoftShadows);
        }
    }
    [Fact]
    public void AViewOutsideTheViewersFrustumKeepsTheEmittersOwnFrame() {
        var spec = Spec();
        var frame = Frame(definition: AuthoredGameFixtures.Load(relativePath: Destination), spec: spec, fitted: static () => default);

        Assert.NotEqual(expected: 777f, actual: frame.FarDistance);
        Assert.NotEqual(expected: spec.Anchor, actual: Assert.Single(collection: frame.Views).Camera.Position);
    }
    [Fact]
    public void FarGeometryHoldsOnlyTheNamedPrototypes() {
        var authored = AuthoredGameFixtures.Load(relativePath: Destination);
        var ball = Assert.Single(collection: authored.Creations);
        var placement = Assert.Single(collection: authored.Placements);
        // Two prototypes, each placed once: the ball, and a second creation of the same shape under another id.
        var definition = (authored with {
            CreationsRaw = [ball, ball with { Id = new DocumentIdentifier(value: "moon") }],
            PlacementRowsRaw = [placement, placement with { Id = "moon-1", PrototypeId = "moon", Position = new DocumentVector3(value: new Vector3(x: 5f, y: 0f, z: 0f)) }],
        });
        var spec = Spec() with { Kind = InfinityViewKind.Far };

        InfinityViewFrame Fit() => Fitted(spec: spec);

        var everything = Frame(definition: definition, spec: spec, fitted: Fit);
        var ballOnly = Frame(definition: definition, spec: spec, fitted: Fit, only: new HashSet<string> { placement.PrototypeId });
        var moonOnly = Frame(definition: definition, spec: spec, fitted: Fit, only: new HashSet<string> { "moon" });
        var neither = Frame(definition: definition, spec: spec, fitted: Fit, only: new HashSet<string> { "no-such-prototype" });

        Assert.Equal(expected: 2, actual: everything.Program.Instances.Count);
        Assert.Single(collection: ballOnly.Program.Instances);
        Assert.Single(collection: moonOnly.Program.Instances);
        Assert.NotEqual(expected: ballOnly.Program.Instances[0].Center, actual: moonOnly.Program.Instances[0].Center);
        Assert.Empty(collection: neither.Program.Instances);
    }
}
