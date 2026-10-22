using System.Globalization;
using System.Numerics;

using Puck.Cli.Mesh;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World;
using Puck.World.Client;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Motion;

/// <summary>Judges whether the slot cameras of a layout see a body: the eyes stand clear of the world's geometry and the line from each
/// eye to the camera's aim point crosses none.</summary>
public interface IMotionClearance {
    /// <summary>Checks one scenario's stills.</summary>
    /// <param name="scenario">The scenario as laid out on ticks.</param>
    /// <param name="stills">The scenario's stills, each with the <c>body.where 0</c> read of its tick.</param>
    /// <param name="cameras">The cameras of the layout the scenario ran in.</param>
    /// <returns>One line for each camera blocked at each still, naming the scenario, the tick, the camera and what blocked it; empty when
    /// every eye at every still is clear.</returns>
    IReadOnlyList<string> Check(MotionPlannedScenario scenario, IReadOnlyList<MotionStillResult> stills, IReadOnlyList<string> cameras);
}
/// <summary>
/// The camera clearance check of <c>puck motion</c>, made on the CPU from the world's own documents. Each slot camera's rig is
/// compiled as the World compiles it (<see cref="WorldCameraRigCompiler"/>) and resolved against body 0's pose at the still's tick,
/// giving the eye and the aim point a frame would use. The geometry is the one the seat camera's clearance keeps its eye out of
/// (<see cref="WorldCameraClearance"/>): every placement the field query can read, emitted as <see cref="WorldPlacementStamper.EmitStatic"/>
/// emits it and read through <see cref="SdfFieldEvaluator"/>, which holds no body. An eye is blocked when a sphere of
/// <see cref="WorldCameraClearance.EyeRadius"/> about it overlaps geometry, and the sight line is blocked when a thin sphere swept from
/// the eye to the aim point hits geometry before the point.
/// </summary>
public sealed class MotionClearance : IMotionClearance {
    /// <summary>The radius, in metres, of the sphere swept along a camera's sight line.</summary>
    public const float SightRadius = 0.05f;
    /// <summary>How far short of the aim point, in metres, a sight line is judged: the surface the body stands on is not an obstruction.</summary>
    public const float AimSkin = 0.2f;

    private static readonly FixedQ4816 FixedSightRadius = FixedQ4816.FromDouble(value: SightRadius);
    private static readonly FixedQ4816 FixedEyeRadius = FixedQ4816.FromDouble(value: WorldCameraClearance.EyeRadius);

    private readonly WorldDefinition m_definition;
    private readonly SdfFieldEvaluator m_field;
    private readonly Dictionary<string, IWorldCameraProgramRig> m_rigs = new(comparer: StringComparer.Ordinal);
    private readonly IReadOnlyList<WorldPlacement> m_placements;

    private MotionClearance(WorldDefinition definition, SdfFieldEvaluator field, IReadOnlyList<WorldPlacement> placements, WorldClient client) {
        m_definition = definition;
        m_field = field;
        m_placements = placements;

        var domains = new WorldValueDomainGuard();

        foreach (var camera in definition.Cameras) {
            if (camera.Rig is { } rig) {
                m_rigs[camera.Name] = WorldCameraRigCompiler.Compile(definition: definition, domains: domains, mirror: client.StateMirror, program: rig);
            }
        }
    }

    /// <summary>Reads a world and builds the geometry its cameras are checked against.</summary>
    /// <param name="worldPath">The world: a package folder or a document.</param>
    /// <param name="clearance">The check, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="error">Why the world could not be read, or empty.</param>
    /// <returns><see langword="true"/> when the check was built.</returns>
    public static bool TryCreate(string worldPath, out MotionClearance? clearance, out string error) {
        clearance = null;

        var fullPath = Path.GetFullPath(path: worldPath);

        // A package folder names its entry document in its manifest.
        if (Directory.Exists(path: fullPath)) {
            try {
                using var manifest = System.Text.Json.JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: fullPath, path2: "manifest.json")));
                var entry = manifest.RootElement.GetProperty(propertyName: "entry").GetString();
                var candidate = Path.Combine(path1: fullPath, path2: entry!);

