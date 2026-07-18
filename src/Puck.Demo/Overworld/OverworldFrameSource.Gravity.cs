using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;
using Puck.Demo.Gravity;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.Demo.Overworld;

/// <summary>
/// The gravity scenario's frame-source surface: a creating-slot-style takeover with one small
/// planetoid, ALONE in its own alternate <see cref="SdfCompositionFrameSource"/> at a far
/// <see cref="Puck.Maths.WorldCoord3"/> cell (<see cref="GravityScenario.PlanetCenter"/>), viewed fullscreen through
/// its OWN <see cref="OrientedFollowRig"/> chase camera riding the walker's published anchor. Unlike the SDF-debug/AGB
/// takeovers (which still route through this file's shared <see cref="Dress"/> and the room's own view stack —
/// <c>m_currentViews</c>), the gravity takeover's content lives in a COMPLETELY separate coordinate frame (a
/// different far cell than the room's own), so it carries its OWN dedicated <see cref="ISdfFrameDresser"/>
/// (<see cref="GravityDresser"/>) and its OWN small <see cref="SdfAnchorTable"/> — the same pattern
/// <c>NestedWorldDresser</c>/<c>GalleryExhibitDresser</c> already use for a standalone nested world, just promoted to
/// a top-level fullscreen takeover so a capture actually shows the whole planetoid (the framing lesson: a screen-slab
/// wired presentation would read as a speck at room-capture scale).
/// <para>
/// SCOPING: entering gravity mode (<see cref="SpawnGravityWalker"/>) does NOT force-exit the other four
/// creating-slot takeovers (creator/world-sculpt/sdf-debug/AGB) the way <c>OverworldRenderNode</c>'s own mode
/// switches mutually exclude each other — this proof's only entry point is the <c>planet.*</c> console verbs, which
/// never run alongside those pad-driven modes in the same script. A future consumer combining them needs that
/// cross-mode exclusion added at the render-node level (the same place the existing four already coordinate it).
/// </para>
/// </summary>
public sealed partial class OverworldFrameSource {
    /// <summary>The stable anchor name <see cref="GravityDresser"/> publishes the walker's pose under, resolved by
    /// its own <see cref="OrientedFollowRig"/> every frame.</summary>
    private const string FieldWalkerAnchorName = "player.field-walker";

    private SdfCompositionFrameSource m_gravityComposition = null!;
    private readonly SdfAnchorTable m_gravityAnchorTable = new();
    // Offsets tuned against GravityScenario.PlanetRadius (7) + the walker's own ~1.3-unit-tall capsule (the arc's
    // framing pass): OrientedFollowRig's stock (0, 2.2, 5) chase offset — sized for a room-scale player box — sits
    // nearly INSIDE a radius-7 sphere. THE FRAMING LESSON: EyeOffset.Y rides the anchor's local Y axis, which is the
    // walker's "up" — the FIELD GRADIENT, not world Y (an equatorial walker's up sits in the world XZ plane) — so a
    // big Y component pulls the eye OUT ALONG THE PLANET'S OWN RADIUS (a "look down from above the walker's own
    // zenith" shot), not "up the screen." A modest Z (along the walker's facing) then leans the shot into a 3/4 view
    // that reads the planet's curvature AND keeps the avatar a recognizable capsule silhouette rather than a
    // grazing-angle sliver (the failure mode every smaller/near-tangent offset produced during tuning).
    private readonly OrientedFollowRig m_gravityCameraRig = new() {
        EyeOffset = new Vector3(x: 0f, y: 10f, z: 6f),
        TargetOffset = new Vector3(x: 0f, y: 1.0f, z: 0f),
    };
    private bool m_gravityActive;
    private SdfProgram? m_lastGravityProgram;
    private float m_gravityTime;

    /// <summary>Whether the planetoid takeover is currently showing.</summary>
    public bool GravityActive => m_gravityActive;

    // Binds the ONE field evaluator (see GravityScenario's single-source-of-truth remarks) and builds the alternate
    // composition — called once from the constructor, mirroring the SDF-debug composition's own ctor-time wiring.
    private void InitializeGravity() {
        m_world.ConfigureFieldEvaluator(evaluator: GravityScenario.BuildEvaluator());
        m_gravityComposition = new SdfCompositionFrameSource(
            dresser: new GravityDresser(owner: this),
            emitters: [
                new PlanetoidEmitter(),
                new WalkerInstanceEmitter(world: m_world),
            ]
        );
    }

