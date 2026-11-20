using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Puck.Abstractions;
using Puck.Abstractions.Memory;
using Puck.SignedDistance;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    private sealed record PackedField(UInt128 Hash);
    private static readonly Puck.Abstractions.CodeDerivedTable<SdfProgram, PackedField> PackedFields = new();

    // Checkpoints keep the numeric palette and poses. The drawn product can use the group's canonical geometry.
    private static SdfMeshGeometry CheckpointInput(Group group, SdfProgram program) {
        var hash = new XxHash128();
        var packed = PackedFields.GetValue(program, static field =>
            new PackedField(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(field.FieldWords))));
        Span<byte> header = stackalloc byte[44];
        BinaryPrimitives.WriteUInt128LittleEndian(header, group.Sealed.Hash);
        BinaryPrimitives.WriteUInt128LittleEndian(header[16..], packed.Hash);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], group.MaterialOrder.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], group.PoseRows.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], group.PoseFirst);
        hash.Append(header);
        hash.Append(MemoryMarshal.AsBytes(group.MaterialOrder.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(group.PoseRows.AsSpan()));
        return new SdfMeshGeometry(Dynamic: group.Dynamic, Hash: hash.GetCurrentHashAsUInt128());
    }

    // What a group's samples depend on beside each lattice's own row: its shape (SdfMeshBakeGroup.Shape, its geometry
    // before the tier and the operands' poses join it), the program's packed field and step scale, its numeric palette
    // and the pose rows its lattices read, which hold every slot pose the kernel reads, a skin member's and an operand's
    // included. Everything a tier changes in what a lattice samples reaches the kernel through those rows (an operand's
    // pose snapped to the tier's cell) or the lattice's row (its spacing, a glyph's cell), so a detail tier takes its
    // floor's coarse search and final slabs from the store when they sample alike, and no sample set of another field is
    // ever taken.
    private static SdfMeshGeometry SampleFieldInput(Group group, SdfProgram program) {
        var hash = new XxHash128();
        var packed = PackedFields.GetValue(program, static field =>
            new PackedField(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(field.FieldWords))));
        Span<byte> header = stackalloc byte[48];
        BinaryPrimitives.WriteUInt128LittleEndian(header, group.Shape);
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

    // The one key a lattice's samples are kept and probed by: a probe (ProbeSamples) and a write (Complete's KeepSamples)
    // both call it with the group and the request, so a hit restores the samples the same request dispatched.
    private static UInt128 SampleInput(Group group, in SdfMeshBakeRequest request) {
        var hash = new XxHash128();
        Span<byte> geometry = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(destination: geometry, value: group.SampleGeometry.Hash);
        hash.Append(geometry);
        Span<byte> row = stackalloc byte[SdfMeshBakeBlock.LatticeBytes];
        // Use the same packer that writes the dispatch. Offsets into a batch are rebased to the start of this request.
        SdfMeshBakeBlock.WriteLattice(row, in request, first: 0, poseBase: 0, poseFirst: group.PoseFirst, poseRowCount: group.PoseRows.Length);
        hash.Append(row);
        hash.Append(MemoryMarshal.AsBytes(request.Points.Span));
        return hash.GetCurrentHashAsUInt128();
    }

    private SdfMeshStore? CheckpointStore(Group group) =>
        ((group.Checkpoints is not null) && StageCodeCurrent(group)
            && ReferenceEquals(group.CheckpointProgram, m_program) ? Library.Disk : null);

    private bool StageCodeCurrent(Group group) => (group.Checkpoints is not { } keys)
        || ((keys.CodeRevision == CodeRevision.Current) && (keys.Value == Identity.Value));

    private bool HoldsCheckpoint(Group group) {
        if (CheckpointStore(group) is not { } store) { return false; }
        var keys = group.Checkpoints!;
        return store.ContainsCheckpoint(keys.Simplification, group.CheckpointGeometry)
            || store.ContainsCheckpoint(keys.Contouring, group.CheckpointGeometry);
    }

    // The latest checkpoint that survives is the earliest work the group still owes.
    private bool ResumeCheckpoint(Group group) {
        if (CheckpointStore(group) is not { } store) { return false; }
        var keys = group.Checkpoints!;

        if (store.TryReadCheckpoint(keys.Simplification, group.CheckpointGeometry, out var simplified)) {
            using (EnterGate(site: BakeGate.Checkpoint)) { m_accounts.SimplifyHits++; }
            if (simplified is not null) { group.DiagnosticCell = simplified.BakeCell; }
            PublishMesh(group, simplified, merged: 0);
            return true;
        }
        var found = store.TryReadCheckpoint(keys.Contouring, group.CheckpointGeometry, out var contour);
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
        if (CheckpointStore(group) is not { } store) { return; }
        var keys = group.Checkpoints!;
        store.WriteCheckpoint(simplified ? keys.Simplification : keys.Contouring, group.CheckpointGeometry, mesh);
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
    // which dispatches the lattice when this answers false. A group owns one request at a time. A lattice the store is
    // known not to hold dispatches in this take, decided from the store's knowledge of its sample sets
    // (SdfMeshStore.MayHoldSamples) with no file read: on a cold store, every lattice. One it may hold is read on the
    // bake's pool ahead of every queued stage, an answer's place; a miss hands it back to the next take, which dispatches
    // it unprobed. A probe is never a stage (Schedule), so a group's lead (SdfMeshBakeBacklog.ObserveLead) runs from its
    // admission to its first real stage. An answered probe never consumes GPU dispatch budget.
    private bool ProbeSamples(Group group, in SdfMeshBakeRequest request) {
        if (CheckpointStore(group) is not { } store) { return false; }
        if (!group.CheckedCheckpoints) {
            group.CheckedCheckpoints = true;
            m_accounts.SimplifyMisses++;
            m_accounts.ContourMisses++;
        }

        var sampling = group.Checkpoints!.Sampling;
        var missed = group.ProbeMissed;

        group.ProbeMissed = false;
        if (missed || !store.MayHoldSamples(sampling) || !store.MayHoldSamples(sampling, SampleInput(group, in request))) {
            m_accounts.SampleMisses++;
            return false;
        }
        // A retired group takes no lattice: its bake no longer plans it.
        if (!group.QueueWork()) { return true; }

        var pending = request;

        group.Contouring = true;
        Enqueue(group: group, urgent: true, work: () => LoadSamples(group, pending, store));
        return true;
    }

    // Reads a probed lattice's samples and answers the group with them, or hands the lattice back to the next take, which
    // dispatches it.
    private void LoadSamples(Group group, SdfMeshBakeRequest request, SdfMeshStore store) {
        if (group.Retired) { return; }

        var length = SampleBytes(in request);
        using var samples = new OwnedBuffer<byte>(allocator: Library.Allocator, observer: group.Memory);

        if (store.TryReadSamples(group.Checkpoints!.Sampling, SampleInput(group, in request), length, samples)) {
            using var answer = Decode(group, in request, samples.GetSpan());
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
    }

    // Keeps a dispatched lattice's samples for a later bake, on the store's writer; the answer does not wait for it.
    private void KeepSamples(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) {
        if (CheckpointStore(group) is not { } store) { return; }

        var kept = request;

        store.KeepSamples(group.Checkpoints!.Sampling, samples, key: () => SampleInput(group, in kept));
    }

    private static int SampleBytes(in SdfMeshBakeRequest request) =>
        checked(request.Samples * Puck.Shaders.SdfWorldPackage.MeshBakeSampleWords * sizeof(uint));
}
