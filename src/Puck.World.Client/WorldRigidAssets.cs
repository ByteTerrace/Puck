using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.Text;
using Puck.SdfVm.Views;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Local creation geometry shared by rigid bodies and admitted static placements. Copies own a root pose and identity, never field instructions.</summary>
public sealed partial class WorldRigidAssets {
    private sealed record AssetMetadata(bool Rigid, float Reach, int Slots);

    private static readonly Puck.Abstractions.CodeDerivedTable<WorldPrototype, AssetMetadata> MetadataCache = new();

    private readonly List<WorldPrototype> m_assets = [];
    private readonly Dictionary<string, int> m_indices = new(comparer: StringComparer.Ordinal);
    private readonly List<int> m_slots = [];
    private readonly List<int> m_offsets = [];
    private readonly List<int> m_materials = [];
    private readonly List<float> m_reaches = [];
    // Templates borrow the builder only during emission; retained instructions and profiles are immutable.
    private readonly Puck.Abstractions.CodeDerivedTable<WorldPrototype, TemplateScratch> m_templates = new();

    private sealed class TemplateScratch {
        public int[] Palette = [];
        public SdfProgramBuilder.Emission? Emission;
        public PackedFontAtlasCatalog? Text;
        public bool[]? Hidden;
        public int Slot;
        public int Surfaces;
        public float BakeScale;

        public readonly WorldPickTarget Target;

        public TemplateScratch(WorldPrototype creation) {
            Target = new WorldPickTarget(BodyIndex: null, Placement: null) { Prototype = creation.Id };
        }
    }

    private int m_slotCount;
    private int m_bodySlotCount;

    private readonly List<SdfVolume> m_volumes = [];
    private readonly List<(int Copy, SdfVolume Volume)> m_rootVolumes = [];
    private int[] m_rootVolumeSlots = [];
    private bool m_volumesDirty = true;
    private bool m_hasBodyVolumes;
    private long m_volumesRevision;
    private long m_poseRevision;
    private readonly List<SdfMeshInstance> m_copies = [];
    private readonly Dictionary<int, WorldPickTarget> m_targets = [];
    private readonly List<WorldCreationInstances.BodyStamp> m_bodies = [];
    private readonly HashSet<int> m_liveBodies = [];
    private readonly Dictionary<int, RootMotion> m_motion = [];

    private WorldShapeIsolation? m_isolation;

    private sealed class RootMotion {
        public SecondOrderFollower3 Position;
        public SecondOrderFollower4 Orientation;
        public WorldEntityAddress Address;

        public int Epoch = -1;

        public int CarryEpoch;

        public readonly WorldStateLease Reads = new();
    }

    /// <summary>Gets the template slots, in the composed program's address space.</summary>
    public IReadOnlyList<int> Slots => m_slots;
    /// <summary>Gets the rigid copies at their current poses.</summary>
    public IReadOnlyList<SdfMeshInstance> Copies => m_copies;
    /// <summary>Gets the number of template slots held by this world's definitions.</summary>
    public int SlotCount => m_slotCount;
    /// <summary>Gets a monotonic revision bumped whenever any rigid copy pose updates or reconciles.</summary>
    public long PoseRevision => m_poseRevision;
    /// <summary>Gets a monotonic revision bumped whenever <see cref="Volumes"/> changes.</summary>
    public long VolumesRevision {
        get {
            EnsureVolumes();

            return m_volumesRevision;
        }
    }
    /// <summary>Gets the live copies' bounded volumes in world space.</summary>
    public IReadOnlyList<SdfVolume> Volumes {
        get {
            EnsureVolumes();

            return m_volumes;
        }
    }

