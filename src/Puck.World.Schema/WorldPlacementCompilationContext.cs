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
        private readonly Dictionary<string, WorldPlacement> m_rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorldPrototype> m_creations = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, CompiledPlacementFrame> m_authored;
        private readonly Dictionary<WorldPlacement, CompiledPlacementFrame> m_frames = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldPlacement, CompiledPlacementStamp> m_stamps = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingFrames = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingStamps = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IReadOnlyList<string>, FieldEntry> m_fields = new(TargetComparer.Instance);
        private readonly ulong m_seed;
        private readonly int m_revision;
        public object Gate { get; } = new();
        public IReadOnlyDictionary<string, CompiledPlacementFrame> FrameTable { get; }

        public CompilationContext(WorldPlacementsSection section, IReadOnlyList<WorldPrototype> creations, ulong seed, int revision) {
            m_seed = seed; m_revision = revision;
            m_authored = WorldPlacementFrameCompilation.Resolve(section, seed);
            var projected = false;
            foreach (var row in section.Rows ?? []) {
                if (row is null) { continue; }
                _ = m_rows.TryAdd(row.Id, row); projected |= row.Surface is not null;
            }
            foreach (var creation in creations) { _ = m_creations.TryAdd(creation.Id, creation); }
            FrameTable = projected ? new LazyFrames(this) : m_authored;
        }
        private static bool SingleRoot(WorldPlacement row) => row.Distribution is null && row.Mirror is null && row.Deal is null;
        private bool Owned(WorldPlacement row) => m_rows.TryGetValue(row.Id, out var declared) && ReferenceEquals(row, declared);
        private CompiledPlacementFrame Authored(WorldPlacement row) => Owned(row)
            ? m_authored.GetValueOrDefault(row.Id, CompiledPlacementFrame.FromAuthored(row))
            : CompiledPlacementFrame.FromAuthored(row);
        private CompiledPlacementFrame BaseFrame(WorldPlacement row) {
            if (row.Parent is not { Length: > 0 } parentId || !m_rows.TryGetValue(parentId, out var parent)) { return Authored(row); }
            var parentFrame = FrameOf(parent);
            if (Owned(row) && parentFrame == Authored(parent)) { return Authored(row); }
            return WorldPlacementFrameCompilation.Compose(parentFrame, parent, row, m_seed);
        }
        private CompiledPlacementFrame FrameOf(WorldPlacement row) {
            if (m_frames.TryGetValue(row, out var held)) { return held; }
            if (row.Surface is not null && SingleRoot(row)) {
                // As with ordinary invalid parent chains, frame inspection is best-effort for unvalidated
                // documents. Descriptor validation is the refusing gate; geometry admission never bypasses it.
                if (!TryDescriptor(row, out _, out _)) { return Authored(row); }
                return StampOf(row).Frame;
            }
            if (!m_resolvingFrames.Add(row)) { throw new InvalidOperationException($"placement '{row.Id}' has a cyclic frame dependency."); }
            try {
                var frame = BaseFrame(row);
                m_frames[row] = frame; return frame;
            } finally { m_resolvingFrames.Remove(row); }
        }
        public CompiledPlacementStamp StampOf(WorldPlacement row, CompiledPlacementFrame? frameOverride = null) {
            var owned = Owned(row) && frameOverride is null;
            if (owned && m_stamps.TryGetValue(row, out var retained)) { return retained; }
            if (!m_resolvingStamps.Add(row)) { throw new InvalidOperationException($"placement '{row.Id}' has a cyclic surface dependency."); }
            try {
                var frame = frameOverride ?? BaseFrame(row);
                if (!TryDescriptor(row, out var descriptor, out var reason)) { throw new InvalidOperationException(reason); }
                var entry = Stamps.GetValue(row, static _ => new StampEntry());
                // Resolve dependencies before taking the cross-context row hold. Different contexts cannot form
                // a lock cycle by holding one row while recursively acquiring another.
                if (descriptor is not null) {
                    foreach (var source in descriptor.Sources) { _ = StampOf(source.Row); }
                }
                lock (entry.Gate) {
                    var matching = entry.Frame == frame && entry.Seed == m_seed && SameDependencies(entry.Inputs, descriptor);
                    if (entry.Stamp is { } cached && matching) {
                        if (owned) { m_stamps[row] = cached; m_frames[row] = cached.Frame; }
                        return cached;
                    }
                    var inputs = InputsOf(descriptor);
                    var rotation = frame.FixedRotation;
                    var candidates = new List<FixedCreationStampInstance>();
                    if (row.Deal is null) {
                        CreationStampLattice.ForEachFixedInstance(frame.FixedPosition, rotation, PatternFor(row),
                            SampledFixedOffsetsFor(row, m_seed), MirrorFor(row), candidates.Add);
                    }
                    var instances = candidates.ToArray();
                    int[]? ordinals = null;
                    if (descriptor is not null && row.Deal is null) {
                        Project(row, descriptor.Policy, GroundField(descriptor.Policy, inputs), ref instances, out ordinals);
                        if (instances.Length == 1 && SingleRoot(row)) { frame = frame.WithFixedPosition(instances[0].Origin); }
                    }
                    var stamp = new CompiledPlacementStamp(frame, rotation, instances, ordinals);
                    WorldBootWork.Current.Add(WorldBootWork.StampPayloadBytes,
                        (long)instances.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<FixedCreationStampInstance>() +
                        (long)(ordinals?.Length ?? 0) * sizeof(int));
                    if (CodeRevision.Current == m_revision) {
                        entry.Frame = frameOverride ?? BaseFrame(row); entry.Seed = m_seed; entry.Inputs = inputs; entry.Stamp = stamp;
                    }
                    if (owned) { m_stamps[row] = stamp; m_frames[row] = stamp.Frame; }
                    return stamp;
                }
            } finally { m_resolvingStamps.Remove(row); }
        }
        private bool SameDependencies(GroundInput[] held, SurfaceDescriptor? descriptor) {
            if (held.Length != (descriptor?.Sources.Length ?? 0)) { return false; }
            if (descriptor is null) { return true; }
            for (var i = 0; i < held.Length; i++) {
                var source = descriptor.Sources[i]; var stamp = StampOf(source.Row);
                if (!ReferenceEquals(held[i].Document, source.Document) || !ReferenceEquals(held[i].Stamp, stamp) ||
                    held[i].Scale != stamp.Frame.FixedScale || held[i].Margin != source.Margin) { return false; }
            }
            return true;
        }
        private GroundInput[] InputsOf(SurfaceDescriptor? descriptor) {
            if (descriptor is null) { return []; }
            var inputs = new GroundInput[descriptor.Sources.Length];
            for (var i = 0; i < inputs.Length; i++) {
                var source = descriptor.Sources[i]; var stamp = StampOf(source.Row);
                inputs[i] = new GroundInput(source.Document, stamp, stamp.Frame.FixedScale, source.Margin);
            }
            return inputs;
        }
        private SdfFieldEvaluator GroundField(WorldPlacementSurface surface, GroundInput[] inputs) {
            if (!m_fields.TryGetValue(surface.Targets, out var entry)) { entry = new FieldEntry(); m_fields.Add(surface.Targets, entry); }
            if (entry.Field is { } field && SameInputs(entry.Inputs, inputs)) { return field; }
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
            foreach (var input in inputs) {
                foreach (var instance in input.Stamp.Instances) {
                    CreationStampEmitter.EmitContact(builder, input.Document,
                        new FixedCreationStampTransform(instance.Origin, input.Stamp.FixedRotation, input.Scale, instance.ReflectionNormal),
                        material, input.Margin);
                }
            }
            field = new SdfFieldEvaluator(builder.Build()); entry.Inputs = inputs; entry.Field = field;
            WorldBootWork.Count(WorldBootWork.SurfaceFields); return field;
        }
        private static void Project(WorldPlacement row, WorldPlacementSurface policy, SdfFieldEvaluator field,
            ref FixedCreationStampInstance[] instances, out int[] ordinals) {
            var accepted = new List<FixedCreationStampInstance>(instances.Length);
            var indices = new List<int>(instances.Length);
            var direction = FixedVector3.FromVector3(policy.Direction).Normalize();
            var offset = FixedVector3.FromVector3(policy.FromOffset);
            var reach = FixedQ4816.FromDouble(policy.Reach); var embed = FixedQ4816.FromDouble(policy.Offset);
            var minFacing = FixedQ4816.FromDouble(policy.MinFacing);
            FixedVector3? previousOrigin = null; var passed = false; var root = FixedVector3.Zero;
            for (var i = 0; i < instances.Length; i++) {
                var instance = instances[i]; WorldBootWork.Count(WorldBootWork.SurfaceCandidates);
                if (previousOrigin != instance.Origin) {
                    previousOrigin = instance.Origin; WorldBootWork.Count(WorldBootWork.SurfaceRays);
                    passed = FixedSurfaceQuery.TryRaycast(field, field, instance.Origin + offset, direction, reach,
                        SurfaceNormalBackoff, out var hit, out var sampledNormal, requireExact: true);
                    if (sampledNormal) { WorldBootWork.Count(WorldBootWork.SurfaceNormals); }
                    if (passed && FixedVector3.Dot(hit.Normal, -direction) < minFacing) {
                        WorldBootWork.Count(WorldBootWork.SurfaceSlopeRejected); passed = false;
                    } else if (!passed) { WorldBootWork.Count(WorldBootWork.SurfaceMissed); }
                    if (passed) { root = hit.Point + hit.Normal * embed; }
                }
                if (!passed) {
                    if (policy.RequireHit) { throw new InvalidOperationException($"placement '{row.Id}' surface rejected candidate {i}."); }
                    continue;
                }
                accepted.Add(instance with { Origin = root }); indices.Add(i); WorldBootWork.Count(WorldBootWork.SurfaceAccepted);
            }
            instances = accepted.ToArray(); ordinals = indices.ToArray(); WorldBootWork.Count(WorldBootWork.SurfacePlans);
        }
        private sealed class LazyFrames(CompilationContext owner) : IReadOnlyDictionary<string, CompiledPlacementFrame> {
            public int Count => owner.m_rows.Count;
            public IEnumerable<string> Keys => owner.m_rows.Keys;
            public IEnumerable<CompiledPlacementFrame> Values {
                get { foreach (var row in owner.m_rows.Values) { yield return this[row.Id]; } }
            }
            public CompiledPlacementFrame this[string key] {
                get { lock (owner.Gate) { return owner.FrameOf(owner.m_rows[key]); } }
            }
            public bool ContainsKey(string key) => owner.m_rows.ContainsKey(key);
            public bool TryGetValue(string key, out CompiledPlacementFrame value) {
                lock (owner.Gate) {
                    if (owner.m_rows.TryGetValue(key, out var row)) { value = owner.FrameOf(row); return true; }
                    value = default; return false;
                }
            }
            public IEnumerator<KeyValuePair<string, CompiledPlacementFrame>> GetEnumerator() {
                foreach (var row in owner.m_rows.Values) { yield return new(row.Id, this[row.Id]); }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
