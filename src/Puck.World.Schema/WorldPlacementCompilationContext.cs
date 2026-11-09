using System.Collections;
using System.Numerics;
using Puck.Abstractions;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

namespace Puck.World;

public static partial class WorldPlacementStamp {
    private sealed partial class CompilationContext {
        private readonly Dictionary<string, WorldPlacement> m_rows = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, WorldPrototype> m_creations = new(comparer: StringComparer.Ordinal);

        private readonly IReadOnlyDictionary<string, CompiledPlacementFrame> m_authored;

        private readonly Dictionary<WorldPlacement, CompiledPlacementFrame> m_frames = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldPlacement, CompiledPlacementStamp> m_stamps = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingFrames = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingStamps = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IReadOnlyList<string>, FieldEntry> m_fields = new(comparer: TargetComparer.Instance);

        private readonly ulong m_seed;
        private readonly int m_revision;

        public object Gate { get; } = new();

        public IReadOnlyDictionary<string, CompiledPlacementFrame> FrameTable { get; }

        public CompilationContext(WorldPlacementsSection section, IReadOnlyList<WorldPrototype> creations, ulong seed, int revision) {
            m_seed = seed; m_revision = revision;
            m_authored = WorldPlacementFrameCompilation.Resolve(section: section, worldSeed: seed);
            var projected = false;

            foreach (var row in (section.Rows ?? [])) {
                if (row is null) { continue; }
                _ = m_rows.TryAdd(key: row.Id, value: row); projected |= (row.Surface is not null);
            }
            foreach (var creation in creations) { _ = m_creations.TryAdd(key: creation.Id, value: creation); }
            FrameTable = (projected ? new LazyFrames(owner: this) : m_authored);
        }

        private static bool SingleRoot(WorldPlacement row) => ((row.Distribution is null) && (row.Mirror is null) && (row.Deal is null));
        private bool Owned(WorldPlacement row) => (m_rows.TryGetValue(key: row.Id, value: out var declared) && ReferenceEquals(objA: row, objB: declared));
        private CompiledPlacementFrame Authored(WorldPlacement row) => (Owned(row: row)
            ? m_authored.GetValueOrDefault(row.Id, WorldPlacementFrameCompilation.LocalFrame(inheritScale: out _, parent: null, row: row, worldSeed: m_seed))
            : WorldPlacementFrameCompilation.LocalFrame(inheritScale: out _, parent: null, row: row, worldSeed: m_seed));
        private CompiledPlacementFrame BaseFrame(WorldPlacement row) {
            if ((row.Parent is not { Length: > 0 } parentId) || !m_rows.TryGetValue(key: parentId, value: out var parent)) { return Authored(row: row); }
            var parentFrame = FrameOf(row: parent);

            if (Owned(row: row) && (parentFrame == Authored(row: parent))) { return Authored(row: row); }
            return WorldPlacementFrameCompilation.Compose(child: row, parent: parent, parentFrame: parentFrame, worldSeed: m_seed);
        }