    private void EnsureVolumes() {
        var dirty = m_volumesDirty;

        if (!dirty && (m_rootVolumes.Count > 0)) {
            if (m_rootVolumeSlots.Length != m_rootVolumes.Count) {
                dirty = true;
            } else {
                for (var index = 0; (index < m_rootVolumes.Count); index++) {
                    if (m_copies[m_rootVolumes[index].Copy].FrameSlot != m_rootVolumeSlots[index]) {
                        dirty = true;
                        break;
                    }
                }
            }
        }

        if (!dirty) {
            return;
        }

        m_volumes.Clear();
        m_volumes.AddRange(collection: m_staticVolumes);
        if (m_rootVolumeSlots.Length != m_rootVolumes.Count) {
            m_rootVolumeSlots = ((m_rootVolumes.Count == 0) ? [] : new int[m_rootVolumes.Count]);
        }
        for (var index = 0; (index < m_rootVolumes.Count); index++) {
            var entry = m_rootVolumes[index];
            var slot = m_copies[entry.Copy].FrameSlot;

            m_rootVolumeSlots[index] = slot;
            m_volumes.Add(item: entry.Volume with { DynamicSlot = slot });
        }
        m_volumesDirty = false;
        m_volumesRevision++;
    }

    /// <summary>Returns whether geometry has no independently changing fields or parts: no shape of the rig a look plays
    /// has a writer (<see cref="CreationRig.HasPartMotion"/>), and nothing else changes its fields (a camera, a face,
    /// erosion or a morph).</summary>
    /// <param name="creation">The creation.</param>
    /// <param name="look">The look a body wears it with, whose part followers count as writers, or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the creation is rigid.</returns>
    public static bool IsRigid(WorldPrototype creation, WorldLook? look = null) {
        return (Metadata(creation: creation).Rigid && ((look is null) || !creation.Rig.Follow(partDynamics: look.Motion.PartDynamics).HasPartMotion));
    }

    // Definitions are immutable. Inspect their fields once, rather than once per wearing body per frame.
    private static AssetMetadata Metadata(WorldPrototype creation) => MetadataCache.GetValue(creation, static prototype => {
        var document = prototype.EngineDocument;
        var shapes = (document.Shapes ?? []);
        var rigid = (!prototype.Rig.HasPartMotion && (document.Cameras is not { Count: > 0 }) &&
            (document.Behavior?.Faces is not { Count: > 0 }) &&
            shapes.All(predicate: shape => ((shape.Erode is null) && (shape.Morph is null))));
        var mixed = (shapes.Any(predicate: shape => document.IsGlassShape(shape: shape)) && shapes.Any(predicate: shape => !document.IsGlassShape(shape: shape)));

        return new AssetMetadata(rigid, (rigid ? CreationStampEmitter.RenderReach(document, 1, null, composeBlends: true) : 0), (mixed ? 2 : 1));
    });

