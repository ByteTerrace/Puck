using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Puck.Abstractions;
using Puck.SignedDistance;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    private sealed record PackedField(UInt128 Hash);
    private static readonly Puck.Abstractions.CodeDerivedTable<SdfProgram, PackedField> PackedFields = new();

    // The key a group's stage checkpoints (its contour and simplified meshes) are kept under: its sealed geometry, tier
    // included, with the numeric palette and poses (GroupInput). The drawn product can use the group's canonical geometry.
    private static SdfMeshGeometry CheckpointInput(Group group, SdfProgram program) => GroupInput(group, program, geometry: group.Sealed.Hash);

    // What a group's samples depend on beside each lattice's own row: its shape (SdfMeshBakeGroup.Shape, its geometry
    // before the tier and the operands' poses join it) and what GroupInput adds, whose pose rows hold every slot pose the
    // kernel reads, a skin member's and an operand's included. Everything a tier changes in what a lattice samples reaches
    // the kernel through those rows (an operand's pose snapped to the tier's cell) or the lattice's row (its spacing, a
    // glyph's cell), so a detail tier takes its floor's coarse search and final slabs from the store when they sample
    // alike, and no sample set of another field is ever taken.
    private static SdfMeshGeometry SampleFieldInput(Group group, SdfProgram program) => GroupInput(group, program, geometry: group.Shape);

    // A group's geometry hash with what the bake reads beside it: the program's packed field and step scale, the
    // group's numeric palette and the pose rows its lattices read.
    private static SdfMeshGeometry GroupInput(Group group, SdfProgram program, UInt128 geometry) {
        var hash = new XxHash128();
        var packed = PackedFields.GetValue(program, static field =>
            new PackedField(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(field.FieldWords))));
        Span<byte> header = stackalloc byte[48];
        BinaryPrimitives.WriteUInt128LittleEndian(header, geometry);
        BinaryPrimitives.WriteUInt128LittleEndian(header[16..], packed.Hash);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], group.MaterialOrder.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], group.PoseRows.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], group.PoseFirst);
        BinaryPrimitives.WriteSingleLittleEndian(header[44..], program.StepScale);
        hash.Append(header);
        hash.Append(MemoryMarshal.AsBytes(group.MaterialOrder.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(group.PoseRows.AsSpan()));
        return new SdfMeshGeometry(Dynamic: group.Dynamic, Hash: hash.GetCurrentHashAsUInt128());
    }

    /// <summary>Where a group's dispatched lattices keep their samples and what their key reads beside each lattice's own
    /// row and points: the store and sampling identity the group checkpoints to, its sample input
    /// (<c>SampleFieldInput</c>'s hash) and the first slot and row count of the pose rows its dispatch packs. Part of the
    /// group's <see cref="CheckpointKeys"/>; immutable, and holding no group, so a reader that keeps a lattice's samples
    /// after the take reads nothing the bake moves on.</summary>
    internal sealed record SampleKeys(SdfMeshStore Store, UInt128 Sampling, UInt128 Geometry, int PoseFirst, int PoseRowCount);

    /// <summary>Everything a group's checkpoints are kept and found under, taken from one program: the bake identity whose
    /// stage keys name its contour and simplified meshes, the program the keys hash (<c>GroupInput</c> reads its packed
    /// field), the geometry key those meshes are kept under and the keys its sample sets are kept under. Immutable and
    /// replaced whole (<c>KeyCheckpoints</c>), so a worker reading a group's keys off the gate reads one consistent set,
    /// never a program from one and a geometry key from another.</summary>
    internal sealed record CheckpointKeys(SdfMeshBakeIdentity Identity, SdfProgram Program, SdfMeshGeometry Geometry, SampleKeys Samples);

    // Under the gate: keys a group's checkpoints under the bake's identity and the program it meshes, kept in a store. Begin
    // keys a group as it begins; a group a new program keeps mid-bake (Place) is keyed again under that program, so its
    // later stages and lattices are kept where a later bake of that program finds them.
    private void KeyCheckpoints(Group group, SdfMeshStore store) {
        var program = m_program!;

        group.Checkpoints = new CheckpointKeys(
            Geometry: CheckpointInput(group, program),
            Identity: Identity,
            Program: program,
            Samples: new SampleKeys(Store: store, Sampling: Identity.Sampling, Geometry: SampleFieldInput(group, program).Hash, PoseFirst: group.PoseFirst, PoseRowCount: group.PoseRows.Length)
        );
    }

    // The one key a lattice's samples are kept and probed by: a probe (LoadSamples) and a write (Receive's KeepSamples)
    // both call it with the keys the take recorded for the request, so a hit restores the samples the same request
    // dispatched. It reads the request's points: its caller holds the request (Complete and ProbeSamples state who), and
    // it is never called under the bake's gate, since a request holds up to LatticeTaps points.
    private static UInt128 SampleInput(SampleKeys keys, in SdfMeshBakeRequest request) {
        var hash = new XxHash128();
        Span<byte> geometry = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(destination: geometry, value: keys.Geometry);
        hash.Append(geometry);
        Span<byte> row = stackalloc byte[SdfMeshBakeBlock.LatticeBytes];
        // Use the same packer that writes the dispatch. Offsets into a batch are rebased to the start of this request.
        SdfMeshBakeBlock.WriteLattice(row, in request, first: 0, poseBase: 0, poseFirst: keys.PoseFirst, poseRowCount: keys.PoseRowCount);
        hash.Append(row);
        hash.Append(MemoryMarshal.AsBytes(request.Points.Span));
        return hash.GetCurrentHashAsUInt128();
    }

    // Under the gate, from the take: the keys the group's next lattice keeps its samples under, or null when the group
    // keeps none. The take records them beside the lattice (SdfMeshBakeBatch.Keeps), so whether and where its samples are
    // kept is decided once, with the dispatch, never by the reader from a program or identity bound since.
    private SampleKeys? SampleKeysOf(Group group) => CurrentCheckpoints(group)?.Samples;

    // A group's checkpoint keys while they key what the bake now meshes: taken under the bake's current code and identity,
    // from the program it meshes; null otherwise, and for a group that keeps none. Read once by each caller, off the gate
    // or under it: the keys are one immutable set (CheckpointKeys).
    private CheckpointKeys? CurrentCheckpoints(Group group) =>
        ((group.Checkpoints is { } keys) && CodeCurrent(keys.Identity) && ReferenceEquals(keys.Program, m_program) ? keys : null);

    private bool StageCodeCurrent(Group group) => (group.Checkpoints is not { } keys) || CodeCurrent(keys.Identity);

    private bool CodeCurrent(SdfMeshBakeIdentity identity) => ((identity.CodeRevision == CodeRevision.Current) && (identity.Value == Identity.Value));

    // Whether the store may hold a checkpoint of the group: from its known entries, never a file read, since the start of a
    // group runs on the frame's thread under the gate. A checkpoint the answer misses is baked again; one it names that is
    // gone fails its load, which falls back to the bake.
    private bool MayHoldCheckpoint(Group group) {
        if (CurrentCheckpoints(group) is not { } keys) { return false; }
        var store = keys.Samples.Store;
        return store.MayHoldCheckpoint(keys.Identity.Simplification, keys.Geometry)
            || store.MayHoldCheckpoint(keys.Identity.Contouring, keys.Geometry);
    }

    // The latest checkpoint that survives is the earliest work the group still owes.
    private bool ResumeCheckpoint(Group group) {
        if (CurrentCheckpoints(group) is not { Samples.Store: var store } current) { return false; }
        var keys = current.Identity;

        if (store.TryReadCheckpoint(keys.Simplification, current.Geometry, out var simplified)) {
            using (EnterGate(site: BakeGate.Checkpoint)) { m_accounts.SimplifyHits++; }
            if (simplified is not null) { group.DiagnosticCell = simplified.BakeCell; }
            PublishMesh(group, simplified, merged: 0);
            return true;
        }
        var found = store.TryReadCheckpoint(keys.Contouring, current.Geometry, out var contour);
        using (EnterGate(site: BakeGate.Checkpoint)) {
            group.CheckedCheckpoints = true;
            m_accounts.SimplifyMisses++;
            if (found) { m_accounts.ContourHits++; } else { m_accounts.ContourMisses++; }
        }
        if (!found) { return false; }

        if (contour is not null) { group.DiagnosticCell = contour.BakeCell; }
        else { group.FoundNoSurface(how: ", as stored before simplification"); }
        Land(group, contour);
        return true;
    }

    private void KeepCheckpoint(Group group, SdfMesh? mesh, bool simplified) {
        if (CurrentCheckpoints(group) is not { } keys) { return; }
        keys.Samples.Store.WriteCheckpoint(simplified ? keys.Identity.Simplification : keys.Identity.Contouring, keys.Geometry, mesh);
    }

    private void StoreEmpty(Group group) {
        KeepCheckpoint(group, mesh: null, simplified: false);
        if (StageCodeCurrent(group)) { Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true); }
    }

    // The simplifier consumes all shape inputs through this call. The contour closure stops here, and the simplifier
    // closure starts here. Calling another simplification routine upstream makes it part of the contour closure.
    internal static class SimplificationStage {
        internal static SdfMesh Run(SdfMesh mesh, bool dynamic, bool glass, int instances, bool preserveFeatures) {
            if (!dynamic && !glass && ((instances > 1) || (mesh.TriangleCount >= SdfMeshChunks.WholeTriangles))) {
                return SdfMeshChunks.Bake(mesh, preserveFeatures);
            }
            var merged = SdfMeshPlanarMerge.Merge(mesh);
            return glass ? merged : SdfMeshCollapse.Levels(merged, preserveFeatures);
        }
    }

    // Whether a take hands a group's next lattice to the store instead of dispatching it; under the gate, from the take,
    // which dispatches the lattice when this answers false. A group owns one request at a time. Only identity-sized work
    // runs here, since a request holds up to LatticeTaps points: a lattice whose sampling identity the store is known to
    // hold no set of (SdfMeshStore.MayHoldSamples, no file read; on a cold store, every lattice) dispatches in this take.
    // Any other is probed on the bake's pool ahead of every queued stage, an answer's place (LoadSamples), which hashes
    // its points; a miss there hands it back to the next take, which dispatches it unprobed. A probe is never a stage
    // (Schedule), so a group's lead (SdfMeshBakeBacklog.ObserveLead) runs from its admission to its first real stage. An
    // answered probe never consumes GPU dispatch budget.
    private bool ProbeSamples(Group group, in SdfMeshBakeRequest request, SampleKeys? keys) {
        if (keys is null) { return false; }
        if (!group.CheckedCheckpoints) {
            group.CheckedCheckpoints = true;
            m_accounts.SimplifyMisses++;
            m_accounts.ContourMisses++;
        }

        var missed = group.ProbeMissed;

        group.ProbeMissed = false;
        if (missed || !keys.Store.MayHoldSamples(keys.Sampling)) {
            m_accounts.SampleMisses++;
            return false;
        }
        // A retired group takes no lattice: its bake no longer plans it.
        if (!group.QueueWork()) { return true; }

        // The probe holds the group until LoadSamples hands it on, so no stage runs and nothing rewrites the request's
        // points (the group's workspace) while the probe hashes them: the request belongs to its holder, as a
        // dispatched one belongs to its ticket's (Complete).
        group.Contouring = true;
        QueueSampleLoad(group, request, keys);
        return true;
    }
    // Its own method, so a probe that returns early above allocates no closure.
    // The load is granted the lattice's output bytes of readback, as a dispatch of it would be (ReadbackBytes), which its
    // read holds until it ends.
    private void QueueSampleLoad(Group group, in SdfMeshBakeRequest request, SampleKeys keys) {
        var pending = request;

        GrantReadback(bytes: request.OutputBytes);
        Enqueue(group: group, urgent: true, work: () => LoadSamples(group, pending, keys));
    }

    // Keys a probed lattice's samples, reads them and answers the group with them, or hands the lattice back to the next
    // take, which dispatches it (ProbeMissed, which that take counts as the miss). On the pool, holding the group
    // (ProbeSamples).
    private void LoadSamples(Group group, SdfMeshBakeRequest request, SampleKeys keys) {
        var length = request.OutputBytes;
        // Charged to the bake's readback owner within the bytes QueueSampleLoad granted, which its release gives back.
        var samples = new AnsweredSamples(bake: this, granted: length);

        try {
            if (group.Retired) { return; }

            var key = SampleInput(keys, in request);

            if (keys.Store.MayHoldSamples(keys.Sampling, key) && keys.Store.TryReadSamples(keys.Sampling, key, length, samples.Storage)) {
                using var answer = Decode(group, in request, samples, offset: 0);
                using (EnterGate(site: BakeGate.Checkpoint)) {
                    if (!GroupList.Holds(group: group)) { return; }
                    group.Contouring = false;
                    group.InFlight = true;
                    group.Next = null;
                    m_accounts.SampleHits++;
                }
                AnswerGroup(group, in request, answer);
                return;
            }

            using (EnterGate(site: BakeGate.Checkpoint)) {
                group.Contouring = false;
                group.ProbeMissed = true;
            }
        } finally {
            samples.Release();
        }
    }

    // Keeps a dispatched lattice's samples for a later bake under the keys its take recorded, copied now and written on
    // the store's writer; the answer does not wait for the write. The key is taken here, from the request's points, which
    // are its group's workspace: its caller holds the request (Receive).
    private static void KeepSamples(SampleKeys keys, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) =>
        keys.Store.KeepSamples(keys.Sampling, samples, key: SampleInput(keys, in request));
}
