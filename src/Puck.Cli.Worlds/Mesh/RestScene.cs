using System.Numerics;

using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.Cli.Mesh;

/// <summary>A body the rest scene stands: its index, the creation it wears, its pose, its look's scale and the first of its
/// pool's transform slots, whose next slots carry its creation's shapes in order.</summary>
/// <param name="BodyIndex">The body's entity index.</param>
/// <param name="Creation">The prototype whose creation the body wears.</param>
/// <param name="Origin">The body's world position.</param>
/// <param name="Scale">The look's uniform render scale.</param>
/// <param name="RootSlot">The body's root transform slot.</param>
public sealed record RestBody(int BodyIndex, WorldPrototype Creation, Vector3 Origin, float Scale, int RootSlot) {
    /// <summary>Returns a world point in the creation's author frame: about the body, unscaled, then turned a half turn
    /// about up (<see cref="Puck.World.Authoring.CreationFrame"/>).</summary>
    /// <param name="world">The world point, in metres.</param>
    /// <returns>The point as the creation's document spells it.</returns>
    public Vector3 ToAuthor(Vector3 world) {
        var local = ((world - Origin) / Scale);

        return new Vector3(x: -local.X, y: local.Y, z: -local.Z);
    }
}

/// <summary>
/// A world's presentation at rest, composed offline as a World composes its frame (<see cref="WorldSceneEmitter"/> through
/// <see cref="SdfCompositionFrameSource"/>), with no device: one body per creation look, standing unturned at rest, so
/// the program and transforms a World's first frame bakes are those its groups are planned from. It names each bake
/// group and each shape instruction by the body and creation shape a pool's slot carries.
/// </summary>
public sealed class RestScene {
    // The spacing, in metres, between the bodies the scene stands, past any creation's reach.
    private const float BodySpacing = 8f;

    // The frame's instances by the placements and prototypes they stand for.
    private readonly ISdfPickMap? m_picks;

    private RestScene(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, IReadOnlyList<RestBody> bodies, ISdfPickMap? picks) {
        m_picks = picks;
        Program = program;
        Transforms = transforms;
        Bodies = bodies;
    }

    /// <summary>Gets the composed program.</summary>
    public SdfProgram Program { get; }
    /// <summary>Gets the frame's dynamic transforms.</summary>
    public IReadOnlyList<DynamicTransform> Transforms { get; }
    /// <summary>Gets the bodies the scene stands.</summary>
    public IReadOnlyList<RestBody> Bodies { get; }