        public CompiledPlacementFrame FrameOf(WorldPlacement row) {
            if (m_frames.TryGetValue(key: row, value: out var held)) { return held; }
            if ((row.Surface is not null) && SingleRoot(row: row)) {
                // As with ordinary invalid parent chains, frame inspection is best-effort for unvalidated
                // documents. Descriptor validation is the refusing gate; geometry admission never bypasses it.
                if (!TryDescriptor(descriptor: out _, reason: out _, row: row)) { return Authored(row: row); }
                return StampOf(row).Frame;
            }
            if (!m_resolvingFrames.Add(item: row)) { throw new InvalidOperationException(message: $"placement '{row.Id}' has a cyclic frame dependency."); }
            try {
                var frame = BaseFrame(row: row);

                m_frames[row] = frame; return frame;
            } finally { m_resolvingFrames.Remove(item: row); }
        }
        public CompiledPlacementStamp StampOf(WorldPlacement row, CompiledPlacementFrame? frameOverride = null) {
            var owned = (Owned(row: row) && (frameOverride is null));

            if (owned && m_stamps.TryGetValue(key: row, value: out var retained)) { return retained; }
            if (!m_resolvingStamps.Add(item: row)) { throw new InvalidOperationException(message: $"placement '{row.Id}' has a cyclic surface dependency."); }
            try {
                var frame = (frameOverride ?? BaseFrame(row: row));

                if (!TryDescriptor(descriptor: out var descriptor, reason: out var reason, row: row)) { throw new InvalidOperationException(message: reason); }
                var entry = Stamps.GetValue(row, static _ => new StampEntry());
                // Resolve dependencies before taking the cross-context row hold. Different contexts cannot form
                // a lock cycle by holding one row while recursively acquiring another.
                if (descriptor is not null) {
                    foreach (var source in descriptor.Sources) { _ = StampOf(source.Row); }
                }
                lock (entry.Gate) {
                    var matching = ((entry.Frame == frame) && (entry.Seed == m_seed) && SameDependencies(descriptor: descriptor, held: entry.Inputs));

                    if ((entry.Stamp is { } cached) && matching) {
                        if (owned) { m_stamps[row] = cached; m_frames[row] = cached.Frame; }
                        return cached;
                    }
                    var inputs = InputsOf(descriptor: descriptor);
                    var rotation = frame.FixedRotation;
                    var candidates = new List<FixedCreationStampInstance>();

                    if (row.Deal is null) {
                        CreationStampLattice.ForEachFixedInstance(frame.FixedPosition, rotation, PatternFor(placement: row),
                            SampledFixedOffsetsFor(placement: row, worldSeed: m_seed), MirrorFor(placement: row), candidates.Add);
                    }
                    var instances = candidates.ToArray();
                    int[]? ordinals = null;

                    if ((descriptor is not null) && (row.Deal is null)) {
                        Project(row, descriptor.Policy, GroundField(descriptor.Policy, inputs), ref instances, out ordinals);
                        if ((instances.Length == 1) && SingleRoot(row: row)) { frame = frame.WithFixedPosition(position: instances[0].Origin); }
                    }
                    var stamp = new CompiledPlacementStamp(frame: frame, instances: instances, ordinals: ordinals, rotation: rotation);

                    WorldBootWork.Current.Add(WorldBootWork.StampPayloadBytes,
                        ((((long)instances.Length) * System.Runtime.CompilerServices.Unsafe.SizeOf<FixedCreationStampInstance>()) +
                        (((long)(ordinals?.Length ?? 0)) * sizeof(int))));
                    if (CodeRevision.Current == m_revision) {
                        entry.Frame = (frameOverride ?? BaseFrame(row: row)); entry.Seed = m_seed; entry.Inputs = inputs; entry.Stamp = stamp;
                    }
                    if (owned) { m_stamps[row] = stamp; m_frames[row] = stamp.Frame; }
                    return stamp;
                }
            } finally { m_resolvingStamps.Remove(item: row); }
        }

