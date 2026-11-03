using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.Text;
using Puck.SdfVm.Views;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Local creation geometry shared by rigid bodies. Copies own a root pose and identity, never field instructions.</summary>
public sealed class WorldRigidAssets {
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

        public readonly WorldPickTarget Target;

        public TemplateScratch(WorldPrototype creation) {
            Target = new WorldPickTarget(BodyIndex: null, Placement: null) { Prototype = creation.Id };
        }
    }

    private int m_slotCount;

    private readonly List<SdfVolume> m_volumes = [];
    private readonly List<(int Copy, SdfVolume Volume)> m_rootVolumes = [];
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
    /// <summary>Gets the live copies' bounded volumes in world space.</summary>
    public IReadOnlyList<SdfVolume> Volumes {
        get {
            m_volumes.Clear();
            foreach (var entry in m_rootVolumes) { m_volumes.Add(item: entry.Volume with { DynamicSlot = m_copies[entry.Copy].FrameSlot }); }
            return m_volumes;
        }
    }

    /// <summary>Returns whether geometry has no independently changing fields or parts.</summary>
    public static bool IsRigid(WorldPrototype creation, WorldLook? look = null) {
        return (Metadata(creation: creation).Rigid && ((look is null) || (look.Motion.PartDynamics is not { Count: > 0 })));
    }

    // Definitions are immutable. Inspect their fields once, rather than once per wearing body per frame.
    private static AssetMetadata Metadata(WorldPrototype creation) => MetadataCache.GetValue(creation, static prototype => {
        var document = prototype.EngineDocument;
        var shapes = (document.Shapes ?? []);
        var rigid = ((document.Frames is not { Count: > 0 }) && (document.Drivers is not { Count: > 0 }) &&
            (document.Effectors is not { Count: > 0 }) && (document.Cameras is not { Count: > 0 }) &&
            (document.Behavior?.Faces is not { Count: > 0 }) &&
            shapes.All(predicate: shape => ((shape.Swings is not { Count: > 0 }) && (shape.Slides is not { Count: > 0 }) && (shape.Erode is null) && (shape.Morph is null))));
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
        foreach (var body in bodies) {
            if (IsRigid(creation: body.Creation, look: body.Look)) { m_bodies.Add(item: body); m_liveBodies.Add(item: body.BodyIndex); }
        }
        foreach (var retired in m_motion.Keys.Where(predicate: body => !ContainsBody(body: body)).ToArray()) { m_motion[retired].Reads.Release(); m_motion.Remove(key: retired); m_targets.Remove(key: retired); }
        // PrepareFrame reads this count before the first pack sizes the root table.
        while (m_copies.Count < m_bodies.Count) { m_copies.Add(item: new SdfMeshInstance(0, new object(), new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity), 1, IsDynamic: true)); }
        if (m_copies.Count > m_bodies.Count) { m_copies.RemoveRange(m_bodies.Count, (m_copies.Count - m_bodies.Count)); }
    }
    /// <summary>Emits each local template once. Body membership and styling do not affect this program.</summary>
    public void Emit(SdfProgramBuilder builder, WorldBakedColors colors, int slotBase, PackedFontAtlasCatalog? text, WorldPickMapBuilder picks, WorldShapeIsolation? isolation = null) {

        m_isolation = isolation;
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
            builder.BeginInstanceDynamic(surfaceSlot, Vector3.Zero, (CreationStampEmitter.RenderReach(document, 1, ((text is null) ? null : text.Resolve), composeBlends: true) + 0.4f));
            var scoped = CreationStampEmitter.ComposesInternally(document: document);

            if (scoped) { builder.PushField(SdfBlendOp.Union); }
            var transform = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1, null, DynamicSlot: surfaceSlot);

            CreationStampEmitter.Emit(builder, document, transform, MaterialFor, inScope: scoped, hidden: hidden);
            if (document.Noise is { } noise) { CreationStampEmitter.EmitNoise(builder: builder, noise: noise, transform: transform); }
            if (textRuns && (text is not null) && (document.TextRuns is { Count: > 0 })) {
                CreationStampEmitter.EmitText(builder, document, transform, text!.Resolve, run => palette[Math.Clamp((run.Material ?? 0), 0, (palette.Length - 1))]);
            }
            if (scoped) { builder.PopField(); }
            builder.EndInstance();
        }
    }

    /// <summary>Writes identity template poses and supplies current per-copy roots without touching the program.</summary>
    public void Pack(Span<DynamicTransform> transforms, IWorldStampSource source, int slotBase, float deltaSeconds = 0) {
        var doors = WorldPortalStraddle.Doors(definition: source.Definition);

        m_rootVolumes.Clear();
        for (var index = 0; (index < m_slotCount); index++) { transforms[(slotBase + index)] = new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity); }
        for (var index = 0; (index < m_bodies.Count); index++) {
            var body = m_bodies[index];

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
            }
            var asset = m_indices[body.Creation.Id];
            var hidden = m_isolation?.HiddenFor(body.Creation.Id, body.Creation.EngineDocument);
            var firstSlot = (slotBase + m_offsets[asset]);
            var endSlot = (slotBase + (((asset + 1) < m_offsets.Count) ? m_offsets[(asset + 1)] : m_slotCount));
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

            if (!previous.SamePose(firstSlot, target, motionIdentity, transform, body.Scale, true, clip, mesh, material, otherSlots)) {
                m_copies[index] = new SdfMeshInstance(firstSlot, target, transform, body.Scale, IsDynamic: true) { Clip = clip, Material = material, Mesh = mesh, MotionIdentity = motionIdentity, OtherAssetSlots = otherSlots };
            }
            foreach (var volume in (body.Creation.EngineDocument.Volumes ?? [])) {
                if (!volume.Enabled) { continue; }
                if ((volume.Parent is { } parentName) && WorldShapeIsolation.IsHidden(hidden: hidden, name: parentName, shapes: (body.Creation.EngineDocument.Shapes ?? []))) { continue; }
                if (m_rootVolumes.Count == SdfProgramBuilder.MaxVolumes) { throw new InvalidOperationException(message: $"Creation '{body.Creation.Id}' on body {body.BodyIndex} exceeds the {SdfProgramBuilder.MaxVolumes}-volume frame capacity."); }
                var parent = body.Creation.EngineDocument.Shapes?.FirstOrDefault(predicate: shape => (shape.Name?.Value == volume.Parent));

                m_rootVolumes.Add(item: (index, volume.ToVolume(dynamicSlot: 0, origin: ((parent?.Position.Value ?? Vector3.Zero) * body.Scale), rotation: (parent?.Rotation.Value ?? Quaternion.Identity), scale: body.Scale)));
            }
        }
    }
    /// <summary>Returns whether this live body is represented by a rigid copy.</summary>
    public bool ContainsBody(int body) => m_liveBodies.Contains(item: body);
    /// <summary>Resolves a rigid body's authored part in its current root pose.</summary>
    public bool TryPartPose(int body, string partId, IWorldStampSource? source, out SdfAnchor pose) {
        var stamp = m_bodies.FirstOrDefault(predicate: stamp => (stamp.BodyIndex == body));

        if ((stamp.Creation is null) || !CreationPartCompiler.Compile(document: stamp.Creation.Document).TryResolve(partId: partId, transformSlot: out var shape)) { pose = default; return false; }
        return TryShapePose(body, stamp.Creation.EngineDocument.Shapes![shape].Id, source, out pose);
    }
    /// <summary>Resolves a rigid shape or root without allocating shape transforms.</summary>
    public bool TryShapePose(int body, int? shapeId, IWorldStampSource? source, out SdfAnchor pose) {
        var stamp = m_bodies.FirstOrDefault(predicate: stamp => (stamp.BodyIndex == body));

        if (stamp.Creation is null) { pose = default; return false; }
        var copy = m_copies.FirstOrDefault(predicate: copy => ((copy.Identity is WorldPickTarget { BodyIndex: var index }) && (index == body)));

        if ((copy is null) && (source is null)) { pose = default; return false; }
        var position = (copy?.Transform.Position ?? source!.Position(index: body));
        var orientation = (copy?.Transform.Orientation ?? source!.Orientation(index: body));
        var shape = ((shapeId is null) ? null : stamp.Creation.EngineDocument.Shapes?.FirstOrDefault(predicate: shape => (shape.Id == shapeId)));

        pose = new SdfAnchor((position + Vector3.Transform(((shape?.Position.Value ?? Vector3.Zero) * stamp.Scale), orientation)), Quaternion.Normalize(value: (orientation * (shape?.Rotation.Value ?? Quaternion.Identity))));
        return true;
    }
    /// <summary>Returns whether a rigid body publishes a part.</summary>
    public bool TryPartSlot(int body, string partId, out int slot) {
        var stamp = m_bodies.FirstOrDefault(predicate: stamp => (stamp.BodyIndex == body));

        if ((stamp.Creation is not null) && CreationPartCompiler.Compile(document: stamp.Creation.Document).TryResolve(partId: partId, transformSlot: out _)) { return TryRootSlot(body: body, slot: out slot); }
        slot = -1; return false;
    }
    /// <summary>Finds a live copy's packed root slot.</summary>
    public bool TryRootSlot(int body, out int slot) {
        for (var index = 0; (index < m_copies.Count); index++) {
            if ((m_copies[index].Identity is WorldPickTarget { BodyIndex: var found }) && (found == body)) { slot = m_copies[index].FrameSlot; return true; }
        }
        slot = -1; return false;
    }
}