    /// <summary>Composes a world's rest scene.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="sourcePath">The path the world was read from, which its text resolves beside.</param>
    /// <returns>The scene.</returns>
    public static RestScene Compose(WorldDefinition definition, string sourcePath) {
        var routes = new WorldSeatAuthorityRouter();
        var inert = new Inert();
        var client = new WorldClient(
            composition: new WorldCompositionState(),
            definition: definition,
            roster: new PlayerRoster(definition: definition, link: inert, seatBindings: new WorldSeatBindings(definition: definition)),
            seatRouter: routes
        );
        var pool = new WorldStampPool();
        var emitter = new WorldSceneEmitter(
            animator: pool,
            audio: inert,
            client: client,
            continuum: new WorldContinuum(client, routes, inert),
            text: new WorldTextCatalog(source: new WorldDefinitionSource(Definition: definition, SourcePath: sourcePath))
        );
        var dresser = new KeptFrame();
        var frames = new SdfCompositionFrameSource(dresser: dresser, emitters: [emitter]);
        var looks = definition.Looks;
        var entries = new List<EntitySnapshot>();
        // Each body's register file as a server births it, so a stamp scale the world keeps in a register
        // (bodies.scale) reads its authored initial value rather than the zero an undelivered register reads as.
        var file = FixedWorldKit.CompileActionStateFile(bodyState: definition.BodyState, identityState: definition.IdentityState);
        var state = new long[file.Length];

        for (var slot = 0; (slot < file.Length); slot++) {
            state[slot] = WorldBodyStateReads.Presented(kind: file[slot].Kind, raw: WorldActionStateLane.InitialRaw(definition: in file[slot]));
        }
        for (var look = 0; ((look < looks.Count) && (look <= byte.MaxValue)); look++) {
            if (looks[look].Source is not WorldLookSource.Creation) { continue; }
            entries.Add(item: new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Teleport,
                Generation: 1,
                Index: entries.Count,
                Kit: 0,
                Look: (byte)look,
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: (entries.Count * BodySpacing), y: 0f, z: 0f),
                State: state
            ));
        }

        client.DeliverSnapshot(snapshot: new WorldSnapshot(Authority: "rest", Entries: entries.ToArray(), Revision: 1, StepTicks: 1UL, Tick: 1UL));
        client.UpdateRenderPoses(alpha: 1f);
        ISdfPickMap? picks = null;

        // Two frames: the first registers the bodies' stamps, the second packs their poses.
        for (var frame = 0; (frame < 2); frame++) {
            emitter.Tick(deltaSeconds: 0f);
            picks = frames.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 1f, width: 64).PickMap;
        }

        var bodies = new List<RestBody>();

        foreach (var entry in entries) {
            if (!pool.TryBodyTransformSlot(bodyIndex: entry.Index, transformSlot: out var root)) { continue; }

            var look = looks[entry.Look];
            var prototype = ((WorldLookSource.Creation)look.Source).PrototypeId.Value;

            if (definition.Creations.FirstOrDefault(predicate: creation => string.Equals(a: creation.Id, b: prototype, comparisonType: StringComparison.Ordinal)) is not { } creation) { continue; }
            bodies.Add(item: new RestBody(BodyIndex: entry.Index, Creation: creation, Origin: entry.Position, RootSlot: root, Scale: look.Scale));
        }

        return new RestScene(bodies: bodies, picks: picks, program: dresser.Program!, transforms: dresser.Transforms);
    }
    /// <summary>Returns the body whose pool carries a dynamic group's slot, or <see langword="null"/> for a static
    /// group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The body, or <see langword="null"/>.</returns>
    public RestBody? Owner(SdfMeshBakeGroup group) => (group.Dynamic ? BodyOf(slot: group.Slot) : null);
    /// <summary>Returns a group's label: its slot and the body and shape the slot carries, its first instance, or the world
    /// segments.</summary>
    /// <param name="group">The group.</param>
    /// <param name="owner">The body that carries it, or <see langword="null"/>.</param>
    /// <returns>The label.</returns>
    public string Label(SdfMeshBakeGroup group, RestBody? owner) {
        if (group.IsWorld) { return "world segments"; }
        if (!group.Dynamic) { return $"instance {-(group.Selector + 1)}{((group.Instances > 1) ? $"+{group.Instances - 1}" : string.Empty)}{Placed(instance: -(group.Selector + 1))}"; }
        if (owner is null) { return $"slot {group.Slot}"; }

        return $"slot {group.Slot} {owner.Creation.Id} body {owner.BodyIndex} {SlotName(owner: owner, slot: group.Slot)}";
    }
    /// <summary>Returns the name of the creation shape a shape instruction draws: the shape whose pool slot its chain's
    /// dynamic transform reads, or the instruction's index where its chain reads none.</summary>
    /// <param name="group">The group the instruction belongs to.</param>
    /// <param name="instruction">The instruction's index in <see cref="Program"/>.</param>
    /// <returns>The name.</returns>
    public string ShapeName(SdfMeshBakeGroup group, int instruction) {
        var instructions = Program.Instructions;

        for (var index = (instruction - 1); (index >= 0); index--) {
            var op = instructions[index].Op;

            if (op == SdfOp.ResetPoint) { break; }
            if (op != SdfOp.TransformDynamic) { continue; }

            var slot = (int)instructions[index].Data0.X;

            return ((BodyOf(slot: slot) is { } body) ? SlotName(owner: body, slot: slot) : $"slot {slot}");
        }

        return $"shape {instruction}";
    }
    /// <summary>Returns the map from a group's frame to world space: its slot's transform for a dynamic group, the
    /// identity for a static one.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The map.</returns>
    public Func<Vector3, Vector3> ToWorld(SdfMeshBakeGroup group) {
        if (!group.Dynamic || (group.Slot >= Transforms.Count)) { return static point => point; }

        var transform = Transforms[group.Slot];
        var turn = ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation);

        return point => (transform.Position + Vector3.Transform(value: point, rotation: turn));
    }

    // The placement and prototype a static instance stands for, as the frame's pick map names it; empty where it names
    // none.
    private string Placed(int instance) =>
        ((m_picks?.Resolve(identity: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: (uint)(instance + 1))) is WorldPickTarget { Placement: { } placement } target)
            ? $" {placement} ({target.Prototype})"
            : string.Empty);
    private RestBody? BodyOf(int slot) => Bodies.FirstOrDefault(predicate: body => ((slot >= body.RootSlot) && (slot < (body.RootSlot + WorldStampPool.SlotsPerPlacement))));
    private static string SlotName(RestBody owner, int slot) {
        var shapes = (owner.Creation.EngineDocument.Shapes ?? []);
        var index = ((slot - owner.RootSlot) - 1);

        return ((index < 0)
            ? "root"
            : ((index < shapes.Count) ? (shapes[index].Name?.Value ?? $"shape {index}") : $"shape {index}"));
    }

    // Keeps the program and transforms of the frame it last dressed.
    private sealed class KeptFrame : ISdfFrameDresser {
        public SdfProgram? Program { get; private set; }
        public DynamicTransform[] Transforms { get; private set; } = [];

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Program = program;
            Transforms = [.. transforms];

            return new SdfFrame(Program: program, ProgramChanged: false, Time: 0f, Views: []) {
                DynamicTransforms = transforms,
                MeshDraws = meshDraws,
                MeshDrawsRevision = meshDrawsRevision,
                MovedTransforms = moved,
            };
        }
    }

    /// <summary>The world around a rest scene, which has none: a server link that sends nothing and is answered nothing,
    /// an audio sink that plays nothing, and no neighbouring worlds, every body solid.</summary>
    public sealed class Inert : IServerLink, IWorldAudioCueSink, IWorldAdjacencySource {
        /// <inheritdoc/>
        public void Query(WorldQuery query, Action<QueryAnswer> completion) { }
        /// <inheritdoc/>
        public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => 0L;
        /// <inheritdoc/>
        public void SubmitIntent(in IntentSubmission submission) { }
        /// <inheritdoc/>
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) { }
        /// <inheritdoc/>
        public void SubmitCue(string eventToken, Vector3? site) { }
        /// <inheritdoc/>
        public void BeginTick(ulong tick) { }
        /// <inheritdoc/>
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        /// <inheritdoc/>
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        /// <inheritdoc/>
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;
            return false;
        }
        /// <inheritdoc/>
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        /// <inheritdoc/>
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;
            return false;
        }
    }
}