    /// <summary>Spawns/resets the walker at a start longitude and activates the fullscreen planetoid takeover — the
    /// <c>planet.spawn</c> console verb's implementation.</summary>
    /// <param name="longitudeDegrees">The starting equatorial longitude, degrees (see
    /// <see cref="GravityScenario.StartPose"/>).</param>
    public void SpawnGravityWalker(double longitudeDegrees) {
        var (position, up) = GravityScenario.StartPose(longitudeDegrees: (float)longitudeDegrees);

        m_world.SpawnFieldWalker(position: position, up: up);
        m_gravityActive = true;
    }

    /// <summary>Queues ticks of scripted walk intent — the <c>planet.walk</c> console verb's implementation. Converts
    /// the console layer's float direction to fixed-point HERE (mirrors <see cref="MoveSelectedRtsUnits"/>'s
    /// double-to-<see cref="FixedQ4816"/> boundary), then forwards to <see cref="OverworldWorld.WalkFieldWalker"/>
    /// (see its remarks for the exact-tick determinism story).</summary>
    /// <param name="ticks">How many ticks to apply <paramref name="move"/> for.</param>
    /// <param name="move">The tangent-plane move vector (X = strafe, Y = forward).</param>
    public void WalkGravityWalker(int ticks, Vector2 move) {
        m_world.WalkFieldWalker(ticks: ticks, move: new FixedVector2(X: FixedQ4816.FromDouble(value: move.X), Y: FixedQ4816.FromDouble(value: move.Y)));
    }

    /// <summary>The walker's current state — the <c>planet.list</c> console verb's data source. Forwards to
    /// <see cref="OverworldWorld.FieldWalkerState"/>.</summary>
    public OverworldWorld.FieldWalkerSnapshot GravityWalkerState() =>
        m_world.FieldWalkerState();

    // The gravity composition's dedicated dresser: computes the OrientedFollowRig chase camera from the walker's
    // published anchor and returns ONE fullscreen SdfViewSnapshot — never reads m_currentViews/the room's director
    // (see the type remarks on why this takeover carries its own dresser rather than sharing this file's Dress).
    private sealed class GravityDresser(OverworldFrameSource owner) : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            owner.DressGravity(program: program, transforms: transforms, width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
    }

    private SdfFrame DressGravity(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_gravityTime += MathF.Max(x: deltaSeconds, y: 0f);

        var walkerTransform = m_world.FieldWalkerTransform(renderOrigin: GravityScenario.PlanetCenter, alpha: interpolationAlpha);

        // Publish THIS frame's walker pose, then resolve it right back through the SAME table/rig seam a real
        // by-name consumer would use (the SdfAnchor verdict — see FieldWalkerBody's remarks: presentation, not sim).
        m_gravityAnchorTable.BeginTick();
        var anchorId = m_gravityAnchorTable.Publish(name: FieldWalkerAnchorName, pose: new SdfAnchor(Position: walkerTransform.Position, Orientation: walkerTransform.Orientation));

        _ = m_gravityAnchorTable.TryResolveAnchor(anchorId: anchorId, anchor: out var anchor);

        var (eye, target, fov) = m_gravityCameraRig.Resolve(anchor: in anchor, time: m_gravityTime);
        var camera = CameraSnapshot.LookAt(position: eye, target: target, fieldOfViewRadians: fov, viewportWidth: width, viewportHeight: height);
        var programChanged = !ReferenceEquals(objA: program, objB: m_lastGravityProgram);

        m_lastGravityProgram = program;

        return new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Time: m_gravityTime,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            WarpAmount: 0f
        ) {
            // The STUDIO mood's own "flat and bright" pair (see this file's StudioAmbientScale/StudioSunScale): a
            // planetoid is lit from one fixed direction like everything else, so a walker circumnavigating it always
            // crosses a real night side — a high ambient keeps that side LEGIBLE for a capture instead of near-black,
            // rather than faking a sourceless flat light (AmbientScale/SunScale both 1 read fine lit, unreadable dark).
            AmbientScale = 1.7f,
            DynamicTransforms = transforms,
            SunScale = 0.6f,
        };
    }
}