    /// <summary>Retains one template per eligible definition and the live body's lightweight occurrences.</summary>
    public void Reconcile(IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldCreationInstances.BodyStamp> bodies, IReadOnlyList<WorldLook>? looks, IReadOnlyList<WorldPlacement> placements) {
        var used = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var body in bodies) { used.Add(item: body.Creation.Id); }
        foreach (var placement in placements) { if (placement.Inhabit is not null) { used.Add(item: placement.PrototypeId); } }
        foreach (var look in (looks ?? [])) {
            if (look.Source is WorldLookSource.Creation creation) { used.Add(item: creation.PrototypeId); }
        }
        m_assets.Clear();
        m_indices.Clear();
        m_offsets.Clear();
        m_reaches.Clear();
        m_slotCount = 0;
        foreach (var creation in creations) {
            if (!used.Contains(item: creation.Id) || !IsRigid(creation)) { continue; }
            m_indices.Add(key: creation.Id, value: m_assets.Count);
            m_assets.Add(item: creation);
            var metadata = Metadata(creation: creation);

            m_reaches.Add(item: metadata.Reach);
            m_offsets.Add(item: m_slotCount);
            m_slotCount += metadata.Slots;
        }
        m_bodies.Clear();
        m_liveBodies.Clear();
        // Every index may name another body now: what is kept by index starts again, and the next pack reads them all.
        m_bodyAt.Clear();
        m_stateWatch.Clear();
        m_wakeAll = true;
        m_copiesRevision++;
        var hasBodyVolumes = false;
        foreach (var body in bodies) {
            if (IsRigid(creation: body.Creation, look: body.Look)) {
                _ = m_bodyAt.TryAdd(key: body.BodyIndex, value: m_bodies.Count);
                m_bodies.Add(item: body);
                m_liveBodies.Add(item: body.BodyIndex);
                if (body.Creation.EngineDocument.Volumes is { Count: > 0 }) {
                    hasBodyVolumes = true;
                }
            }
        }
        m_hasBodyVolumes = hasBodyVolumes;
        m_volumesDirty = true;
        m_poseRevision++;
        foreach (var retired in m_motion.Keys.Where(predicate: body => !ContainsBody(body: body)).ToArray()) { m_motion[retired].Reads.Release(); m_motion.Remove(key: retired); m_targets.Remove(key: retired); }
        // PrepareFrame reads this count before the first pack sizes the root table.
        m_bodySlotCount = m_slotCount;
        var copyCount = (m_bodies.Count + m_staticCopies.Count);
        while (m_copies.Count < copyCount) { m_copies.Add(item: new SdfMeshInstance(0, new object(), new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity), 1, IsDynamic: true)); }
        if (m_copies.Count > copyCount) { m_copies.RemoveRange(copyCount, (m_copies.Count - copyCount)); }
    }
    /// <summary>Emits each local template once. Body membership and styling do not affect this program.</summary>
    public void Emit(SdfProgramBuilder builder, WorldBakedColors colors, int slotBase, PackedFontAtlasCatalog? text, WorldPickMapBuilder picks, WorldShapeIsolation? isolation = null) {

        m_isolation = isolation;
        // The isolation, the materials and the static copies an emit places are read by every copy's row.
        m_wakeAll = true;
        m_copiesRevision++;
        m_slots.Clear();
        m_materials.Clear();
        for (var index = 0; (index < m_assets.Count); index++) {
            var creation = m_assets[index];
            var document = creation.EngineDocument;
            var slot = (slotBase + m_offsets[index]);
            var isolated = isolation?.HiddenFor(creation.Id, document);
            var scratch = m_templates.GetValue(creation, static creation => new TemplateScratch(creation: creation));
            var paletteLength = WorldPlacementStamper.PaletteLength(document: document);

            if (scratch.Palette.Length != paletteLength) { scratch.Palette = new int[paletteLength]; }
            var palette = scratch.Palette;

            WorldPlacementStamper.FillPalette(builder, colors, document, tint: null, ids: palette, pool: (slot, 1f), hidden: isolated);

            m_materials.Add(item: palette[Math.Clamp((creation.Mesh?.Material ?? 0), 0, (palette.Length - 1))]);
            picks.Materials(creation.Id, palette);
            var first = builder.InstanceCount;

            if ((scratch.Emission is { } held) && (scratch.Slot == slot) && ReferenceEquals(objA: scratch.Text, objB: text) && ReferenceEquals(objA: scratch.Hidden, objB: isolated) && builder.CanAppendEmission(emission: held)) {
                builder.AppendEmission(emission: held);
                for (var surface = 0; (surface < scratch.Surfaces); surface++) { m_slots.Add(item: (slot + surface)); }
            } else {
                EmitTemplate(builder: builder, colors: colors, creation: creation, isolated: isolated, scratch: scratch, slot: slot, text: text);
            }
            picks.Instances(first, builder.InstanceCount, scratch.Target);
        }
        EmitStaticAssets(builder: builder, colors: colors, picks: picks, isolation: isolation);
    }

    // Keep the closure-bearing rebuild out of the retained-template path.
    private void EmitTemplate(SdfProgramBuilder builder, WorldBakedColors colors, PackedFontAtlasCatalog? text, TemplateScratch scratch, WorldPrototype creation, int slot, bool[]? isolated) {
        var document = creation.EngineDocument;
        var palette = scratch.Palette;
        var mark = builder.MarkEmission();
        var firstSurface = m_slots.Count;
        var materials = new Dictionary<(int Shape, int Material), int>();

        int MaterialFor(ShapeDocument shape) {
            var key = (shape.Id, (shape.Material ?? 0));

            if (!materials.TryGetValue(key: key, value: out var material)) { material = WorldPlacementStamper.MaterialForShape(builder, palette[Math.Clamp(key.Item2, 0, (palette.Length - 1))], shape, 1, rigid: true); materials.Add(key: key, value: material); }
            return material;
        }
        var shapes = (document.Shapes ?? []);

        if ((shapes.Count == 0) && (document.TextRuns is not { Count: > 0 })) { return; }
        var hasGlass = shapes.Any(predicate: shape => document.IsGlassShape(shape: shape));
        var mixed = (hasGlass && shapes.Any(predicate: shape => !document.IsGlassShape(shape: shape)));

        EmitSurface(slot, hidden: (mixed ? shapes.Select(selector: (shape, index) => (document.IsGlassShape(shape: shape) || (isolated?[index] ?? false))).ToArray() : isolated), textRuns: ((isolated is null) && (!hasGlass || mixed)));
        if (mixed) { EmitSurface((slot + 1), hidden: shapes.Select(selector: (shape, index) => (!document.IsGlassShape(shape: shape) || (isolated?[index] ?? false))).ToArray(), textRuns: false); }
        // A per-shape material registers more palette rows. Such a template stays on the normal emission path;
        // the retained-emission contract deliberately names only materials registered before its mark.
        scratch.Emission = ((builder.MaterialCount == mark.Materials) ? builder.RetainEmission(mark: mark) : null);
        scratch.Slot = slot;
        scratch.Text = text;
        scratch.Hidden = isolated;
        scratch.Surfaces = (m_slots.Count - firstSurface);
        void EmitSurface(int surfaceSlot, bool[]? hidden, bool textRuns) {
            m_slots.Add(item: surfaceSlot);
            WorldPlacementStamper.EmitRootSurface(builder, document, surfaceSlot, hidden, textRuns, text, MaterialFor,
                run => palette[Math.Clamp((run.Material ?? 0), 0, (palette.Length - 1))], glass: (hasGlass && (!mixed || (surfaceSlot != slot))));
        }
    }

    // The awake set, as the stamp pool keeps one (WorldCreationInstances.Awake): a pack reads only the bodies something
    // woke. The invariant: a body's copy row stands until its body's presented inputs move (the source's journal), a
    // state cell its lanes read moves (m_stateWatch), or its root follower is still settling (PackBody keeps it awake);
    // a reconcile, an emit, another slot base, source or door set, or a pack its caller says owes everything reads
    // every body. A source that keeps no journal has every body read every pack.
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_awake = new();
    private bool m_wakeAll = true;
    private int[] m_visits = [];
    private readonly Dictionary<int, int> m_bodyAt = [];
    private readonly WorldBodyChangeCursor m_bodyCursor = new();
    private readonly List<int> m_changedBodies = [];
    private readonly WorldLeaseWatch m_stateWatch = new();
    private readonly List<int> m_changedCopies = [];
    private long m_copiesRevision;
    private IWorldStampSource? m_packedSource;
    private IReadOnlyList<WorldPortalStraddle.Door>? m_packedDoors;
    private int m_packedSlotBase = -1;

    /// <summary>Gets the indices into <see cref="Copies"/> of the body copies whose row the latest <see cref="Pack"/>
    /// rewrote.</summary>
    public IReadOnlyList<int> ChangedCopies => m_changedCopies;
    /// <summary>Gets a counter that moves whenever <see cref="Copies"/> changes other than by a row
    /// <see cref="ChangedCopies"/> names: a reconcile's membership, or the static copies an emit places.</summary>
    public long CopiesRevision => m_copiesRevision;
    /// <summary>Gets the number of bodies the latest <see cref="Pack"/> read: every rigid body on a pack that owes them
    /// all, otherwise only those something woke.</summary>
    public int VisitCount { get; private set; }

    /// <summary>Writes identity template poses and supplies current per-copy roots without touching the program. A pack
    /// reads only the bodies something woke unless it owes them all.</summary>
    /// <param name="transforms">The whole shared dynamic-transform table.</param>
    /// <param name="source">The world the bodies' poses, lanes and doors are read from.</param>
    /// <param name="slotBase">The table slot the first template's range starts at.</param>
    /// <param name="deltaSeconds">The seconds the frame advances each body's root follower by.</param>
    /// <param name="everything">Whether the frame owes the whole table, so every template row and body is written
    /// again.</param>
    public void Pack(Span<DynamicTransform> transforms, IWorldStampSource source, int slotBase, float deltaSeconds = 0, bool everything = true) {
        var doors = WorldPortalStraddle.Doors(definition: source.Definition);
        var whole = (everything || m_wakeAll || (slotBase != m_packedSlotBase) || !ReferenceEquals(objA: source, objB: m_packedSource) || !ReferenceEquals(objA: doors, objB: m_packedDoors));

        m_wakeAll = false;
        m_packedSlotBase = slotBase;
        m_packedSource = source;
        m_packedDoors = doors;
        m_changedCopies.Clear();
        m_changedBodies.Clear();
        if (m_bodyCursor.TryCollect(bodies: m_changedBodies, source: source)) {
            foreach (var body in m_changedBodies) {
                if (m_bodyAt.TryGetValue(key: body, value: out var at)) { _ = m_awake.Add(index: at); }
            }
        } else {
            whole = true;
        }

        whole |= !m_stateWatch.Collect(mirror: source.StateMirror, woken: m_awake);

        var anyMoved = false;

        if (whole) {
            if (m_hasBodyVolumes || (m_rootVolumes.Count > 0)) {
                m_rootVolumes.Clear();
                m_volumesDirty = true;
            }
            PackTemplates(transforms: transforms, slotBase: slotBase);
            m_awake.Clear();
            for (var index = 0; (index < m_bodies.Count); index++) {
                anyMoved |= PackBody(deltaSeconds: deltaSeconds, doors: doors, index: index, slotBase: slotBase, source: source, volumes: true);
            }

            VisitCount = m_bodies.Count;
        } else {
            // A body its pack keeps awake goes into the set again, so the visits are taken first.
            var count = m_awake.Drain(members: ref m_visits);

            foreach (var index in m_visits.AsSpan(start: 0, length: count)) {
                if (index < m_bodies.Count) {
                    anyMoved |= PackBody(deltaSeconds: deltaSeconds, doors: doors, index: index, slotBase: slotBase, source: source, volumes: false);
                }
            }

            VisitCount = count;
        }
        if (anyMoved) {
            m_poseRevision++;
        }
    }
    // Reads one body into its copy's row, rewritten in place. Returns whether the row changed. The root volumes are
    // gathered only on a pack that reads every body, since what they hold moves only with the roster or the isolation.
    private bool PackBody(int index, IWorldStampSource source, int slotBase, float deltaSeconds, IReadOnlyList<WorldPortalStraddle.Door> doors, bool volumes) {
        var body = m_bodies[index];
        var settles = false;

        if (!m_targets.TryGetValue(key: body.BodyIndex, value: out var target) || (target.Prototype != body.Creation.Id)) {
            target = new WorldPickTarget(null, body.BodyIndex) { CreationSlots = 1, Prototype = body.Creation.Id };
            m_targets[body.BodyIndex] = target;
        }
        var position = source.Position(index: body.BodyIndex);
        var orientation = source.Orientation(index: body.BodyIndex);

        if (!m_motion.TryGetValue(key: body.BodyIndex, value: out var motion)) { m_motion.Add(key: body.BodyIndex, value: motion = new()); }
        motion.Reads.Bind(mirror: source.StateMirror, bodyIndex: body.BodyIndex);
        motion.Reads.Arrive(first: body.Creation, second: body.Look);
        var lanes = WorldLookLaneEvaluator.EvaluateLanes(expressions: body.Look.Motion.Lanes, reads: motion.Reads);

        if (WorldDynamicsResponse.TryResolveResponse(rows: source.Definition.Dynamics, name: body.Look.Motion.Dynamics, response: out var response)) {
            var address = source.EntityAddress(index: body.BodyIndex);
            var epoch = source.PoseEpoch(index: body.BodyIndex);
            var crossings = source.Carries.Epoch(index: body.BodyIndex);

            if ((motion.Address == address) && (motion.Epoch == epoch) && (motion.CarryEpoch != crossings)) {
                if (source.Carries.TrySince(index: body.BodyIndex, since: motion.CarryEpoch, carry: out var carry)) {
                    motion.Position.Carry(rotation: carry.Rotation, translation: carry.Translation);
                    motion.Orientation.Carry(rotation: carry.Rotation);
                } else { motion.Epoch = -1; }
            }
            motion.CarryEpoch = crossings;
            if ((motion.Address != address) || (motion.Epoch != epoch)) { motion.Position.Reseed(); motion.Orientation.Reseed(); motion.Address = address; motion.Epoch = epoch; }
            (position, orientation) = SecondOrderPoseFollower.StepPose(deltaSeconds: deltaSeconds, orientation: ref motion.Orientation, position: ref motion.Position, response: in response, targetOrientation: orientation, targetPosition: position);
            settles = true;
        }
        var asset = m_indices[body.Creation.Id];
        var hidden = m_isolation?.HiddenFor(body.Creation.Id, body.Creation.EngineDocument);
        var firstSlot = (slotBase + m_offsets[asset]);
        var endSlot = (slotBase + (((asset + 1) < m_offsets.Count) ? m_offsets[(asset + 1)] : m_bodySlotCount));
        var previous = m_copies[index];
        var addressKey = source.EntityAddress(index: body.BodyIndex);
        var epochKey = source.PoseEpoch(index: body.BodyIndex);
        var motionIdentity = (((previous.MotionIdentity is ValueTuple<WorldPickTarget, WorldEntityAddress, int> key) && ReferenceEquals(objA: key.Item1, objB: target) && (key.Item2 == addressKey) && (key.Item3 == epochKey))
            ? previous.MotionIdentity : (target, addressKey, epochKey));
        var otherSlots = ((endSlot > (firstSlot + 1))
            ? (((previous.OtherAssetSlots.Count == 1) && (previous.OtherAssetSlots[0] == (firstSlot + 1))) ? previous.OtherAssetSlots : new[] { (firstSlot + 1) }) : []);
        var transform = new DynamicTransform(Lanes: lanes, Orientation: orientation, Position: position) { MaterialScale = body.Scale };
        var mesh = (((hidden is null) && (body.Creation.Mesh is { } authored)) ? WorldPlacementStamper.MeshOf(mesh: authored) : null);
        var material = ((asset < m_materials.Count) ? m_materials[asset] : 0);
        var clip = WorldPortalStraddle.Clip(doors: doors, center: position, radius: (m_reaches[asset] * body.Scale), firstSlot: 0, slotCount: 1);

        // The body's row is rewritten in place: a moving body allocates nothing, and its revision tells consumers it moved.
        var previousRevision = previous.Revision;

        _ = previous.Update(firstSlot, target, motionIdentity, transform, body.Scale, true, clip, mesh, material, otherSlots);

        var changed = (previous.Revision != previousRevision);

        if (changed) { m_changedCopies.Add(item: index); }
        m_stateWatch.Index(holder: index, lease: motion.Reads);
        // A root follower is read again while it still moves the row; a frame that advances no time cannot tell.
        if (settles && (changed || (deltaSeconds <= 0f))) { _ = m_awake.Add(index: index); }
        if (volumes && m_hasBodyVolumes) {
            var volumeCount = (body.Creation.EngineDocument.Volumes?.Count ?? 0);

            for (var volumeIndex = 0; (volumeIndex < volumeCount); volumeIndex++) {
                if (!WorldShapeIsolation.TryAdmitVolume(creation: body.Creation.EngineDocument, emitted: m_rootVolumes.Count, hidden: hidden, parentIndex: out var parentIndex, position: volumeIndex, volume: out var volume)) { continue; }

                var parent = ((parentIndex >= 0) ? body.Creation.EngineDocument.Shapes![parentIndex] : null);

                m_rootVolumes.Add(item: (index, volume.ToVolume(dynamicSlot: 0, origin: ((parent?.Position.Value ?? Vector3.Zero) * body.Scale), rotation: (parent?.Rotation.Value ?? Quaternion.Identity), scale: body.Scale)));
            }
        }

        return changed;
    }
    /// <summary>Returns whether this live body is represented by a rigid copy.</summary>
    public bool ContainsBody(int body) => m_liveBodies.Contains(item: body);
    /// <summary>Resolves a rigid body's authored part in its current root pose.</summary>
    public bool TryPartPose(int body, string partId, IWorldStampSource? source, out SdfAnchor pose) {
        if (!TryStampOf(body: body, stamp: out var stamp) || !CreationPartCompiler.Compile(document: stamp.Creation.Document).TryResolve(partId: partId, transformSlot: out var shape)) { pose = default; return false; }
        return TryShapePose(body, stamp.Creation.EngineDocument.Shapes![shape].Id, source, out pose);
    }
    /// <summary>Resolves a rigid shape or root without allocating shape transforms.</summary>
    public bool TryShapePose(int body, int? shapeId, IWorldStampSource? source, out SdfAnchor pose) {
        if (!TryStampOf(body: body, stamp: out var stamp)) { pose = default; return false; }
        var copy = CopyOf(body: body);

        if ((copy is null) && (source is null)) { pose = default; return false; }
        var position = (copy?.Transform.Position ?? source!.Position(index: body));
        var orientation = (copy?.Transform.Orientation ?? source!.Orientation(index: body));
        var shape = ((shapeId is null) ? null : ShapeOf(creation: stamp.Creation.EngineDocument, shapeId: shapeId.Value));

        pose = new SdfAnchor((position + Vector3.Transform(((shape?.Position.Value ?? Vector3.Zero) * stamp.Scale), orientation)), Quaternion.Normalize(value: (orientation * (shape?.Rotation.Value ?? Quaternion.Identity))));
        return true;
    }
    /// <summary>Returns whether a rigid body publishes a part.</summary>
    public bool TryPartSlot(int body, string partId, out int slot) {
        if (TryStampOf(body: body, stamp: out var stamp) && CreationPartCompiler.Compile(document: stamp.Creation.Document).TryResolve(partId: partId, transformSlot: out _)) { return TryRootSlot(body: body, slot: out slot); }
        slot = -1; return false;
    }
    /// <summary>Finds a live copy's packed root slot.</summary>
    public bool TryRootSlot(int body, out int slot) {
        if (CopyOf(body: body) is { } copy) { slot = copy.FrameSlot; return true; }
        slot = -1; return false;
    }

    // The live copy of a body, or null when it has none or no pack has written its row yet.
    private SdfMeshInstance? CopyOf(int body) => ((m_bodyAt.TryGetValue(key: body, value: out var at) && (at < m_copies.Count) &&
        (m_copies[at].Identity is WorldPickTarget { BodyIndex: var found }) && (found == body))
        ? m_copies[at]
        : null);
    private static ShapeDocument? ShapeOf(CreationDocument creation, int shapeId) {
        if (creation.Shapes is not { } shapes) { return null; }
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == shapeId) { return shapes[index]; }
        }

        return null;
    }
    // The body's stamp, or false when the body has none; its creation is never null on a true answer.
    private bool TryStampOf(int body, out WorldCreationInstances.BodyStamp stamp) {
        if (m_bodyAt.TryGetValue(key: body, value: out var at) && (m_bodies[at].Creation is not null)) { stamp = m_bodies[at]; return true; }
        stamp = default; return false;
    }
}