                fullPath = (File.Exists(path: candidate) ? candidate : $"{candidate}.puck");
            } catch (Exception exception) when ((exception is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException)) {
                error = $"the package '{fullPath}' names no entry document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }
        }

        if (!WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: fullPath, proveNeighbours: false, reason: out var reason)) {
            error = $"'{fullPath}' failed to load: {reason}";

            return false;
        }

        var definition = admission!.Definition;
        var usable = new List<WorldPlacement>();

        // A placement the field query has no twin for (a render-only fold) is left out alone, as the presentation leaves out the
        // whole field; the rest of the world still answers.
        foreach (var placement in definition.Placements) {
            try {
                var builder = new SdfProgramBuilder();

                WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, definition: definition, placements: [placement]);
                _ = new SdfFieldEvaluator(program: builder.Build());
                usable.Add(item: placement);
            } catch (ArgumentException) {
                continue;
            }
        }

        var all = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(builder: all, creations: definition.Creations, definition: definition, placements: usable);

        var inert = new RestScene.Inert();
        var client = new WorldClient(
            composition: new WorldCompositionState(),
            definition: definition,
            roster: new PlayerRoster(definition: definition, link: inert, seatBindings: new WorldSeatBindings(definition: definition)),
            seatRouter: new WorldSeatAuthorityRouter()
        );

        clearance = new MotionClearance(client: client, definition: definition, field: new SdfFieldEvaluator(program: all.Build()), placements: usable);
        error = string.Empty;

        return true;
    }
    /// <summary>Resolves the eye and the aim point of a camera for a body.</summary>
    /// <param name="camera">The camera's name.</param>
    /// <param name="position">Body 0's world position.</param>
    /// <param name="yawDegrees">Body 0's heading, in degrees; 0 faces -Z and positive turns left.</param>
    /// <param name="eye">The eye, in metres.</param>
    /// <param name="target">The aim point, in metres.</param>
    /// <returns><see langword="false"/> when the world has no such camera program.</returns>
    public bool TryResolve(string camera, Vector3 position, double yawDegrees, out Vector3 eye, out Vector3 target) {
        eye = default;
        target = default;

        if (!m_rigs.TryGetValue(key: camera, value: out var rig)) {
            return false;
        }

        var anchor = new SdfAnchor(Orientation: Quaternion.CreateFromAxisAngle(angle: ((float)(yawDegrees * (Math.PI / 180d))), axis: Vector3.UnitY), Position: position);

        (eye, target, _) = rig.Resolve(anchor: in anchor, clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f));

        return true;
    }
    /// <summary>Says what blocks a camera, if anything.</summary>
    /// <param name="eye">The eye, in metres.</param>
    /// <param name="target">The aim point, in metres.</param>
    /// <returns>A description of the obstruction, or <see langword="null"/> when the eye is clear and sees the point.</returns>
    public string? Blocked(Vector3 eye, Vector3 target) {
        var fixedEye = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: eye));

        if (m_field.Overlap(center: fixedEye, radius: FixedEyeRadius)) {
            return $"the eye ({Format(point: eye)}) is inside geometry or within {WorldCameraClearance.EyeRadius.ToString(provider: CultureInfo.InvariantCulture)} m of it, {Nearest(point: eye)}";
        }

        var offset = (target - eye);
        var length = offset.Length();

        if (!(length > AimSkin)) {
            return null;
        }

        var direction = FixedVector3.FromVector3(value: (offset / length)).Normalize();

        if (!m_field.SphereCast(dir: direction, hit: out var hit, maxDist: FixedQ4816.FromDouble(value: (length - AimSkin)), origin: fixedEye, radius: FixedSightRadius)) {
            return null;
        }

        var point = (eye + ((offset / length) * ((float)((double)hit.Distance))));

        return $"the line from the eye ({Format(point: eye)}) to the aim point ({Format(point: target)}) meets geometry {((double)hit.Distance).ToString(format: "0.00", provider: CultureInfo.InvariantCulture)} m out at ({Format(point: point)}), {Nearest(point: point)}";
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> Check(MotionPlannedScenario scenario, IReadOnlyList<MotionStillResult> stills, IReadOnlyList<string> cameras) {
        var problems = new List<string>();

        foreach (var still in stills) {
            if (!MotionRig.TryPoseOf(answer: still.Where, position: out var position, yawDegrees: out var yaw)) {
                problems.Add(item: $"scenario '{scenario.Scenario.Name}' tick {still.Tick.ToString(provider: CultureInfo.InvariantCulture)}: the body's pose was not read, so no camera could be checked");

                continue;
            }

            foreach (var camera in cameras) {
                if (!TryResolve(camera: camera, eye: out var eye, position: position, target: out var target, yawDegrees: yaw)) {
                    problems.Add(item: $"scenario '{scenario.Scenario.Name}' tick {still.Tick.ToString(provider: CultureInfo.InvariantCulture)}: camera '{camera}' has no camera program in the world, so it could not be checked");

                    continue;
                }
                if (Blocked(eye: eye, target: target) is { } why) {
                    problems.Add(item: $"scenario '{scenario.Scenario.Name}' tick {still.Tick.ToString(provider: CultureInfo.InvariantCulture)} (t+{still.Offset.ToString(provider: CultureInfo.InvariantCulture)}): camera '{camera}' is blocked: {why}");
                }
            }
        }

        return problems;
    }

    private static string Format(Vector3 point) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{point.X:0.00}, {point.Y:0.00}, {point.Z:0.00}");
    // Names the placement whose geometry lies nearest a point, built only when something is blocked.
    private string Nearest(Vector3 point) {
        var best = default(WorldPlacement);
        var bestDistance = double.PositiveInfinity;
        var at = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point));

        foreach (var placement in m_placements) {
            try {
                var builder = new SdfProgramBuilder();

                WorldPlacementStamper.EmitStatic(builder: builder, creations: m_definition.Creations, definition: m_definition, placements: [placement]);

                if (new SdfFieldEvaluator(program: builder.Build()).TryDistance(distance: out var distance, material: out _, position: at) && (((double)distance) < bestDistance)) {
                    best = placement;
                    bestDistance = ((double)distance);
                }
            } catch (ArgumentException) {
                continue;
            }
        }

        return ((best is null)
            ? "no placement named"
            : $"nearest placement '{best.Id}' (prototype '{best.PrototypeId}', {bestDistance.ToString(format: "0.00", provider: CultureInfo.InvariantCulture)} m)"
        );
    }
}