        private bool SameDependencies(GroundInput[] held, SurfaceDescriptor? descriptor) {
            if (held.Length != (descriptor?.Sources.Length ?? 0)) { return false; }
            if (descriptor is null) { return true; }
            for (var i = 0; (i < held.Length); i++) {
                var source = descriptor.Sources[i]; var stamp = StampOf(source.Row);

                if (!ReferenceEquals(objA: held[i].Document, objB: source.Document) || !ReferenceEquals(objA: held[i].Stamp, objB: stamp) ||
                    (held[i].Scale != stamp.Frame.FixedScale) || (held[i].Margin != source.Margin)) { return false; }
            }
            return true;
        }
        private GroundInput[] InputsOf(SurfaceDescriptor? descriptor) {
            if (descriptor is null) { return []; }
            var inputs = new GroundInput[descriptor.Sources.Length];

            for (var i = 0; (i < inputs.Length); i++) {
                var source = descriptor.Sources[i]; var stamp = StampOf(source.Row);

                inputs[i] = new GroundInput(source.Document, stamp, stamp.Frame.FixedScale, source.Margin);
            }
            return inputs;
        }
        private SdfFieldEvaluator GroundField(WorldPlacementSurface surface, GroundInput[] inputs) {
            if (!m_fields.TryGetValue(key: surface.Targets, value: out var entry)) { entry = new FieldEntry(); m_fields.Add(key: surface.Targets, value: entry); }
            if ((entry.Field is { } field) && SameInputs(a: entry.Inputs, b: inputs)) { return field; }
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            foreach (var input in inputs) {
                foreach (var instance in input.Stamp.Instances) {
                    CreationStampEmitter.EmitContact(builder, input.Document,
                        new FixedCreationStampTransform(instance.Origin, input.Stamp.FixedRotation, input.Scale, instance.ReflectionNormal),
                        material, input.Margin);
                }
            }
            field = new SdfFieldEvaluator(program: builder.Build()); entry.Inputs = inputs; entry.Field = field;
            WorldBootWork.Count(kind: WorldBootWork.SurfaceFields); return field;
        }
        private static void Project(WorldPlacement row, WorldPlacementSurface policy, SdfFieldEvaluator field,
            ref FixedCreationStampInstance[] instances, out int[] ordinals) {
            var accepted = new List<FixedCreationStampInstance>(capacity: instances.Length);
            var indices = new List<int>(capacity: instances.Length);
            var direction = FixedVector3.FromVector3(value: policy.Direction).Normalize();
            var offset = FixedVector3.FromVector3(value: policy.FromOffset);
            var reach = FixedQ4816.FromDouble(value: policy.Reach); var embed = FixedQ4816.FromDouble(value: policy.Offset);
            var minFacing = FixedQ4816.FromDouble(value: policy.MinFacing);
            FixedVector3? previousOrigin = null; var passed = false; var root = FixedVector3.Zero;

            for (var i = 0; (i < instances.Length); i++) {
                var instance = instances[i]; WorldBootWork.Count(kind: WorldBootWork.SurfaceCandidates);
                if (previousOrigin != instance.Origin) {
                    previousOrigin = instance.Origin; WorldBootWork.Count(kind: WorldBootWork.SurfaceRays);
                    passed = FixedSurfaceQuery.TryRaycast(field, field, (instance.Origin + offset), direction, reach,
                        SurfaceNormalBackoff, out var hit, out var sampledNormal, requireExact: true);
                    if (sampledNormal) { WorldBootWork.Count(kind: WorldBootWork.SurfaceNormals); }
                    if (passed && (FixedVector3.Dot(left: hit.Normal, right: -direction) < minFacing)) {
                        WorldBootWork.Count(kind: WorldBootWork.SurfaceSlopeRejected); passed = false;
                    } else if (!passed) { WorldBootWork.Count(kind: WorldBootWork.SurfaceMissed); }
                    if (passed) { root = (hit.Point + (hit.Normal * embed)); }
                }
                if (!passed) {
                    if (policy.RequireHit) { throw new InvalidOperationException(message: $"placement '{row.Id}' surface rejected candidate {i}."); }
                    continue;
                }
                accepted.Add(item: instance with { Origin = root }); indices.Add(item: i); WorldBootWork.Count(kind: WorldBootWork.SurfaceAccepted);
            }
            instances = accepted.ToArray(); ordinals = indices.ToArray(); WorldBootWork.Count(kind: WorldBootWork.SurfacePlans);
        }

        private sealed class LazyFrames(CompilationContext owner) : IReadOnlyDictionary<string, CompiledPlacementFrame> {
            public int Count => owner.m_rows.Count;
            public IEnumerable<string> Keys => owner.m_rows.Keys;
            public IEnumerable<CompiledPlacementFrame> Values {
                get { foreach (var row in owner.m_rows.Values) { yield return this[row.Id]; } }
            }

            public CompiledPlacementFrame this[string key] {
                get { lock (owner.Gate) { return owner.FrameOf(row: owner.m_rows[key]); } }
            }

            public bool ContainsKey(string key) => owner.m_rows.ContainsKey(key: key);
            public bool TryGetValue(string key, out CompiledPlacementFrame value) {
                lock (owner.Gate) {
                    if (owner.m_rows.TryGetValue(key: key, value: out var row)) { value = owner.FrameOf(row: row); return true; }
                    value = default; return false;
                }
            }
            public IEnumerator<KeyValuePair<string, CompiledPlacementFrame>> GetEnumerator() {
                foreach (var row in owner.m_rows.Values) { yield return new(key: row.Id, value: this[row.Id]); }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
