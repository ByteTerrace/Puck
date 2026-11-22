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
        private readonly IReadOnlyDictionary<string, WorldPlacement> m_rows;
        // The creations the context was compiled under, found through the list's shared key index.
        private readonly IReadOnlyList<WorldPrototype> m_creations;

        // The authored world frames resolved so far, each on its first read with the parents it composes over
        // (WorldPlacementFrameCompilation.TryResolveFrame, which carries a row's frame by its identity across
        // sections), so a context built for a few rows' frames holds and allocates those rows' alone.
        private readonly Dictionary<string, CompiledPlacementFrame> m_authored;
        private readonly HashSet<string> m_authoring = new(comparer: StringComparer.Ordinal);

        private readonly Dictionary<WorldPlacement, CompiledPlacementFrame> m_frames = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldPlacement, CompiledPlacementStamp> m_stamps = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingFrames = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_resolvingStamps = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IReadOnlyList<string>, FieldEntry> m_fields = new(comparer: SequenceEqualityComparer<string>.Instance);

        private readonly ulong m_seed;
        private readonly int m_revision;

        public object Gate { get; } = new();

        public IReadOnlyDictionary<string, CompiledPlacementFrame> FrameTable { get; }

        public CompilationContext(WorldPlacementsSection section, IReadOnlyList<WorldPrototype> creations, ulong seed, int revision) {
            m_seed = seed; m_revision = revision;
            m_authored = new(comparer: StringComparer.Ordinal);
            m_rows = WorldPlacementIndex.ById(placements: (section.Rows ?? []));
            m_creations = creations;
            FrameTable = new LazyFrames(owner: this);
        }
        // A successor of the previous context. Surface descriptors and parent validation read only the rows' literal
        // structure (a surface row, its targets and its parents are literal static roots), never a holding mask or a
        // bound value, so they always carry. A holding-only replacement also carries frames and stamps, each to the row
        // that replaced its row; a move of the section's bound values (moved) resolves the frames again and derives
        // each stamp again through the per-row stamp table, which keeps a stamp whose frame did not move. Ground fields
        // are not carried: they are only read when a stamp derives again, and an evaluator is not shared between two
        // contexts' gates.
        private CompilationContext(CompilationContext previous, WorldPlacementsSection section, IReadOnlyDictionary<WorldPlacement, WorldPlacement> replaced, bool moved) {
            m_seed = previous.m_seed; m_revision = previous.m_revision;
            // A holding mask moves no frame, so a holding-only successor starts from the frames resolved before.
            m_authored = (moved ? new(comparer: StringComparer.Ordinal) : new(previous.m_authored, comparer: StringComparer.Ordinal));
            m_rows = WorldPlacementIndex.ById(placements: (section.Rows ?? []));
            m_creations = previous.m_creations;
            FrameTable = new LazyFrames(owner: this);

            WorldPlacement Row(WorldPlacement row) => replaced.GetValueOrDefault(key: row, defaultValue: row);
            RowRead[] Reads(RowRead[] held) {
                foreach (var read in held) {
                    if (!read.Creation && (read.Row is WorldPlacement row) && replaced.ContainsKey(key: row)) {
                        return [.. held.Select(each => (!each.Creation && (each.Row is WorldPlacement kept)) ? each with { Row = Row(row: kept) } : each)];
                    }
                }
                return held;
            }
            SurfaceValidation Validation(SurfaceValidation held) {
                var reads = Reads(held: held.Reads);
                var descriptor = held.Descriptor;

                if (descriptor is not null) {
                    foreach (var source in descriptor.Sources) {
                        if (replaced.ContainsKey(key: source.Row)) {
                            descriptor = descriptor with { Sources = [.. descriptor.Sources.Select(each => each with { Row = Row(row: each.Row) })] };
                            break;
                        }
                    }
                }
                return ((ReferenceEquals(objA: reads, objB: held.Reads) && ReferenceEquals(objA: descriptor, objB: held.Descriptor)) ? held : new SurfaceValidation(descriptor, held.Reason, reads));
            }

            if (!moved) {
                foreach (var (row, frame) in previous.m_frames) { m_frames[Row(row: row)] = frame; }
                foreach (var (row, stamp) in previous.m_stamps) { m_stamps[Row(row: row)] = stamp; }
            }
            foreach (var (row, validation) in previous.m_descriptors) { m_descriptors[Row(row: row)] = Validation(held: validation); }
            foreach (var (row, parent) in previous.m_parentValidation) {
                var reads = Reads(held: parent.Reads);

                m_parentValidation[Row(row: row)] = (ReferenceEquals(objA: reads, objB: parent.Reads) ? parent : new ParentValidation(parent.Reason, reads));
            }
        }
        // The context for a holding-only replacement of this context's section; see the constructor.
        public CompilationContext Rebase(WorldPlacementsSection section, IReadOnlyDictionary<WorldPlacement, WorldPlacement> replaced) {
            lock (Gate) { return new CompilationContext(moved: false, previous: this, replaced: replaced, section: section); }
        }
        // The context for this context's own section once the bound values its frames read have moved.
        public CompilationContext Moved(WorldPlacementsSection section) {
            lock (Gate) { return new CompilationContext(moved: true, previous: this, replaced: NoReplacements, section: section); }
        }
        private static readonly IReadOnlyDictionary<WorldPlacement, WorldPlacement> NoReplacements = new Dictionary<WorldPlacement, WorldPlacement>();
        public int Revision => m_revision;
        public IReadOnlyList<WorldPrototype> Creations => m_creations;
        public ulong Seed => m_seed;
        private WorldPrototype? CreationOf(string id) => WorldDefinitionRows.FindCreation(creations: m_creations, id: id);
        private static bool SingleRoot(WorldPlacement row) => row.Distribution is null && row.Mirror is null && row.Deal is null;
        private bool Owned(WorldPlacement row) => m_rows.TryGetValue(row.Id, out var declared) && ReferenceEquals(row, declared);
        // A declared row's authored world frame; a row whose parent chain does not resolve keeps its own authored root,
        // as frame compilation gives it.
        private CompiledPlacementFrame Authored(WorldPlacement row) {
            if (!Owned(row: row)) { return WorldPlacementFrameCompilation.LocalFrame(inheritScale: out _, parent: null, row: row, worldSeed: m_seed); }
            if (m_authored.TryGetValue(key: row.Id, value: out var frame)) { return frame; }
            if (!WorldPlacementFrameCompilation.TryResolveFrame(byId: m_rows, frame: out frame, frames: m_authored, placement: row, reason: out _, visiting: m_authoring, worldSeed: m_seed)) {
                frame = WorldPlacementFrameCompilation.LocalFrame(inheritScale: out _, parent: null, row: row, worldSeed: m_seed);
                m_authored[row.Id] = frame;
            }
            return frame;
        }
        private CompiledPlacementFrame BaseFrame(WorldPlacement row) {
            if (row.Parent is not { Length: > 0 } parentId || !m_rows.TryGetValue(parentId, out var parent)) { return Authored(row); }
            var parentFrame = FrameOf(parent);
            if (Owned(row) && parentFrame == Authored(parent)) { return Authored(row); }
            return WorldPlacementFrameCompilation.Compose(parentFrame, parent, row, m_seed);
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

                if (Owned(row: row)) { m_frames[row] = frame; }
                return frame;
            } finally { m_resolvingFrames.Remove(item: row); }
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
                get { lock (owner.Gate) { return owner.FrameOf(row: owner.m_rows[key]); } }
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
