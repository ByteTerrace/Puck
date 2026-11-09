using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.Abstractions.Memory;
using Puck.Hosting;
using Puck.Hosting.Jobs;
using BakeStage = Puck.SdfVm.SdfMeshBakeReport.Stage;

namespace Puck.SdfVm;

/// <summary>One dispatch of the mesh bake's field sampler: the lattice it samples and the instances it selects.</summary>
/// <param name="Origin">The position of the lattice's first sample: in world space, or in the slot's frame for a
/// non-negative <paramref name="Selector"/>.</param>
/// <param name="Cell">The spacing between samples along each axis, in world units.</param>
/// <param name="Dims">The samples along each axis.</param>
/// <param name="Selector">A dynamic-transform slot whose instances the bake samples when non-negative, the run of
/// <see cref="Instances"/> instances from <c>-(Selector + 1)</c> on otherwise.</param>
/// <param name="Gradients">Whether the sampler measures the field's gradient at each sample: a points lattice's, at the
/// samples of the final lattice its contour reads one at.</param>
public readonly record struct SdfMeshBakeRequest(Vector3 Origin, Vector3 Cell, (int X, int Y, int Z) Dims, int Selector, bool Gradients) {
    /// <summary>Gets the lattice's local frame, independently of the instances selected by its mask.</summary>
    public int FrameSlot { get; init; } = ((Selector >= 0) ? Selector : -1);

    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => ((Dims.X * Dims.Y) * Dims.Z);
    /// <summary>Gets the field evaluations the dispatch makes: five a sample that measures the gradient, one
    /// otherwise.</summary>
    public int Taps => (Gradients ? (5 * Samples) : Samples);

    /// <summary>Gets the first plane along Z of its group's final lattice that a slab of it samples, or -1 for any other
    /// lattice. A slab keeps its final lattice's <see cref="Origin"/>: its sample at plane <c>z</c> of its own lies at
    /// <c>Origin + (x, y, Slab + z) * Cell</c>, where the whole lattice's would.</summary>
    public int Slab { get; init; } = -1;

    /// <summary>Returns where one of the lattice's samples lies, as the bake kernel places it: a points lattice's point,
    /// otherwise its grid position from <see cref="Origin"/>, a slab's planes counted from <see cref="Slab"/>.</summary>
    /// <param name="local">The sample's index within the lattice, from zero to <see cref="Samples"/>.</param>
    /// <returns>The sample's position, in the frame <see cref="Origin"/> is in.</returns>
    public Vector3 SampleAt(int local) {
        if (!Points.IsEmpty) {
            var point = Points.Span[local];

            return new Vector3(x: point.X, y: point.Y, z: point.Z);
        }

        return (Origin + (new Vector3(x: (local % Dims.X), y: ((local / Dims.X) % Dims.Y), z: ((local / (Dims.X * Dims.Y)) + Math.Max(val1: 0, val2: Slab))) * Cell));
    }

    /// <summary>Gets the surface points a points lattice samples, one a sample, in place of a grid, in the frame
    /// <see cref="Origin"/> is in; empty for a grid lattice.</summary>
    public ReadOnlyMemory<Vector4> Points { get; init; }

    /// <summary>Gets the run of instances a static selector names, from the instance <c>-(Selector + 1)</c> on: the
    /// instances of one rigid copy, which bake as one mesh (<see cref="SdfInstanceRange.BakesWithPrevious"/>). One for
    /// every other selector.</summary>
    public int Instances { get; init; } = 1;
}
/// <summary>The lattices one bake dispatch samples, at most <see cref="SdfWorldPackage.MeshBakeBatch"/> holding at most
/// <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples together, and the group each answers.</summary>
/// <param name="Lattices">The lattices, in dispatch order.</param>
/// <param name="Tickets">The group each lattice answers.</param>
public sealed record SdfMeshBakeBatch(SdfMeshBakeRequest[] Lattices, object[] Tickets) {
    /// <summary>Gets each lattice's group's poses: the first slot they cover and three rows a slot from it, laid out as
    /// the dynamic-transform table lays a slot out, which the bake kernel reads in place of the frame's transforms; no
    /// rows for a group that reads no slot.</summary>
    public (int First, ReadOnlyMemory<Vector4> Rows)[] Poses { get; init; } = [];
    /// <summary>Gets the snapshots the batch's lattices sample, each at its field's handle in the program the batch samples:
    /// the ones its groups' geometries hashed (<see cref="SdfMeshBakeGroup.SampledSnapshots"/>), one snapshot a handle.</summary>
    public (int Handle, SdfSampledSnapshot Snapshot)[] Sampled { get; init; } = [];
    /// <summary>Gets the handles of the program the batch samples (<see cref="SdfProgram.SampledFields"/>), which its sampled
    /// table's directory holds a word for.</summary>
    public int SampledHandles { get; init; }
    /// <summary>Gets the words of the batch's sampled table (<see cref="WriteSampled"/>), at least one.</summary>
    public int SampledWords {
        get {
            var words = SampledHandles;

            foreach (var (_, snapshot) in Sampled) { words = checked(words + snapshot.Count); }
            return Math.Max(val1: 1, val2: words);
        }
    }
    /// <summary>Writes the table the bake kernel reads sampled values from (<c>SdfWorldPackage.SampledValues</c>): a word a
    /// handle, the word its snapshot starts at or <c>0xFFFFFFFF</c> for a handle the batch samples none of, then each
    /// snapshot's voxels as float bits.</summary>
    /// <param name="table">The table, at least <see cref="SampledWords"/> words.</param>
    public void WriteSampled(Span<uint> table) {
        table[..SampledHandles].Fill(value: uint.MaxValue);
        var cursor = SampledHandles;

        foreach (var (handle, snapshot) in Sampled) {
            table[handle] = (uint)cursor;
            System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(span: snapshot.Values).CopyTo(destination: table[cursor..]);
            cursor += snapshot.Count;
        }
    }
    /// <summary>Gets the input prefix the batch reads: explicit points at their sample offsets, then saved poses after
    /// all samples. Grid samples without poses need no input rows, including trailing grid samples.</summary>
    public int InputRows {
        get {
            var first = 0;
            var required = 0;

            foreach (var lattice in Lattices) {
                if (!lattice.Points.IsEmpty) { required = Math.Max(val1: required, val2: checked(first + lattice.Points.Length)); }
                first = checked(first + lattice.Samples);
            }
            foreach (var (_, rows) in Poses) {
                first = checked(first + rows.Length);
                if (!rows.IsEmpty) { required = Math.Max(val1: required, val2: first); }
            }

            return required;
        }
    }
    /// <summary>Gets the bake whose groups the batch's lattices answer, which its readback completes or abandons whatever
    /// residency the pass that dispatched it records by then; <see langword="null"/> for a batch no bake took.</summary>
    public SdfMeshPathBake? Bake { get; internal init; }
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples {
        get {
            var samples = 0;

            foreach (var lattice in Lattices) {
                samples += lattice.Samples;
            }

            return samples;
        }
    }
    // Each lattice's ticket, written only under its bake's gate by its one transition (SdfMeshPathBake.Settle).
    internal SdfMeshBakeTicket[] States { get; } = new SdfMeshBakeTicket[Tickets.Length];

    /// <summary>Returns where a lattice's ticket stands: taken until its samples reach its group or slice, or it is handed
    /// back unanswered.</summary>
    /// <param name="lattice">The lattice's index in <see cref="Lattices"/>.</param>
    /// <returns>The ticket's state.</returns>
    public SdfMeshBakeTicket StateOf(int lattice) {
        if (Bake is not { } bake) { return States[lattice]; }
        lock (bake.Gate) { return States[lattice]; }
    }

    /// <summary>Returns the runs of the batch's point table: each points lattice's points at its first sample's place in
    /// the dispatch, then each lattice's poses from <see cref="PoseBase"/>, in table rows of one <see cref="Vector4"/>.
    /// The rows between the runs are never read, so a table that holds the runs alone leaves no row the bake kernel reads
    /// stale.</summary>
    /// <returns>The first table row of each run and its rows, in table order; empty when the batch reads no table.</returns>
    public (int Row, ReadOnlyMemory<Vector4> Rows)[] PointRuns() {
        var runs = new List<(int Row, ReadOnlyMemory<Vector4> Rows)>();
        var first = 0;

        foreach (var lattice in Lattices) {
            if (!lattice.Points.IsEmpty) { runs.Add(item: (first, lattice.Points)); }
            first += lattice.Samples;
        }
        for (var index = 0; (index < Poses.Length); index++) {
            if (!Poses[index].Rows.IsEmpty) { runs.Add(item: (PoseBase(lattice: index), Poses[index].Rows)); }
        }

        return [.. runs];
    }
    /// <summary>Writes the batch's point table (<see cref="PointRuns"/>) into a table of rows, as an answerer on the CPU
    /// reads it.</summary>
    /// <param name="table">The table, at least <see cref="InputRows"/> rows.</param>
    /// <returns>Whether the batch wrote a row.</returns>
    public bool WritePoints(Span<Vector4> table) {
        var runs = PointRuns();

        foreach (var (row, rows) in runs) { rows.Span.CopyTo(destination: table[row..]); }
        return (runs.Length > 0);
    }
    /// <summary>Returns the point table entry a lattice's poses start at: after every sample's point and every earlier
    /// lattice's poses.</summary>
    /// <param name="lattice">The lattice's index in <see cref="Lattices"/>.</param>
    /// <returns>The entry.</returns>
    public int PoseBase(int lattice) {
        var entry = Samples;

        for (var index = 0; (index < Math.Min(val1: lattice, val2: Poses.Length)); index++) {
            entry += Poses[index].Rows.Length;
        }

        return entry;
    }
}
/// <summary>Where one lattice of a taken batch stands (<see cref="SdfMeshBakeBatch.StateOf"/>): each ticket leaves
/// <see cref="Taken"/> once, for <see cref="Answered"/> or <see cref="Returned"/>, so answering and abandoning a batch,
/// in either order or in part, settle each lattice exactly once.</summary>
public enum SdfMeshBakeTicket {
    /// <summary>The lattice is dispatched or awaiting its samples, and its group takes no other lattice.</summary>
    Taken,
    /// <summary>The lattice's samples reached its group or slice.</summary>
    Answered,
    /// <summary>The lattice went back to its group or slice unanswered, to be taken again.</summary>
    Returned,
}
/// <summary>
/// The baked-mesh path's bake of one residency's program: a mesh per bake group, sampled from the program on the GPU and
/// contoured on the CPU, drawn in the frame's mesh draws by every view of the residency.
/// <para>A group is the instances one rigid frame moves: every active instance riding one dynamic-transform slot, or one
/// static copy, the run of static instances that bake with the first (<see cref="SdfInstanceRange.BakesWithPrevious"/>),
/// so shapes of one copy that share a face bake as their union. Each group's field is sampled through the frame's own
/// interpreter (<c>sdf-mesh-bake.comp.hlsl</c>) with only its instances in the walk's mask, lattices taken in the groups'
/// order into batches the residency's background GPU work dispatches (<see cref="TryTake"/>, <see cref="SdfGpuWork"/>),
/// each lattice at most <see cref="LatticeTaps"/> field evaluations: a coarse lattice over the group's bounding sphere, refinements over the box of
/// the cells the surface can cross until the box stops shrinking, then a final lattice of at most 65 samples a side
/// whose spacing follows the box's own proportions (a union's cubic, keeping each of its shapes at the resolution its own
/// lattice would give it), sampled in slabs of whole planes, values alone; then the fine samples
/// of the cells the final lattice cannot resolve (<see cref="SdfSampledLattice.Refine"/>), and then gradients at the
/// samples a contour reads one at alone (<see cref="SdfSampledLattice.GradientPoints"/>). The final samples contour on
/// the thread pool (<see cref="SdfSampledLattice"/>) and split at their creases (<see cref="SdfContour.SplitAtCreases"/>).
/// A dynamic group bakes in its slot's frame: its box, lattices and points are in that frame, and the kernel reads every
/// transform through the poses the group's geometry hashed (<see cref="SdfMeshGeometryBuilder.Poses"/>), never the frame
/// it dispatches in, so a mesh is a function of its geometry and a body moving while it bakes bakes whole. Its vertices stay in that frame, and
/// its draw follows the slot's live transform; a static group's are in world space.</para>
/// <para>A new program restarts the bake; the meshes of the last finished bake keep drawing until each group's new mesh
/// lands. A group whose geometry the library holds takes that mesh, and one its store kept from an earlier run under the
/// bake's <see cref="Identity"/> loads from disk off the frame's thread (<see cref="SdfMeshStore"/>), baking only when the
/// store refuses the entry. The bake reports its counted work, the groups it loaded and the wall time it took
/// (<see cref="Describe"/>).</para>
/// <para>A dynamic group whose shapes ride slots besides its own is planned again once those shapes settle away from the
/// pose it was baked at, and its new mesh replaces the skinned one when it lands (<see cref="SdfMeshSettle"/>).</para>
/// </summary>
public sealed partial class SdfMeshPathBake {
    /// <summary>The field evaluations one lattice makes at most: long lattices and point runs are cut into chunks of this
    /// many evaluations (a gradient sample counts five), a few milliseconds of a current desktop GPU
    /// (<see cref="NanosecondsPerTap"/>). No lattice's spacing or extent reads it, so the meshes do not depend on it.</summary>
    public const int LatticeTaps = 1_000_000;
    /// <summary>The field evaluations one batch (<see cref="TryTake"/>) makes at most, past its first lattice. The
    /// background GPU work (<see cref="SdfGpuWork"/>) paces batches by their measured GPU time; this bounds one
    /// submission's.</summary>
    public const int BatchTaps = (8 * LatticeTaps);
    /// <summary>The GPU time one field evaluation is estimated to take, in nanoseconds, on a current desktop GPU (the
    /// debug world's cold bake measures about four on an RTX 4070): what the background GPU work charges a batch before
    /// its timestamps measure it, and on a queue without timestamps.</summary>
    public const double NanosecondsPerTap = 4.0;
    /// <summary>The frame, in seconds, a per-frame budget is sized for (<see cref="FrameShares"/>): a 60 Hz frame.</summary>
    public const double ReferenceFrameSeconds = (1.0 / 60.0);
    /// <summary>The most reference frames' shares one frame takes, however long it is.</summary>
    public const int MaxFrameShares = 8;

    // Samples along the longest axis of a coarse or refining lattice, and of the final one.
    private const int CoarseSamples = 33;
    private const int FinalSamples = (SdfWorldPackage.MeshBakeMaxSamplesPerAxis);
    // The fewest samples along any axis of a refining lattice, and of the final one, so a thin axis keeps an inside sample.
    private const int RefineMinimum = 17;
    private const int FinalMinimum = 9;
    private const int MaxRefinements = 5;

    /// <summary>The finest spacing, in metres, of a final lattice along its longest axis: a part smaller than the lattice
    /// would resolve bakes at this spacing unless an instance declares a finer detail (<see cref="SdfInstanceRange.Detail"/>).
    /// A detail at or past it refines nothing the lattice does not already resolve.</summary>
    public const float MinimumCell = 0.015f;

    // A refinement whose box keeps this share of every extent has converged.
    private const float Converged = 0.7f;
    // The spacing, in metres, a final lattice's refined cells aim for, and the most fine cells a coarse cell splits into
    // along each axis (SdfSampledLattice.Refine).
    private const float FineCell = 0.005f;

    private const int MaxRefinement = 4;
    // The most fine cells a coarse cell splits into along each axis when a member declares its own detail
    // (SdfInstanceRange.Detail), which the lattice's spacing does not reach at MaxRefinement.
    private const int MaxDetailRefinement = 8;
    // The samples a union's final lattice holds at most before it coarsens (UnionFinal), and how much finer than that
    // lattice a member's own spacing is before its region refines.
    private const float UnionSamples = 1_048_576f;
    // The samples one plane of a union's final lattice holds at most, which coarsens a union broad in X and Y.
    private const float UnionPlaneSamples = 75_000f;
    private const float RegionRatio = 1.5f;

    /// <summary>Gets the name of the residency whose program the bake meshes, which its start and done lines name.</summary>
    public string Residency { get; init; } = string.Empty;

    /// <summary>Gets this residency's process-local diagnostic identity, independent of its display name and program.</summary>
    public string ResidencyId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the selectors of the groups the bake bakes, every group when <see langword="null"/>: a group outside
    /// it finishes unbaked, with no mesh, and no selected group of its geometry waits on it (an offline audit of chosen
    /// groups).</summary>
    public IReadOnlySet<int>? Selection { get; init; }

    /// <summary>Gets the library the bake stores each group's mesh in and takes a group of geometry it holds from, rather
    /// than baking the group again: in a later program of the residency, or in another residency holding the same
    /// library.</summary>
    public SdfMeshLibrary Library { get; init; } = new();
    /// <summary>Gets or sets the identity of the bake on the kernels it dispatches (<see cref="SdfMeshStore.IdentityOf"/>),
    /// under which a new program's groups load from the library's store and its meshes are written there; the residency
    /// sets it from its kernel set. A group keeps the identity its program started under. A program that misses groups
    /// the store does not hold logs why, by the identity's parts (<see cref="SdfMeshStore.ExplainMisses"/>).</summary>
    public SdfMeshBakeIdentity Identity { get; set; } = new(value: UInt128.Zero);

    private readonly Lock m_gate = new();
    // The gate a taken batch's tickets are read under (SdfMeshBakeBatch.StateOf).
    internal Lock Gate => m_gate;
    private readonly List<Group> m_groups = [];
    private readonly List<Group> m_retiredGroups = [];

    // How many of the first groups Pending found done; every write of a group lowers it to that group's index (Replace).
    private int m_doneThrough;

    private readonly Stopwatch m_clock = new();

    private SdfProgram? m_program;
    // The baked sampled set the groups were planned at (SdfSampledSet.Baked): a frame carrying another resamples the groups
    // that read a moved field.
    private SdfSampledSet m_sampled = SdfSampledSet.Empty;
    // The indices, ascending, of the groups whose geometry reads a sampled field: the only groups a resample can touch.
    // Which groups read one is the program's (a group planned again at an index holds the same instances), so Restart, the
    // one writer of a program's group list, rebuilds it (WatchSettles).
    private readonly List<int> m_sampledReaders = [];
    // The groups holding a successor (Group.Successor), which Promote starts once their bake lands.
    private int m_successors;
    // Frames prepared; restarts and the groups they planned; resamples, the readers they walked, the newer plans that
    // replaced a waiting successor, and the successors started: what moved fields cost the frame thread, as counts.
    private long m_preparedFrames;
    private long m_restarts;
    private long m_restartGroups;
    private long m_resamples;
    private long m_resampleGroups;
    private long m_coalesced;
    private long m_promoted;
    private double m_seconds;
    private bool m_reported;

    /// <summary>Gets the program the bake meshes: the one the last prepared frame carried, or
    /// <see langword="null"/> before the first.</summary>
    public SdfProgram? Program {
        get {
            lock (m_gate) { return m_program; }
        }
    }

    /// <summary>Reads the mesh a group landed, whole: a chunked group's parent, whose surface concatenates the chunks
    /// <see cref="SdfMeshDresser"/> draws one by one.</summary>
    /// <param name="selector">The group's <see cref="SdfMeshBakeGroup.Selector"/>.</param>
    /// <param name="mesh">The landed mesh, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the group is planned and has landed a mesh.</returns>
    public bool TryLandedMesh(int selector, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh) {
        lock (m_gate) {
            foreach (var group in m_groups) {
                if ((group.Selector == selector) && (group.Mesh is { } landed)) {
                    mesh = landed;
                    return true;
                }
            }
        }

        mesh = null;
        return false;
    }

    /// <summary>Gets whether a group still owes a lattice the frame can dispatch, a contour still runs, a field slice
    /// (<see cref="Probe"/>) still samples, a newer plan of a resampled group waits to start, or a landed mesh has not
    /// reached the dressed frame yet.</summary>
    public bool Pending {
        get {
            lock (m_gate) {
                if ((m_program is not null) && (m_dressedRevision != m_productRevision)) { return true; }
                if ((m_slice is not null) || (m_successors > 0)) { return true; }
                // A group's Done only turns on, so the groups before the cursor stay done until one is replaced.
                while ((m_doneThrough < m_groups.Count) && m_groups[m_doneThrough].Done) { m_doneThrough++; }
                return (m_doneThrough < m_groups.Count);
            }
        }
    }
    /// <summary>Gets whether an opaque static group still owes a mesh, or a landed mesh has not reached the dressed
    /// frame. Only these groups enter the irradiance scene: animated groups, glass and diagnostic field slices must
    /// not hold its first lighting measurement or a later static-scene refresh behind unrelated work.</summary>
    public bool IrradiancePending {
        get {
            lock (m_gate) {
                if ((m_program is not null) && (m_dressedRevision != m_productRevision)) { return true; }
                foreach (var group in m_groups) {
                    if ((!group.Dynamic || m_fixedAssetSlots.Contains(group.Slot)) && !group.Glass && !group.Done) { return true; }
                }
                return false;
            }
        }
    }

    /// <summary>Queues a field slice, whose lattices the frame's bake dispatch takes before any group's, one reference
    /// frame's share at a time; its completion action runs on the thread that answers its last lattice, and a new program
    /// restarts it. One slice samples at a time.</summary>
    /// <param name="slice">The slice to sample.</param>
    /// <returns><see langword="false"/> when another slice still samples, which leaves the queue unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slice"/> is <see langword="null"/>.</exception>
    public bool Probe(SdfFieldSlice slice) {
        ArgumentNullException.ThrowIfNull(argument: slice);

        lock (m_gate) {
            if (m_slice is not null) { return false; }
            m_slice = slice;
            return true;
        }
    }

    // The field slice still sampling, and the ticket each of its lattices answers through.
    private SdfFieldSlice? m_slice;

    private sealed record SliceTicket(SdfFieldSlice Slice, int Lattice, int Generation);

    // The bake owns the one copy-membership index used by both demand and dressing. Its snapshot contains no poses.
    private readonly HashSet<int> m_fixedAssetSlots = [];
    private readonly Dictionary<int, List<int>> m_slotCopies = [];
    private readonly Dictionary<int, List<int>> m_instanceCopies = [];
    private IReadOnlyList<SdfMeshInstance> m_demandCopies = [];
    private CopyLayout[] m_copyLayout = [];
    private int[] m_copyAssetSlots = [];
    private long m_copyLayoutRevision;
    private readonly record struct CopyLayout(int AssetSlot, SdfMeshTemplateRange? TemplateRange, bool IsDynamic, bool HasMesh, int[] OtherAssetSlots);

    // A changed landed product is acknowledged only after the dresser has published its draws and ownership claims.
    private long m_productRevision;
    private long m_dressedRevision = -1;
    // The library's lost revision this bake last restarted at.
    private long m_lostRevision;

    // This is the only door from presentation to the bake's live groups. Preparation and reading share one gate;
    // workers cannot replace/release a landed mesh before the dresser has claimed it. No bake entry calls presentation.
    internal FrameAccess PrepareFrame(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        using (EnterFrameGate()) {
            m_preparedFrames++;
            // The bake reads only the baked handles: a frame whose height fields' heights alone moved changes nothing here.
            // A lost mesh (SdfMeshLibrary.Lose) restarts the bake on the same program, which bakes again only the groups
            // whose meshes were lost (Keeps).
            if (!ReferenceEquals(frame.Program, m_program) || (Library.LostRevision != m_lostRevision)) {
                m_lostRevision = Library.LostRevision;
                Restart(frame);
            } else if (!ReferenceEquals(frame.Sampled.Baked, m_sampled)) {
                Resample(frame);
            }
            if (m_successors > 0) { Promote(frame); }
            m_demandTransforms = frame.DynamicTransforms;
            m_demandCopies = frame.MeshInstances;
            if (!SameCopyLayout(frame)) { IndexCopies(frame); }
        }
        // Measured with the gate open, so the workers keep publishing meanwhile (Prioritize).
        Prioritize();
        var scope = EnterFrameGate();
        try {
            Settle(frame);
            return new FrameAccess(this, scope);
        } catch { scope.Dispose(); throw; }
    }

    internal ref struct FrameAccess {
        private readonly SdfMeshPathBake m_owner;
        private Lock.Scope m_scope;
        internal FrameAccess(SdfMeshPathBake owner, Lock.Scope scope) {
            m_owner = owner;
            m_scope = scope;
            Revision = owner.m_productRevision;
            CopyLayoutRevision = owner.m_copyLayoutRevision;
        }
        internal long Revision { get; }
        internal long CopyLayoutRevision { get; }
        internal IReadOnlyList<SdfMeshBakeGroup> Groups => m_owner.m_groups;
        internal SdfMesh? MeshOf(SdfMeshBakeGroup group) => ((Group)group).Mesh;
        internal IReadOnlyList<int>? CopiesOf(SdfMeshBakeGroup group) => m_owner.CopiesOf(group);
        internal void Acknowledge() => m_owner.m_dressedRevision = Revision;
        public void Dispose() => m_scope.Dispose();
    }

    private bool SameCopyLayout(SdfFrame frame) {
        if ((m_copyLayout.Length != frame.MeshInstances.Count) || (m_copyAssetSlots.Length != frame.MeshAssetSlots.Count)) { return false; }
        for (var index = 0; index < m_copyAssetSlots.Length; index++) { if (m_copyAssetSlots[index] != frame.MeshAssetSlots[index]) { return false; } }
        for (var index = 0; index < m_copyLayout.Length; index++) {
            var old = m_copyLayout[index];
            var copy = frame.MeshInstances[index];
            if ((old.AssetSlot != copy.AssetSlot) || (old.TemplateRange != copy.TemplateRange) || (old.IsDynamic != copy.IsDynamic) || (old.HasMesh != (copy.Mesh is not null)) ||
                (old.OtherAssetSlots.Length != copy.OtherAssetSlots.Count)) { return false; }
            for (var surface = 0; surface < old.OtherAssetSlots.Length; surface++) {
                if (old.OtherAssetSlots[surface] != copy.OtherAssetSlots[surface]) { return false; }
            }
        }
        return true;
    }

    // The one membership index serves both pending-bake demand and landed draw dressing. It contains no poses:
    // a moving occurrence keeps its membership and supplies its current matrix through m_demandCopies.
    private void IndexCopies(SdfFrame frame) {
        m_slotCopies.Clear();
        m_instanceCopies.Clear();
        m_fixedAssetSlots.Clear();
        m_copyAssetSlots = frame.MeshAssetSlots.ToArray();
        m_copyLayout = new CopyLayout[frame.MeshInstances.Count];
        for (var index = 0; index < m_copyLayout.Length; index++) {
            var copy = frame.MeshInstances[index];
            m_copyLayout[index] = new(copy.AssetSlot, copy.TemplateRange, copy.IsDynamic, copy.Mesh is not null, copy.OtherAssetSlots.ToArray());
        }
        m_copyLayoutRevision++;
        for (var index = 0; index < frame.MeshInstances.Count; index++) {
            var copy = frame.MeshInstances[index];
            if (copy.TemplateRange is { } range) {
                for (var instance = 0; instance < range.InstanceCount; instance++) { AddCopyIndex(m_instanceCopies, range.FirstInstance + instance, index); }
                continue;
            }
            AddCopyIndex(m_slotCopies, copy.AssetSlot, index);
            if (!copy.IsDynamic) { m_fixedAssetSlots.Add(copy.AssetSlot); }
            foreach (var slot in copy.OtherAssetSlots) {
                if (slot != copy.AssetSlot) { AddCopyIndex(m_slotCopies, slot, index); }
                if (!copy.IsDynamic) { m_fixedAssetSlots.Add(slot); }
            }
        }
    }
    private static void AddCopyIndex(Dictionary<int, List<int>> table, int key, int copy) {
        if (!table.TryGetValue(key, out var indices)) { table.Add(key, indices = []); }
        if ((indices.Count == 0) || (indices[^1] != copy)) { indices.Add(copy); }
    }
    private IReadOnlyList<int>? CopiesOf(SdfMeshBakeGroup group) {
        if (!group.Dynamic) { return m_instanceCopies.GetValueOrDefault(group.FirstInstance); }
        if (!m_copyAssetSlots.Contains(group.Slot)) { return null; }
        return (m_slotCopies.TryGetValue(group.Slot, out var copies) ? copies : Array.Empty<int>());
    }
    // The lattices and their groups while TryTake chooses them, reused take to take.
    private readonly List<SdfMeshBakeRequest> m_takenLattices = [];
    private readonly List<object> m_takenTickets = [];

    /// <summary>Returns the reference frames' shares a frame presented over <paramref name="frameSeconds"/> takes of a
    /// per-frame budget: one for each whole <see cref="ReferenceFrameSeconds"/> it spans, between one and
    /// <see cref="MaxFrameShares"/>. A budget sized for a 60 Hz frame then spends the same share of a longer frame's time
    /// (the mesh region's landing words).</summary>
    /// <param name="frameSeconds">The interval the frame's presentation spans, in seconds; zero or less is a reference
    /// frame.</param>
    /// <returns>The frame's shares.</returns>
    public static int FrameShares(double frameSeconds) =>
        ((int)Math.Clamp(value: Math.Floor(d: (frameSeconds / ReferenceFrameSeconds)), min: 1.0, max: MaxFrameShares));
    /// <summary>Takes the lattices of one dispatch: the next lattice of each group that owes one and has none in flight,
    /// the groups the demand points see largest first (<see cref="SdfMeshDemand"/>) and the plan's order among equals, up
    /// to <see cref="SdfWorldPackage.MeshBakeBatch"/> lattices, <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples
    /// and <see cref="BatchTaps"/> evaluations. Each call takes a batch of its own, so several batches dispatch at once,
    /// each lattice in one of them, and a taken batch counts as the bake's work. Taking by priority rather than round robin
    /// finishes the groups about the views first, each landing in the mesh library as it finishes, so a residency the
    /// program leaves mid-bake (a body crossing into another world) leaves the groups it finished there for the residency
    /// it reaches.</summary>
    /// <param name="field">The program whose field the dispatch samples: the tables' uploaded program. The bake takes
    /// nothing unless it is the program the bake meshes (<see cref="Program"/>), so a batch never samples another
    /// program's field.</param>
    /// <param name="batch">The lattices to dispatch and the group each answers, handed back to
    /// <see cref="Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/> or <see cref="Abandon"/>.</param>
    /// <returns><see langword="true"/> when the bake takes a lattice.</returns>
    public bool TryTake(SdfProgram? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch) {
        batch = null;
        using (EnterFrameGate()) {
            if ((field is null) || !ReferenceEquals(objA: field, objB: m_program)) { return false; }

            var lattices = m_takenLattices;
            var tickets = m_takenTickets;

            lattices.Clear();
            tickets.Clear();
            m_takenPoses.Clear();
            m_takenSampled.Clear();
            var samples = 0;
            var taps = 0;
            // A slice's lattices come first, within the same batch, so a long bake never holds a diagnostic back.
            while ((m_slice is { } slice) && (lattices.Count < SdfWorldPackage.MeshBakeBatch)) {
                if (slice.Take(instances: (m_program?.Instances.Count ?? 0), ticket: out var lattice, worldSelector: SdfMeshBakeGroup.WorldSelector) is not { } next) { break; }
                if ((lattices.Count > 0) && (((taps + next.Taps) > BatchTaps) || ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples))) {
                    slice.Return(ticket: lattice);
                    break;
                }
                samples += next.Samples;
                taps += next.Taps;
                lattices.Add(item: next);
                tickets.Add(item: new SliceTicket(Generation: slice.Generation, Lattice: lattice, Slice: slice));
                m_takenPoses.Add(item: (0, ReadOnlyMemory<Vector4>.Empty));
                // A slice samples any instance, so it reads every field at the bake's set.
                for (var handle = 0; (handle < m_sampled.Count); handle++) { m_takenSampled[handle] = m_sampled[handle]; }
            }
            var candidates = m_candidates;
            var pending = false;
            var flying = 0;
            var working = 0;
            var admitted = 0;

            candidates.Clear();
            for (var index = 0; (index < m_groups.Count); index++) {
                var group = m_groups[index];

                pending |= !group.Done;
                flying += ((!group.Done && group.InFlight) ? 1 : 0);
                working += ((!group.Done && group.Contouring) ? 1 : 0);
                admitted += ((!group.Done && group.Admitted) ? 1 : 0);
                if (group.Done || group.InFlight || group.Contouring || (group.Next is null)) { continue; }
                candidates.Add(item: (group.Priority, index));
            }
            if (pending) {
                m_accounts.Takes++;
                m_accounts.WaitingGroups += candidates.Count;
                m_accounts.FlyingGroups += flying;
                m_accounts.WorkingGroups += working;
            }
            CollectionsMarshal.AsSpan(list: candidates).Sort(comparison: ByPriority);
            var stop = 0;
            // The first group the account defers holds its place: no group behind it starts its final lattice
            // before it does, so a large group never starves behind a stream of smaller ones.
            var admissionWaits = false;
            long? available = null;
            var availableRead = false;
            var backlogged = false;
            var backlogRead = false;

            foreach (var (_, index) in candidates) {
                if (lattices.Count >= SdfWorldPackage.MeshBakeBatch) { stop = 3; break; }

                var group = m_groups[index];
                var next = group.Next!.Value;

                // A batch reads one snapshot a handle: a group whose geometry hashed another waits for a later batch.
                if (!SamplesAlike(group: group)) { continue; }

                // A lattice past the dispatch's remaining samples waits, and a smaller one behind it fills them: a
                // dispatch that stops at the first lattice too large for it leaves half its samples unused.
                if ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples) {
                    stop = 2;
                    continue;
                }
                // Only sampled values consume the fixed readback budget. Saved poses follow the sample points
                // in a growable input table; charging them here strands a full 65-cubed lattice forever.
                // A batch's first lattice is always taken, and no lattice holds more than LatticeTaps.
                if ((lattices.Count > 0) && ((taps + next.Taps) > BatchTaps)) { stop = 1; break; }
                // A group starting its final lattice holds that lattice and everything meshed from it until it lands,
                // so the process account admits its simultaneous peak; the rest keep their small refining lattices.
                // A first slab returned unanswered keeps the admission it took.
                if ((next.Slab == 0) && !group.Admitted) {
                    if (admissionWaits) { continue; }
                    // A group admitted while the queue already holds a round of every worker's stages would only wait
                    // there holding its lattice: admission waits for the queue to drain (Backlogged).
                    if (!backlogRead) { backlogged = Backlogged(); backlogRead = true; }
                    if (backlogged && (admitted > 0)) {
                        admissionWaits = true;
                        continue;
                    }

                    var peak = PeakOf(final: group.Final!);

                    // The machine's free memory paces admission; it is read outside the account, at most every
                    // AvailableSeconds.
                    if (!availableRead) { available = AvailablePhysicalBytes(); availableRead = true; }
                    if (!group.Memory.TryReserve(available: available, peak: peak)) {
                        if (admitted > 0) {
                            if (!group.DeferredReported) { SdfMeshBakeReport.Deferred(admitted: admitted, available: available, peak: peak, residency: Residency, selector: group.Selector); group.DeferredReported = true; }
                            admissionWaits = true;
                            continue;
                        }
                        // With nothing of the bake admitted, the group is admitted whatever the budget and the machine's
                        // free memory read, so the bake always progresses, one group at a time. The library's cache
                        // stays: the groups following a leader take its mesh from there rather than baking it again.
                        group.Memory.Reserve(peak: peak);
                        if (!group.DeferredReported) { SdfMeshBakeReport.AdmittedAlone(available: available, peak: peak, residency: Residency, selector: group.Selector); }
                    }
                    group.DeferredReported = false;
                    group.Admitted = true;
                    admitted++;
                }
                group.InFlight = true;
                samples += next.Samples;
                taps += next.Taps;
                lattices.Add(item: next);
                tickets.Add(item: group);
                m_takenPoses.Add(item: (group.PoseFirst, group.PoseRows));
                foreach (var (handle, snapshot) in group.SampledSnapshots) { m_takenSampled[handle] = snapshot; }
            }
            if (pending) {
                if (lattices.Count == 0) { m_accounts.IdleTakes++; }
                switch (stop) {
                    case 1: m_accounts.StopTaps++; break;
                    case 2: m_accounts.StopSamples++; break;
                    case 3: m_accounts.StopBatch++; break;
                    default: m_accounts.StopDrained++; break;
                }
            }
            if (lattices.Count == 0) { return false; }
            batch = new SdfMeshBakeBatch(Lattices: [.. lattices], Tickets: [.. tickets]) {
                Bake = this,
                Poses = [.. m_takenPoses],
                Sampled = [.. m_takenSampled.Select(selector: static entry => (entry.Key, entry.Value))],
                SampledHandles = (m_program?.SampledFields.Count ?? 0),
            };
            m_accounts.Batches++;
            foreach (var lattice in batch.Lattices) {
                m_accounts.Dispatches++;
                m_accounts.Samples += lattice.Samples;
                m_accounts.Taps += lattice.Taps;
            }
            return true;
        }
    }

    // How long a reading of the machine's free memory paces admission before it is read again.
    private const double AvailableSeconds = 0.25;

    private long? m_available;
    private long m_availableAt;

    // The machine's free physical memory, read again once the last reading is AvailableSeconds old. Under the gate.
    private long? AvailablePhysicalBytes() {
        var now = Stopwatch.GetTimestamp();

        if ((m_availableAt == 0) || (Stopwatch.GetElapsedTime(endingTimestamp: now, startingTimestamp: m_availableAt).TotalSeconds >= AvailableSeconds)) {
            m_available = HostMemory.AvailablePhysicalBytes();
            m_availableAt = now;
        }
        return m_available;
    }

    private readonly List<(int First, ReadOnlyMemory<Vector4> Rows)> m_takenPoses = [];
    // The snapshots the batch being taken samples, by handle.
    private readonly Dictionary<int, SdfSampledSnapshot> m_takenSampled = [];

    // Whether a group's snapshots agree with the batch being taken, handle by handle.
    private bool SamplesAlike(Group group) {
        foreach (var (handle, snapshot) in group.SampledSnapshots) {
            if (m_takenSampled.TryGetValue(key: handle, value: out var taken) && !ReferenceEquals(objA: taken, objB: snapshot)) { return false; }
        }
        return true;
    }
    // The groups a take may take a lattice of, by priority, reused take to take.
    private readonly List<(float Priority, int Index)> m_candidates = [];

    // Higher priority first, then the plan's order.
    private static readonly Comparison<(float Priority, int Index)> ByPriority = static (left, right) =>
        ((right.Priority.CompareTo(value: left.Priority) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index));

    /// <summary>Returns a taken batch's lattices still unanswered to their groups, as when its dispatch never completed or
    /// its answer stopped part way; each group dispatches its lattice again, and those lattices' samples count as discarded.
    /// A lattice already answered or returned stays as it is.</summary>
    /// <param name="batch">The batch <see cref="TryTake"/> took, or <see langword="null"/>.</param>
    public void Abandon(SdfMeshBakeBatch? batch) {
        if (batch is null) { return; }
        lock (m_gate) {
            for (var index = 0; (index < batch.Tickets.Length); index++) { _ = Settle(batch: batch, index: index, to: SdfMeshBakeTicket.Returned); }
        }
    }
    /// <summary>Answers a dispatched batch with the samples its readback copied, each lattice its own slice: each group's
    /// lattice is decoded on the calling thread and answered on the bake's workers. A lattice answered or returned already
    /// takes nothing; one whose answer throws stays taken, so <see cref="Abandon"/> returns it and every lattice after
    /// it.</summary>
    /// <param name="batch">The batch <see cref="TryTake"/> took.</param>
    /// <param name="samples">The batch's samples, <see cref="SdfWorldPackage.MeshBakeSampleWords"/> words each, the
    /// lattices back to back.</param>
    public void Complete(SdfMeshBakeBatch batch, ReadOnlySpan<byte> samples) {
        ArgumentNullException.ThrowIfNull(argument: batch);

        var offset = 0;

        for (var index = 0; (index < batch.Lattices.Length); index++) {
            var lattice = batch.Lattices[index];
            var bytes = ((lattice.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint));
            var slice = samples.Slice(length: bytes, start: offset);

            offset += bytes;
            // A group's lattice is decoded here, in place of a copy of its readback, and answered off the caller's
            // thread ahead of every other queued stage; the group stays in flight until then, so the bake takes no
            // lattice of it before its answer lands. The queued job keeps the group's storage owner open for the
            // decoded answer, which a retired group never takes.
            if (batch.Tickets[index] is Group group) {
                lock (m_gate) {
                    if (batch.States[index] != SdfMeshBakeTicket.Taken) { continue; }
                }
                if (!group.QueueWork()) {
                    lock (m_gate) { _ = Settle(batch: batch, index: index, to: SdfMeshBakeTicket.Returned); }
                    continue;
                }

                Answer answer;

                try {
                    answer = Decode(group: group, request: in lattice, samples: slice);
                } catch {
                    group.EndWork();
                    throw;
                }

                bool answered;

                lock (m_gate) { answered = Settle(batch: batch, index: index, to: SdfMeshBakeTicket.Answered); }
                if (!answered) {
                    answer.Dispose();
                    group.EndWork();
                    continue;
                }
                Enqueue(group: group, urgent: true, work: () => {
                    using (answer) { AnswerGroup(answer: answer, group: group, request: in lattice); }
                });
                continue;
            }
            Complete(batch: batch, index: index, request: in lattice, samples: slice);
        }
    }
    // The one transition of a batch's ticket out of Taken, which answering and abandoning both take: Answered once its
    // samples reach its group or slice (the group stays in flight until its answer job runs), Returned when they never will,
    // which hands the lattice back to be taken again. Under the gate; false when the ticket was settled already.
    private bool Settle(SdfMeshBakeBatch batch, int index, SdfMeshBakeTicket to) {
        if (batch.States[index] != SdfMeshBakeTicket.Taken) { return false; }
        batch.States[index] = to;
        if (to != SdfMeshBakeTicket.Returned) { return true; }
        if (batch.Tickets[index] is Group group) { group.InFlight = false; }
        if ((batch.Tickets[index] is SliceTicket ticket) && ReferenceEquals(objA: ticket.Slice, objB: m_slice) && (ticket.Generation == ticket.Slice.Generation)) {
            ticket.Slice.Return(ticket: ticket.Lattice);
        }
        m_accounts.Discarded += batch.Lattices[index].Samples;
        return true;
    }

    // Answers a field slice's lattice.
    private void Complete(SdfMeshBakeBatch batch, int index, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) {
        if (batch.Tickets[index] is SliceTicket sliced) {
            bool done;

            lock (m_gate) {
                if (!Settle(batch: batch, index: index, to: SdfMeshBakeTicket.Answered)) { return; }
                // A slice a new program restarted, or one already finished, takes no answer from an earlier dispatch.
                if (!ReferenceEquals(objA: sliced.Slice, objB: m_slice) || (sliced.Slice.Generation != sliced.Generation)) { return; }
                done = sliced.Slice.Answer(samples: samples, ticket: sliced.Lattice);
            }
            if (done) {
                sliced.Slice.Complete();
                lock (m_gate) { m_slice = null; }
            }
        }
    }

    // One lattice's decoded samples, owned by its group's answer job and charged to the group's storage owner: each
    // sample's value and material, and either its gradient or, for a points lattice without gradients, the slot that won
    // at the point.
    private sealed class Answer(IAllocator? allocator, MemoryAccount.Owner owner) : IDisposable {
        public readonly OwnedBuffer<float> Values = new(allocator: allocator, observer: owner);
        public readonly OwnedBuffer<int> Materials = new(allocator: allocator, observer: owner);
        public readonly OwnedBuffer<float> Gradients = new(allocator: allocator, observer: owner);
        public readonly OwnedBuffer<int> Slots = new(allocator: allocator, observer: owner);

        public void Dispose() { Values.Dispose(); Materials.Dispose(); Gradients.Dispose(); Slots.Dispose(); }
    }

    // Decodes one lattice's readback records, MeshBakeSampleWords little-endian words each: value, material, then the
    // gradient's three components or the winning slot.
    private Answer Decode(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) {
        var answer = new Answer(allocator: Library.Allocator, owner: group.Memory);

        try {
            var count = request.Samples;
            var hasSlots = (!request.Gradients && !request.Points.IsEmpty);

            answer.Values.Resize(count, clear: false, preserve: false);
            answer.Materials.Resize(count, clear: false, preserve: false);
            if (request.Gradients) { answer.Gradients.Resize((3 * count), clear: false, preserve: false); }
            if (hasSlots) { answer.Slots.Resize(count, clear: false, preserve: false); }

            var values = answer.Values.GetSpan();
            var materials = answer.Materials.GetSpan();
            var gradients = answer.Gradients.GetSpan();
            var slots = answer.Slots.GetSpan();

            for (var index = 0; (index < count); index++) {
                var record = samples.Slice(start: ((index * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                values[index] = BinaryPrimitives.ReadSingleLittleEndian(source: record);
                materials[index] = BinaryPrimitives.ReadInt32LittleEndian(source: record[4..]);
                if (hasSlots) {
                    slots[index] = BinaryPrimitives.ReadInt32LittleEndian(source: record[8..]);
                } else if (request.Gradients) {
                    gradients[(3 * index)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[8..]);
                    gradients[((3 * index) + 1)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[12..]);
                    gradients[((3 * index) + 2)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[16..]);
                }
            }

            return answer;
        } catch {
            answer.Dispose();
            throw;
        }
    }
    // Answers one lattice of a group on a worker: a refining lattice narrows its group's box, a final slab fills its
    // lattice, and a points lattice answers its stage. Each handler works on the group's own storage outside the gate,
    // where only the group's one running job reads it, and takes the gate only to publish the group's next step.
    private void AnswerGroup(Group group, in SdfMeshBakeRequest request, Answer answer) {
        var answered = Stopwatch.GetTimestamp();
        var values = answer.Values.GetSpan();
        var materials = answer.Materials.GetSpan();

        if (!request.Points.IsEmpty) {
            if (group.Final is { } final) {
                AnswerFinal(final: final, gradients: answer.Gradients.GetSpan(), group: group, materials: materials, request: in request, values: values);
            } else if (group.Creases is not null) {
                AnswerCreases(gradients: answer.Gradients.GetSpan(), group: group, request: in request, values: values);
            } else {
                AnswerPoints(group: group, kind: 6, materials: materials, request: in request);
            }
        } else if (request.Slab >= 0) {
            AnswerSlab(group: group, materials: materials, request: in request, values: values);
        } else {
            Refine(group: group, request: in request, values: values);
        }
        Account(since: answered, stage: BakeStage.Answer);
    }

    /// <summary>Describes the bake: its groups, the work it dispatched, and its meshes' triangles, vertices and bytes,
    /// with the wall time from its start to its last mesh.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            var meshes = 0;
            var triangles = 0L;
            var vertices = 0L;
            var bytes = 0L;

            foreach (var group in m_groups) {
                if (group.Mesh is not { } mesh) { continue; }
                meshes++;
                triangles += mesh.TriangleCount;
                vertices += mesh.Positions.Length;
                bytes += ((((((long)mesh.Positions.Length) + mesh.Normals.Length) * 3) * sizeof(float))
                    + (((3L * mesh.TriangleCount) + mesh.TriangleMaterials.Length) * sizeof(uint)));
            }

            return $"[sdf.mesh-bake] residency={Residency} groups={m_groups.Count} meshes={meshes} pending={m_groups.Count(predicate: static group => !group.Done)} dispatches={m_accounts.Dispatches} samples={m_accounts.Samples} discarded={m_accounts.Discarded} taps={m_accounts.Taps} refined={m_accounts.RefinedCells} edges={m_accounts.CutEdges} softened={m_accounts.Softened} merged={m_accounts.Merged} loaded={m_accounts.Loaded} skinned={m_groups.Count(predicate: static group => (group.Mesh is { VertexMembers.IsEmpty: false }))} triangles={triangles} vertices={vertices} bytes={bytes} seconds={(m_reported ? m_seconds : m_clock.Elapsed.TotalSeconds):0.000}";
        }
    }

    // The groups PlanAhead planned, for the program and far distance it planned them at.
    private (SdfProgram Program, float FarDistance, List<Group> Groups)? m_prepared;

    /// <summary>Plans a program's groups (<see cref="SdfMeshBakeGroup.Plan{T}"/>) as soon as a build finishes it, on the
    /// build's thread, at the dynamic transforms it was emitted under, and takes from the library the meshes it holds of
    /// their geometry, renumbering any whose materials moved, so the frame that first draws the program restarts the bake
    /// without planning it or renumbering its meshes. That restart plans again at its own transforms only the groups it
    /// cannot keep from the program before whose box or geometry read a transform (<see cref="SdfMeshBakeGroup.Refresh{T}"/>).
    /// The plan holds for the first restart of that program at that far distance; any other restart plans whole.</summary>
    /// <param name="program">The program the build finished.</param>
    /// <param name="transforms">The dynamic transforms the program was emitted under, which no other thread writes
    /// while this runs.</param>
    /// <param name="farDistance">The far distance of the frames the program was emitted for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="transforms"/> is
    /// <see langword="null"/>.</exception>
    public void PlanAhead(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float farDistance) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        var planned = Stopwatch.GetTimestamp();
        var groups = SdfMeshBakeGroup.Plan(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: farDistance,
            program: program,
            transforms: transforms
        );

        var identity = Identity.Value;

        foreach (var group in groups) {
            if (Library.TryTake(geometry: group.Sealed, identity: identity, materials: group.MaterialOrder, mesh: out var warmed)) { warmed.Release(); }
        }

        lock (m_gate) {
            m_prepared = (program, farDistance, groups);
            m_planMilliseconds = Stopwatch.GetElapsedTime(startingTimestamp: planned).TotalMilliseconds;
        }
    }

    // Puts a group at an index of the list, under the gate.
    private void Replace(int index, Group group) {
        m_allResident = false;
        group.Owner = this;
        m_groups[index] = group;
        WatchSettle(group: group, index: index);
        m_doneThrough = Math.Min(val1: m_doneThrough, val2: index);
    }
    // Starts the bake of a new program: one group per dynamic slot and per run of static instances baked together (one rigid
    // copy), each starting at the coarse lattice over its bounding box. The meshes of the previous groups keep drawing until
    // each new mesh lands. A frame of the same program whose baked sampled set moved resamples instead (Resample).
    private void Restart(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var previous = m_groups.ToDictionary(keySelector: static group => group.Selector);
        var identity = Identity.Value;
        var program = frame.Program;
        var sampled = frame.Sampled.Baked;
        var plan = (((m_prepared is { } prepared) && ReferenceEquals(objA: prepared.Program, objB: program) && (prepared.FarDistance == frame.FarDistance)) ? prepared.Groups : null);

        m_allResident = false;
        m_demandTransforms = frame.DynamicTransforms;
        m_program = program;
        m_sampled = sampled;
        m_groups.Clear();
        m_doneThrough = 0;
        // A slice samples one program and set: a new one takes every lattice again.
        m_slice?.Reset();
        // A successor waits on a group of the previous program; this program's plan is made at the frame's set already.
        foreach (var old in previous.Values) { old.Successor = null; }
        m_successors = 0;

        // A new program takes the plan a build already made off the frame's thread (PlanAhead), with each group this restart
        // cannot keep that read the transforms or a moved field planned again at the frame's; otherwise the whole plan.
        if (plan is not null) {
            _ = SdfMeshBakeGroup.Refresh(
                create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
                farDistance: frame.FarDistance,
                groups: plan,
                program: program,
                sampled: sampled,
                stale: group => !(previous.TryGetValue(key: group.Selector, value: out var old) && Keeps(group: group, identity: identity, old: old)),
                transforms: frame.DynamicTransforms
            );
            m_groups.AddRange(collection: plan);
        } else {
            m_groups.AddRange(collection: SdfMeshBakeGroup.Plan(
                create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
                farDistance: frame.FarDistance,
                program: program,
                sampled: sampled,
                transforms: frame.DynamicTransforms
            ));
        }
        m_prepared = null;
        m_restarts++;
        m_restartGroups += m_groups.Count;

        // A program rebuilt with a group's instructions unchanged keeps that group, its bake and any lattice in flight; only
        // a group whose geometry moved bakes again (Place).
        var started = new PlaceCounts();
        var replaced = (previous.Count != m_groups.Count);
        // The group of this bake baking each geometry, which every later group of that geometry waits on (Follow).
        var leaders = new Dictionary<SdfMeshGeometry, Group>();

        for (var index = 0; (index < m_groups.Count); index++) {
            replaced |= Place(group: m_groups[index], identity: identity, index: index, leaders: leaders, old: previous.GetValueOrDefault(key: m_groups[index].Selector), started: ref started);
        }
        // A previous group this program does not keep is retired: its queued work is dropped (TakeWork).
        var planned = new HashSet<Group>(collection: m_groups, comparer: ReferenceEqualityComparer.Instance);

        foreach (var old in previous.Values) {
            if (!planned.Contains(item: old)) { m_retiredGroups.Add(item: old); old.Retired = true; }
        }
        WatchSettles();
        BeginOrphans(indices: null, started: ref started);
        Prioritize();
        BeginAccounts(cause: "program", fresh: true, instances: program.Instances.Count, restarted: restarted, started: started);
        // The draws name their groups, so they are rebuilt only when a group was replaced, renumbered or dropped; a
        // program change that keeps every group (a palette value) leaves the drawn meshes, and the mesh revision, alone.
        // A replacement taken wholly from cache lands no later bake callback to invalidate these draws.
        if (replaced) {
            m_productRevision++;
        }
        m_restartMilliseconds = Stopwatch.GetElapsedTime(startingTimestamp: restarted).TotalMilliseconds;
    }
    // Plans again, at a frame's baked sampled set, the groups of the same program whose geometry hashed a snapshot the set
    // no longer holds. Only a group whose geometry reads a sampled field can (m_sampledReaders), so the work is the readers',
    // never the whole plan's. A reader still baking an older snapshot keeps baking it, and the newest plan waits as its
    // successor, which the frame after it lands starts (Promote): one bake of a group in flight, the latest snapshot winning.
    private void Resample(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var sampled = frame.Sampled.Baked;
        var identity = Identity.Value;

        m_sampled = sampled;
        m_resamples++;
        // A slice samples every baked handle.
        m_slice?.Reset();

        var stale = new List<Group>();
        var indices = new List<int>();

        foreach (var index in m_sampledReaders) {
            var group = m_groups[index];

            m_resampleGroups++;
            if (group.Geometry.SampledMatches(sampled: sampled)) {
                // The set moved back to the snapshot the group bakes: a newer plan waiting on it is no longer newer.
                DropSuccessor(group: group);
                continue;
            }
            stale.Add(item: group);
            indices.Add(item: index);
        }
        if (stale.Count == 0) { return; }

        var plans = new List<Group>(collection: stale);

        _ = SdfMeshBakeGroup.Refresh(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: frame.FarDistance,
            groups: plans,
            program: m_program!,
            sampled: sampled,
            stale: static _ => false,
            transforms: frame.DynamicTransforms
        );

        var started = new PlaceCounts();
        var replaced = false;
        var leaders = Leaders();

        for (var at = 0; (at < plans.Count); at++) {
            var old = stale[at];
            var plan = plans[at];

            if (ReferenceEquals(objA: old, objB: plan)) { continue; }
            if (!old.Done) {
                if (old.Successor is null) { m_successors++; } else { m_coalesced++; }
                old.Successor = plan;
                continue;
            }
            replaced |= Succeed(identity: identity, index: indices[at], leaders: leaders, old: old, plan: plan, started: ref started);
        }
        BeginOrphans(indices: m_sampledReaders, started: ref started);
        BeginAccounts(cause: "resample", fresh: false, instances: frame.Program.Instances.Count, restarted: restarted, started: started);
        if (replaced) { m_productRevision++; }
    }
    // Starts each successor whose group has landed in that group's place (Resample): the landed mesh draws until the
    // successor's lands.
    private void Promote(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var identity = Identity.Value;
        var started = new PlaceCounts();
        var replaced = false;
        Dictionary<SdfMeshGeometry, Group>? leaders = null;

        foreach (var index in m_sampledReaders) {
            var old = m_groups[index];

            if (!old.Done || (old.Successor is not { } plan)) { continue; }
            m_promoted++;
            replaced |= Succeed(identity: identity, index: index, leaders: (leaders ??= Leaders()), old: old, plan: plan, started: ref started);
        }
        if (leaders is null) { return; }
        BeginOrphans(indices: m_sampledReaders, started: ref started);
        BeginAccounts(cause: "promote", fresh: false, instances: frame.Program.Instances.Count, restarted: restarted, started: started);
        if (replaced) { m_productRevision++; }
    }
    // Puts a reader's newer plan in the place of the reader, which has landed (Place), dropping any successor the reader
    // held, which the plan replaces, and retiring the reader once its place holds another group. The one door a resampled
    // plan reaches the group list by, so the successor count stays the readers' successors.
    private bool Succeed(Group old, Group plan, int index, UInt128 identity, Dictionary<SdfMeshGeometry, Group> leaders, ref PlaceCounts started) {
        DropSuccessor(group: old);

        var replaced = Place(group: plan, identity: identity, index: index, leaders: leaders, old: old, started: ref started);

        if (!ReferenceEquals(objA: m_groups[index], objB: old)) { m_retiredGroups.Add(item: old); old.Retired = true; }
        return replaced;
    }
    private void DropSuccessor(Group group) {
        if (group.Successor is null) { return; }
        group.Successor = null;
        m_successors--;
    }
    // The readers baking a geometry no other reader waits on. A group sharing geometry with a reader hashed the same
    // snapshot, so it is a reader too: the readers alone hold every leader a resampled group can follow.
    private Dictionary<SdfMeshGeometry, Group> Leaders() {
        var leaders = new Dictionary<SdfMeshGeometry, Group>();

        foreach (var index in m_sampledReaders) {
            var group = m_groups[index];

            if (!group.Done && (group.Leader is null) && (group.Replaces is null)) { _ = leaders.TryAdd(key: group.Sealed, value: group); }
        }
        return leaders;
    }

    // What the groups a restart, resample or promotion placed began: a bake, a load from the store, or a wait on another
    // group of the same geometry (Place).
    private struct PlaceCounts {
        public int Baking;
        public int Loading;
        public int Shared;
        // The first groups begun, which the start line names.
        public List<SdfMeshBakeReport.Begun>? Named;

        public void Begun(Group group, bool loading) {
            if (loading) { Loading++; } else { Baking++; }
            if ((Named ??= []).Count < SdfMeshBakeReport.NamedMost) {
                Named.Add(item: new(Selector: group.Selector, Dynamic: group.Dynamic, Place: (group.Dynamic ? group.Slot : group.Instances), Loading: loading));
            }
        }
    }

    // Puts a newly planned group at an index in place of the group `old` the bake held at its selector: `old` itself where
    // the plan keeps it (the same geometry and materials, or a dynamic group's form, Keeps) or renumbers it (Renumbers);
    // otherwise the new group, finished from the library, its store or another group of its geometry when it can be, and
    // baking while `old`'s mesh draws otherwise. Returns whether the drawn product moved. Under the gate; the caller retires
    // `old` when the index no longer holds it.
    private bool Place(Group group, Group? old, int index, UInt128 identity, Dictionary<SdfMeshGeometry, Group> leaders, ref PlaceCounts started) {
        // A group the moved set did not touch stays the group it was, whatever it is doing.
        if ((old is not null) && ReferenceEquals(objA: old, objB: group)) {
            Replace(group: old, index: index);
            if (!old.Done && (old.Leader is null) && (old.Replaces is null)) { _ = leaders.TryAdd(key: old.Sealed, value: old); }
            return false;
        }
        group.Owner = this;
        group.TakeDemandBox();
        if ((old is not null) && Keeps(group: group, identity: identity, old: old)) {
            old.AdoptSampled(planned: group);
            Replace(group: old, index: index);
            if (!old.Done && (old.Leader is null) && (old.Replaces is null)) { _ = leaders.TryAdd(key: old.Sealed, value: old); }
            return false;
        }
        Replace(group: group, index: index);
        // A group outside the bake's selection finishes unbaked, so it neither bakes nor leads a selected group of
        // its geometry to its unsampled result.
        if ((Selection is { } selection) && !selection.Contains(item: group.Selector)) {
            group.Outcome = "not selected";
            group.Done = true;
            return true;
        }
        // Every way below of finishing a group without a bake takes what the library holds under this identity, and
        // a group begun bakes under it, so a later program keeps the group only while the identity stands.
        group.Identity = identity;
        // A dynamic group whose shapes only took other material ids, because a palette registered before them came
        // or went (a body leaving or entering the world), keeps its mesh renumbered by place, as a mesh taken from
        // the library is.
        if (
            (old is not null) &&
            Renumbers(group: group, identity: identity, old: old) &&
            Library.TryTake(geometry: old.Sealed, identity: identity, materials: group.MaterialOrder, mesh: out var renumber)
        ) {
            old.Renumber(materials: group.MaterialOrder, mesh: renumber);
            old.AdoptSampled(planned: group);
            old.ResidentAt = 0;
            Replace(group: old, index: index);
            return true;
        }
        // Geometry the library holds a mesh of, baked at another index or slot, in an earlier program (a body that left
        // the world and came back) or by another residency (a body that crossed from another world), takes that mesh: a
        // static group's is in world space and a dynamic group's in its slot's frame, which its geometry holds.
        if (Library.TryTake(geometry: group.Sealed, identity: identity, materials: group.MaterialOrder, mesh: out var baked)) {
            group.Mesh = baked;
            group.Done = true;
            return true;
        }
        // Geometry an earlier bake found no surface in meshes nothing again.
        if (Library.HoldsEmpty(geometry: group.Sealed, identity: identity)) {
            group.Outcome = "no surface, as baked before";
            group.Done = true;
            return true;
        }
        // A dynamic group of a form the library holds a mesh of, baked with its other slots at another pose (a body
        // crossing from another world mid-stride, or wearing a creation another body wears), takes that skinned mesh
        // and watches its members against the pose the mesh was baked at, so it bakes again only once it settles away
        // from it (Settle).
        if (group.Dynamic && Library.TryTakeForm(form: group.Form, identity: identity, materials: group.MaterialOrder, mesh: out var shaped)) {
            group.Mesh = shaped;
            group.Done = true;
            group.Stored = true;
            group.Watch(baked: shaped);
            return true;
        }

        if ((old is not null) && (old.Mesh is not null) && (old.Dynamic == group.Dynamic)) {
            group.Mesh = old.Mesh.Acquire();
        }
        if (leaders.TryGetValue(key: group.Sealed, value: out var leader)) {
            group.Leader = leader;
            started.Shared++;
            return true;
        }
        leaders.Add(key: group.Sealed, value: group);
        started.Begun(group: group, loading: Begin(group: group));
        return true;
    }
    // Begins each group, of the listed indices or every one, still waiting on a leader the bake has retired: it bakes its
    // geometry itself.
    private void BeginOrphans(IReadOnlyList<int>? indices, ref PlaceCounts started) {
        var count = (indices?.Count ?? m_groups.Count);

        for (var at = 0; (at < count); at++) {
            var group = m_groups[(indices?[at] ?? at)];

            if (group.Done || (group.Leader is not { Retired: true })) { continue; }
            group.Leader = null;
            started.Begun(group: group, loading: Begin(group: group));
        }
    }
    // Opens the accounts and the start line of the work a restart, resample or promotion began, if any: a new program's, or
    // any once the bake before it reported, whose counts and clock start again; otherwise work added to the bake still
    // running, whose done line then covers it. A bake that began nothing and has every group done reports here or never.
    private void BeginAccounts(string cause, bool fresh, int instances, long restarted, PlaceCounts started) {
        var begun = ((started.Baking + started.Loading) + started.Shared);

        if ((begun > 0) && (fresh || m_reported || !m_clock.IsRunning)) {
            m_accounts.Clear();
            m_reported = false;
            m_clock.Restart();
            m_bakeStarted = restarted;
            m_loadsQueued = started.Loading;
            m_loadsDone = 0;
            m_loadFirst = ((started.Loading > 0) ? restarted : 0);
            m_loadLast = 0;
            SdfMeshBakeReport.Start(baking: started.Baking, cause: cause, disk: Library.Disk, dynamic: m_groups.Count(predicate: static group => group.Dynamic), groups: m_groups.Count, identity: Identity, instances: instances, loading: started.Loading, named: started.Named, residency: Residency, residencyId: ResidencyId, shared: started.Shared);
        } else if (begun > 0) {
            m_loadsQueued += started.Loading;
            if ((started.Loading > 0) && (m_loadFirst == 0)) { m_loadFirst = restarted; }
        }
        if (m_clock.IsRunning) { Finish(); }
    }
    // Whether a new program's group stays the group the previous program planned at its selector: the same geometry and
    // materials, or, for a dynamic group, the same form and materials, which are the same shapes in another relative pose,
    // so the skinned mesh drawing (or baking) carries on, rides each member's slot, and bakes again only once the members
    // settle away from the pose it was baked at (Settle). A group the previous program planned under another bake identity
    // holds a product of other code or kernels, so it is never kept.
    private static bool Keeps(Group group, Group old, UInt128 identity) {
        if (old.Failed || (old.Mesh is { Lost: true }) || (old.Identity != identity) || (old.Slot != group.Slot) || !old.MaterialOrder.AsSpan().SequenceEqual(other: group.MaterialOrder)) { return false; }

        return ((old.Sealed == group.Sealed) || (old.Dynamic && group.Dynamic && (old.Form == group.Form) && ((old.Mesh is not null) || !old.Done)));
    }
    // Whether a new program's group is the finished dynamic group the previous program planned at its selector with only
    // its material ids moved: the same form, as many materials, and a mesh drawing, baked under the current identity.
    private static bool Renumbers(Group group, Group old, UInt128 identity) => (
        (old.Identity == identity) &&
        !old.Failed &&
        old.Dynamic &&
        group.Dynamic &&
        (old.Slot == group.Slot) &&
        old.Done &&
        (old.Mesh is not null) &&
        (old.Replaces is null) &&
        (old.Form == group.Form) &&
        (old.MaterialOrder.Length == group.MaterialOrder.Length)
    );
    // Starts a group the library holds no mesh of at the coarse lattice over its box, under the bake's identity. Geometry
    // the library's store kept from an earlier run loads from disk off the frame's thread instead, and bakes only when the
    // store refuses its entry; true when it loads.
    private bool Begin(Group group) {
        group.Allocator = Library.Allocator;
        group.TakeDemandBox();
        // The coarse lattice is a cube about the bounding box's center, so its cells are cubes.
        var center = ((group.Low + group.High) * 0.5f);
        var half = (MathF.Max(x: (group.High.X - group.Low.X), y: MathF.Max(x: (group.High.Y - group.Low.Y), y: (group.High.Z - group.Low.Z))) * 0.5f);

        group.Low = (center - new Vector3(value: half));
        group.High = (center + new Vector3(value: half));
        group.Next = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: CoarseSamples);
        group.Identity = Identity.Value;
        if (!Library.Holds(geometry: group.Sealed, identity: group.Identity)) { return false; }
        group.Deferred = group.Next;
        group.Next = null;
        group.Contouring = true;
        Schedule(group: group, work: () => Load(group: group));
        return true;
    }
    // The lattice over a group's box: `longest` samples along its longest axis and the same spacing along the others,
    // never fewer than `minimum` samples along any. A final lattice's spacing is never finer than MinimumCell, so a small
    // part meshes at the detail a view resolves rather than at its own scale.
    private static SdfMeshBakeRequest Lattice(Group group, int longest, int minimum, bool gradients) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var target = (MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) / (longest - 1));

        if (gradients) { target = MathF.Max(x: target, y: MinimumCell); }

        int Samples(float length) => Math.Clamp(value: (((int)MathF.Ceiling(x: (length / target))) + 1), min: minimum, max: FinalSamples);

        var dims = (X: Samples(length: extent.X), Y: Samples(length: extent.Y), Z: Samples(length: extent.Z));
        var cell = new Vector3(x: (extent.X / (dims.X - 1)), y: (extent.Y / (dims.Y - 1)), z: (extent.Z / (dims.Z - 1)));

        return new SdfMeshBakeRequest(Cell: cell, Dims: dims, Gradients: gradients, Origin: group.Low, Selector: group.Selector) { FrameSlot = (group.Dynamic ? group.Slot : -1), Instances = group.Instances };
    }
    // Whether a sheet thinner than the spacing passes between any two samples of a lattice (SdfSampledLattice.SheetsBetween).
    private static bool SheetBetween(in SdfMeshBakeRequest lattice, ReadOnlySpan<float> values) => Array.Exists(array: SdfSampledLattice.SheetsBetween(cell: lattice.Cell, dims: lattice.Dims, values: values), match: static mask => (mask != 0));
    // Narrows a group's box to the cells the surface can cross: every cell whose corners change sign and every sample
    // whose value is within half the lattice's finest spacing of the surface, widened by one cell along each axis, and
    // every pair of samples neither of which is within that reach that a sheet thinner than the spacing passes between
    // (SdfSampledLattice.SheetsBetween: a plate lying between two sample rows of a lattice narrowed to a flat box, whose
    // long axes are sampled coarsely; kept within half the cell's diagonal instead, the box stays so fat the final
    // lattice steps over the plate). A pair with a sample within reach is left to that sample, so the pair test moves
    // the box only where the distance test loses the sheet. A group with no such cell has no surface. Once the box
    // stops shrinking, the group's next lattice is the final one.
    private void Refine(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<float> values) {
        var (sx, sy, sz) = request.Dims;
        var near = (0.5f * MathF.Min(x: request.Cell.X, y: MathF.Min(x: request.Cell.Y, y: request.Cell.Z)));
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);
        var between = SdfSampledLattice.SheetsBetween(cell: request.Cell, dims: request.Dims, near: near, values: values);
        // A sample of a pair a sheet passes between joins the box with one cell about it: the sheet lies between the
        // pair along the line and within the line's reach of the sample across it, and the cell beyond the pair keeps
        // the sheet off the next lattice's edge, whose outermost pair no line can test. A sample near the surface or
        // beside a sign change can hold the surface anywhere within its cell, so its cell joins and the box widens by one.
        var sheetLow = new Vector3(value: float.PositiveInfinity);
        var sheetHigh = new Vector3(value: float.NegativeInfinity);

        int Index(int x, int y, int z) => (x + (sx * (y + (sy * z))));

        for (var z = 0; (z < sz); z++) {
            for (var y = 0; (y < sy); y++) {
                for (var x = 0; (x < sx); x++) {
                    var value = values[Index(x: x, y: y, z: z)];
                    var point = (request.Origin + (new Vector3(x: x, y: y, z: z) * request.Cell));

                    if (between[Index(x: x, y: y, z: z)] != 0) {
                        sheetLow = Vector3.Min(value1: sheetLow, value2: (point - request.Cell));
                        sheetHigh = Vector3.Max(value1: sheetHigh, value2: (point + request.Cell));
                    }

                    var candidate = (MathF.Abs(x: value) <= near);

                    if (!candidate) {
                        var inside = (value < 0f);

                        candidate = ((((x + 1) < sx) && ((values[Index(x: (x + 1), y: y, z: z)] < 0f) != inside)) ||
                            (((y + 1) < sy) && ((values[Index(x: x, y: (y + 1), z: z)] < 0f) != inside)) ||
                            (((z + 1) < sz) && ((values[Index(x: x, y: y, z: (z + 1))] < 0f) != inside)));
                    }
                    if (!candidate) { continue; }
                    low = Vector3.Min(value1: low, value2: point);
                    high = Vector3.Max(value1: high, value2: (point + request.Cell));
                }
            }
        }

        if (low.X <= high.X) {
            low -= request.Cell;
            high += request.Cell;
        }
        low = Vector3.Min(value1: low, value2: sheetLow);
        high = Vector3.Max(value1: high, value2: sheetHigh);
        var empty = !(low.X <= high.X);

        // An empty result is a fact about the geometry, stored whether or not the group is still planned.
        if (empty) { Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true); }
        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            m_accounts.KindSamples[0] += request.Samples;
            if (empty) {
                group.Outcome = $"no surface after {group.Refinements} refinements";
                group.Next = null;
                group.Done = true;
                group.Mesh = null;
                Finish();
                return;
            }

            var before = (group.High - group.Low);
            var after = (high - low);

            group.Low = low;
            group.High = high;
            group.Refinements++;

            var converged = ((after.X >= (Converged * before.X)) && (after.Y >= (Converged * before.Y)) && (after.Z >= (Converged * before.Z)));

            if (converged || (group.Refinements >= MaxRefinements)) {
                group.Final = ((group.Members.Count > 1)
                    ? UnionFinal(group: group)
                    : OwnFinal(group: group));
                group.Next = SlabOf(group: group);
                return;
            }
            group.Next = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: RefineMinimum);
        }
    }
    // The final lattice of a group of one member (SdfMeshBakeGroup.Members): FinalSamples along its longest axis, no finer
    // than MinimumCell. An instance that declares its own detail (SdfInstanceRange.Detail), static or riding a slot, refines
    // its bound's part of the box toward that spacing wherever the surface can enter a cell, as finely as
    // MaxDetailRefinement allows.
    private static FinalLattice OwnFinal(Group group) {
        var lattice = (Lattice(gradients: true, group: group, longest: FinalSamples, minimum: FinalMinimum) with { Gradients = false });
        var low = Vector3.Max(value1: group.Low, value2: group.DetailLow);
        var high = Vector3.Min(value1: group.High, value2: group.DetailHigh);

        if (!(group.Detail > 0f) || (high.X < low.X) || (high.Y < low.Y) || (high.Z < low.Z)) {
            return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory);
        }

        var coarse = MathF.Max(x: lattice.Cell.X, y: MathF.Max(x: lattice.Cell.Y, y: lattice.Cell.Z));

        return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory) {
            Factor = Math.Clamp(value: ((int)MathF.Ceiling(x: (coarse / group.Detail))), min: 2, max: MaxDetailRefinement),
            Regions = [new SdfLatticeRegion(High: high, Low: low)],
        };
    }
    // The final lattice of a union of members (a static copy's run, a dynamic slot's several instances, or the parts a pooled
    // group declares), which keeps each member at the resolution its own final lattice would give it, in the group's frame
    // and alike for static and dynamic groups: a cubic lattice past the per-axis cap, at the finest member's spacing where that fits
    // UnionSamples and coarser where it does not, never coarser than the capped lattice over the union's box; a member
    // whose own spacing stays finer than that by more than RegionRatio is a region its refinement measures at its own
    // spacing (SdfSampledLattice.Refine). A member's own spacing is its bounding sphere's box at FinalSamples a side.
    private static FinalLattice UnionFinal(Group group) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var longest = MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z));
        var finest = float.PositiveInfinity;

        foreach (var (_, radius, _, _, _) in group.Members) {
            finest = MathF.Min(x: finest, y: MemberCell(radius: radius));
        }

        // One plane along Z holds at most UnionPlaneSamples, and no axis more samples than a slab's first plane can name
        // (SdfMeshBakeBlock.MaxSlabPlanes).
        var cell = MathF.Max(
            x: MathF.Max(
                x: MathF.Min(x: (longest / (FinalSamples - 1)), y: MathF.Max(x: finest, y: MathF.Cbrt(x: (((extent.X * extent.Y) * extent.Z) / UnionSamples)))),
                y: MathF.Sqrt(x: ((extent.X * extent.Y) / UnionPlaneSamples))
            ),
            y: (longest / (SdfMeshBakeBlock.MaxSlabPlanes - 2))
        );

        int Samples(float length) => Math.Max(val1: FinalMinimum, val2: (((int)MathF.Ceiling(x: (length / cell))) + 1));

        var dims = (X: Samples(length: extent.X), Y: Samples(length: extent.Y), Z: Samples(length: extent.Z));

        var spacing = new Vector3(x: (extent.X / (dims.X - 1)), y: (extent.Y / (dims.Y - 1)), z: (extent.Z / (dims.Z - 1)));
        var coarse = MathF.Max(x: spacing.X, y: MathF.Max(x: spacing.Y, y: spacing.Z));
        var regions = new List<SdfLatticeRegion>();
        var target = float.PositiveInfinity;
        var detailed = float.PositiveInfinity;

        foreach (var (_, radius, detail, low, high) in group.Members) {
            var own = ((detail > 0f) ? MathF.Min(x: MemberCell(radius: radius), y: detail) : MemberCell(radius: radius));

            // A member the lattice already resolves refines nothing: a declared detail at or past the coarse spacing (a
            // slab's half thickness over a lattice finer than it) would only spend the refinement's sample budget on
            // surface the coarse contour holds, leaving the thin members that need it the coarse cell.
            if ((detail > 0f) ? (own >= coarse) : ((own * RegionRatio) >= coarse)) { continue; }
            regions.Add(item: new SdfLatticeRegion(
                High: Vector3.Min(value1: group.High, value2: high),
                Low: Vector3.Max(value1: group.Low, value2: low)
            ));
            target = MathF.Min(x: target, y: own);
            if (detail > 0f) { detailed = MathF.Min(x: detailed, y: detail); }
        }

        var lattice = new SdfMeshBakeRequest(Cell: spacing, Dims: dims, Gradients: false, Origin: group.Low, Selector: group.Selector) { FrameSlot = (group.Dynamic ? group.Slot : -1), Instances = group.Instances };

        return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory) {
            Factor = ((regions.Count > 0) ? Math.Clamp(value: ((int)MathF.Ceiling(x: (coarse / target))), min: 2, max: (float.IsFinite(f: detailed) ? MaxDetailRefinement : MaxRefinement)) : 0),
            Regions = [.. regions],
        };
    }
    // The spacing a static member's own final lattice would have: its bounding sphere's box at FinalSamples a side, no finer
    // than MinimumCell.
    private static float MemberCell(float radius) => MathF.Max(x: MinimumCell, y: ((2f * radius) / (FinalSamples - 1)));
    // The next slab of a group's final lattice: whole planes along Z, as many as a frame's share of field evaluations
    // takes, at least one.
    private static SdfMeshBakeRequest SlabOf(Group group) {
        var final = group.Final!;

        var (x, y, z) = final.Lattice.Dims;
        var planes = Math.Clamp(value: (LatticeTaps / (x * y)), min: 1, max: (z - final.Planes));

        return final.Lattice with {
            Dims = (x, y, planes),
            Slab = final.Planes,
        };
    }
    // Answers a slab of a group's final lattice. Once every plane is answered, the lattice chooses the cells it
    // refines on the thread pool (Prepare).
    private void AnswerSlab(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<float> values, ReadOnlySpan<int> materials) {
        var final = group.Final!;
        var start = (request.Slab * (request.Dims.X * request.Dims.Y));

        values.CopyTo(destination: final.Values[start..]);
        materials.CopyTo(destination: final.Materials[start..]);
        final.Planes += request.Dims.Z;

        var complete = (final.Planes >= final.Lattice.Dims.Z);
        var empty = false;

        // A lattice all of one sign whose field holds no sheet between its samples (SdfSampledLattice.SheetsBetween) has
        // no surface; it finishes here, once its last slab lands. One that does refines next, and its fine samples cross
        // the sheet.
        if (complete) {
            var inside = false;
            var outside = false;

            foreach (var value in final.Values) {
                inside |= (value < 0f);
                outside |= (value >= 0f);
            }
            empty = (!(inside && outside) && !SheetBetween(lattice: final.Lattice, values: final.Values));
            if (empty) { Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true); }
        }

        FinalLattice? emptied = null;

        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            m_accounts.KindSamples[1] += request.Samples;
            if (!complete) {
                group.Next = SlabOf(group: group);
                return;
            }
            if (empty) {
                group.Outcome = "no crossed cell in the final lattice";
                emptied = group.Final;
                group.Final = null;
                group.Next = null;
                group.Done = true;
                group.Mesh = null;
                Finish();
            } else {
                group.Next = null;
                group.Contouring = true;
                Schedule(group: group, work: () => Prepare(final: final, group: group));
            }
        }
        emptied?.Dispose();
    }
    // Builds a group's sampled lattice from its final lattice and chooses the cells it refines: their fine samples are
    // measured next, or, with none, the gradients the contour reads.
    private void Prepare(Group group, FinalLattice final) {
        ReadOnlyMemory<Vector4> points = default;
        string? refusal = null;
        var started = Stopwatch.GetTimestamp();

        try {
            var lattice = final.Lattice;

            final.Sampled = new SdfSampledLattice(
                allocator: group.Allocator,
                observer: group.Memory,
                cell: lattice.Cell,
                materials: final.MaterialMemory,
                origin: lattice.Origin,
                samplesX: lattice.Dims.X,
                samplesY: lattice.Dims.Y,
                samplesZ: lattice.Dims.Z,
                values: final.ValueMemory
            );

            var factor = FactorOf(final: final);

            if (factor >= 2) {
                points = final.Sampled.Refine(factor: factor, regions: final.Regions);
            }
            if (points.Length == 0) {
                points = ChooseCrossingPoints(final: final);
            } else {
                final.Stage = FinalStage.Refinement;
            }
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            refusal = exception.Message;
        }
        Account(detail: new(Count: points.Length, Dims: final.Lattice.Dims, Factor: final.Sampled?.Refinement, Measured: final.Sampled?.MeasuredCells), group: group, since: started, stage: BakeStage.Prepare);

        var empty = ((refusal is not null) || (points.Length == 0));

        if (empty) {
            if (refusal is not null) { SdfMeshBakeReport.Refused(refusal: refusal, selector: group.Selector); } else { Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true); }
        } else {
            // The refinement now names its fine samples, so the admission narrows from their bound to their count.
            _ = group.Memory.TryReserve(peak: PeakOf(final: final, fineSamples: ((final.Stage == FinalStage.Refinement) ? points.Length : 0)));
            final.Points = points;
        }

        FinalLattice? emptied = null;

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            if (empty) {
                group.Outcome = ((refusal is null) ? "no crossed cell in the final lattice" : "lattice refused");
                emptied = group.Final;
                group.Final = null;
                group.Next = null;
                group.Done = true;
                group.Mesh = null;
                Finish();
            } else {
                final.Answered = 0;
                group.Next = FinalChunk(final: final, group: group);
            }
        }
        emptied?.Dispose();
    }
    // The fine cells along each axis a final lattice's coarse cell refines into at most: a union's own factor, or as many
    // as reach FineCell, at most MaxRefinement.
    private static int FactorOf(FinalLattice final) {
        var cell = final.Lattice.Cell;

        return ((final.Factor > 0)
            ? final.Factor
            : Math.Min(val1: MaxRefinement, val2: ((int)MathF.Ceiling(x: (MathF.Min(x: cell.X, y: MathF.Min(x: cell.Y, y: cell.Z)) / FineCell)))));
    }

    // The workspace bytes a final lattice's bake holds at once, per lattice sample and per fine sample: the lattice's
    // values, materials and gradients, its per-cell refinement state and contour, the fine samples' keys, values,
    // materials, slots and points, and the crossing and gradient rounds' points. An upper estimate of the sum of those
    // buffers, so an admitted group's charges stay within its reservation.
    private const long WorkspaceBytesPerSample = 96;

    // A final lattice's simultaneous peak: its own samples, and the fine samples its refinement measures, at most the
    // bound its spacing and factor allow (SdfSampledLattice.FineSampleBound) until the refinement names them.
    private static long PeakOf(FinalLattice final, int? fineSamples = null) {
        var (x, y, z) = final.Lattice.Dims;
        var fine = (fineSamples ?? SdfSampledLattice.FineSampleBound(factor: FactorOf(final: final), samplesX: x, samplesY: y, samplesZ: z));

        return checked(((((long)final.Lattice.Samples) + fine) * WorkspaceBytesPerSample));
    }
    // The next chunk of a group's fine, crossing-root or gradient samples, as many as a frame's share of field evaluations
    // takes. Gradient taps are spaced by the contour's finest cell.
    private static SdfMeshBakeRequest FinalChunk(Group group, FinalLattice final) {
        var gradients = (final.Stage == FinalStage.Gradients);
        var count = Math.Clamp(value: (LatticeTaps / (gradients ? 5 : 1)), min: 1, max: (final.Points.Length - final.Answered));

        return new SdfMeshBakeRequest(Cell: final.Sampled!.ContourCell, Dims: (count, 1, 1), Gradients: gradients, Origin: Vector3.Zero, Selector: group.Selector) {
            FrameSlot = (group.Dynamic ? group.Slot : -1),
            Instances = group.Instances,
            Points = final.Points.Slice(start: final.Answered, length: count),
        };
    }
    // Answers fine samples, a round of edge-root values, or gradients. The next crossing round and the final gradients
    // are chosen on the thread pool; once every gradient is answered, the group contours there.
    private void AnswerFinal(Group group, FinalLattice final, in SdfMeshBakeRequest request, ReadOnlySpan<float> values, ReadOnlySpan<int> materials, ReadOnlySpan<float> gradients) {
        var sampled = final.Sampled!;

        if (final.Stage == FinalStage.Refinement) {
            sampled.AnswerRefinement(first: final.Answered, materials: materials, values: values);
        } else if (final.Stage == FinalStage.Crossings) {
            sampled.AnswerCrossings(first: final.Answered, values: values);
        } else {
            sampled.AnswerGradients(first: final.Answered, gradients: gradients!, values: values);
        }
        final.Answered += request.Samples;

        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            m_accounts.KindSamples[(2 + ((int)final.Stage))] += request.Samples;
            if (final.Answered < final.Points.Length) {
                group.Next = FinalChunk(final: final, group: group);
                return;
            }

            group.Next = null;
            group.Contouring = true;
            if (final.Stage != FinalStage.Gradients) {
                Schedule(group: group, work: () => ChooseCrossings(final: final, group: group));
                return;
            }

            Schedule(group: group, work: () => Contour(group: group, lattice: sampled));
        }
    }
    // Locates zeroes along the lattice edges before measuring their tangent planes. An off-edge normal projection of a
    // linear estimate can instead land on a different face of a corner, supplying the contour with the wrong plane.
    private static ReadOnlyMemory<Vector4> ChooseCrossingPoints(FinalLattice final) {
        var points = final.Sampled!.CrossingPoints();

        final.Stage = ((points.Length > 0) ? FinalStage.Crossings : FinalStage.Gradients);
        return ((points.Length > 0) ? points : final.Sampled.GradientPoints());
    }
    private void ChooseCrossings(Group group, FinalLattice final) {
        var refined = (final.Stage == FinalStage.Refinement);
        var started = Stopwatch.GetTimestamp();
        var points = ChooseCrossingPoints(final: final);

        Account(detail: new(Count: points.Length, Dims: final.Lattice.Dims, Factor: final.Sampled!.Refinement, Measured: final.Sampled.MeasuredCells), group: group, since: started, stage: BakeStage.Crossings);
        final.Points = points;

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            final.Answered = 0;
            if (refined) { m_accounts.RefinedCells += final.Sampled!.MeasuredCells; }
            group.Next = FinalChunk(final: final, group: group);
        }
    }
    // Contours a group's final lattice, then queues the first round of points its materials are sampled at
    // (SdfMeshMaterialCut).
    private void Contour(Group group, SdfSampledLattice lattice) {
        // The running stage owns the final lattice and frees it once it returns; the group gives it up under the gate,
        // with the next stage published, so the answer dispatcher selects that stage.
        using var final = group.Final;

        group.DiagnosticCell = lattice.ContourCell;
        SdfContour? contour = null;
        var started = Stopwatch.GetTimestamp();

        try {
            contour = lattice.Contour();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            SdfMeshBakeReport.Refused(refusal: exception.Message, selector: group.Selector);
        }
        Account(detail: new(Count: contour?.QuadCount, Factor: lattice.Refinement, Measured: lattice.MeasuredCells), group: group, since: started, stage: BakeStage.Contour);

        var empty = ((contour is null) || (contour.QuadCount == 0));
        int[] creases = [];

        if (empty) {
            if (contour is not null) { Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true); }
        } else if (contour is not null) {
            creases = CreaseVertices(contour: contour);
            if (creases.Length > 0) {
                group.Projection = new ContourProjection(contour: contour);
                group.CreaseSpacing = lattice.ContourCell;
                group.CreaseSlope = lattice.SurfaceSlope();
                group.Points.Resize(creases.Length, clear: false, preserve: false);
                for (var index = 0; (index < creases.Length); index++) {
                    var vertex = creases[index];

                    group.Points[index] = new Vector4(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)], w: 1f);
                }
            }
        }

        lock (m_gate) {
            group.Contouring = false;
            group.Final = null;
            if (empty) {
                group.Outcome = ((contour is null) ? "contour refused" : "contour empty");
                group.Done = true;
                group.Mesh = null;
                Finish();
                return;
            }
            group.Contour = contour;
            if (creases.Length == 0) {
                group.Contouring = true;
                Schedule(group: group, work: () => Split(group: group));
                return;
            }
            group.Creases = creases;
            group.PointsAnswered = 0;
            group.Next = CreaseChunk(group: group);
        }
    }
    // The vertices a contour's field check measures (AnswerCreases): every vertex no crossing pins to the surface.
    private static int[] CreaseVertices(SdfContour contour) {
        var found = new List<int>();

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            if (((vertex < contour.Features.Length) && contour.Features[vertex]) || ((vertex < contour.Solved.Length) && contour.Solved[vertex])) { found.Add(item: vertex); }
        }

        return [.. found];
    }
    // The next chunk of the vertices a group's field check measures, as many as a frame's share of field evaluations takes.
    private static SdfMeshBakeRequest CreaseChunk(Group group) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: (LatticeTaps / 5));

        return new SdfMeshBakeRequest(Cell: group.CreaseSpacing, Dims: (count, 1, 1), Gradients: true, Origin: Vector3.Zero, Selector: group.Selector) {
            FrameSlot = (group.Dynamic ? group.Slot : -1),
            Instances = group.Instances,
            Points = group.Points.Memory.Slice(group.PointsAnswered, count),
        };
    }
    // Answers a chunk of a group's field check with the field's value and gradient at each vertex no crossing pins to the
    // surface (SdfContour.Features and SdfContour.Solved): a vertex its crossings' tangent planes placed, and a refined
    // cell's merged vertex. The planes meet on the surface only at a sharp crease: where an edge is rounded by a fillet,
    // chamfer or rounding near the cell's size (a smooth blend's), a cell whose crossings all lie on the two faces puts its
    // vertex at the faces' meeting line, proud of the rounding, while a neighbour with a crossing on the rounding puts its
    // own there, so the rim steps in and out from cell to cell and its flat faces dent where the vertices beside it split.
    // A vertex the field puts farther off the surface than CreaseTolerance of the contour's cell moves onto the surface
    // along the gradient, takes the gradient for its normal and is no crease, except one the contour put inside its cell
    // beside a sharp convex crease past it (SdfContour.Beside) that the field puts inside the solid: moved onto the face
    // nearer it, its quads still turn across the crease, and as no crease its face's normal would shade the other face's
    // quads up to 90 degrees off, where a vertex proud of a rounding lies outside. A vertex whose gradient reads shorter
    // than KinkGradient of the field's typical slope sits on a crease the gradient's taps straddle and stays, and so does
    // one whose gradient turns away from the way its own triangles face: inside a union, the field of a layer thinner than
    // a cell (a slab's ceiling finish, a tread on its riser) measures to the face it shares with its neighbour, so its
    // gradient points at that seam, and the move would lift the vertex off the surface onto it. Once every vertex is answered, the
    // contour splits at the creases that remain on the thread pool. Every movement is clipped before an incident
    // triangle reverses its original facing; an incomplete projection keeps its original crease and shading normal.
    private void AnswerCreases(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<float> values, ReadOnlySpan<float> gradients) {
        var contour = group.Contour!;
        var tolerance = (CreaseTolerance * MathF.Min(x: group.CreaseSpacing.X, y: MathF.Min(x: group.CreaseSpacing.Y, y: group.CreaseSpacing.Z)));
        var softened = 0;

        for (var index = 0; (index < request.Samples); index++) {
            var vertex = group.Creases![(group.PointsAnswered + index)];
            var gradient = new Vector3(x: gradients[(3 * index)], y: gradients[((3 * index) + 1)], z: gradients[((3 * index) + 2)]);

            softened += (ProjectCrease(contour: contour, gradient: gradient, projection: group.Projection!, slope: group.CreaseSlope, tolerance: tolerance, value: values[index], vertex: vertex) ? 1 : 0);
        }
        group.PointsAnswered += request.Samples;

        var more = (group.PointsAnswered < group.Points!.Length);

        if (!more) {
            group.Creases = null;
            group.Projection = null;
            group.Points.Resize(0);
        }
        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            m_accounts.KindSamples[5] += request.Samples;
            m_accounts.Softened += softened;
            if (more) {
                group.Next = CreaseChunk(group: group);
                return;
            }
            group.Next = null;
            group.Contouring = true;
            Schedule(group: group, work: () => Split(group: group));
        }
    }
    // Moves one vertex no crossing pins onto the field's surface by its value and gradient there, as AnswerCreases
    // describes; whether it moved.
    private static bool ProjectCrease(SdfContour contour, ContourProjection projection, int vertex, float value, Vector3 gradient, float tolerance, float slope) {
        var length = gradient.Length();

        if (!(length > (KinkGradient * slope))) { return false; }

        var distance = (value / length);

        if (!(MathF.Abs(x: distance) > tolerance)) { return false; }
        gradient /= length;
        if (!(Vector3.Dot(vector1: gradient, vector2: projection.Outward(vertex: vertex)) > 0f)) { return false; }
        var cut = ((vertex < contour.Beside.Length) && contour.Beside[vertex] && (distance < 0f));
        var point = projection.Move(amount: out var amount, contour: contour, displacement: (-distance * gradient), vertex: vertex);

        if (!(amount > 0f)) { return false; }
        contour.Positions[(3 * vertex)] = point.X;
        contour.Positions[((3 * vertex) + 1)] = point.Y;
        contour.Positions[((3 * vertex) + 2)] = point.Z;
        if ((MathF.Abs(x: distance) * (1f - amount)) <= tolerance) {
            if ((vertex < contour.Features.Length) && !cut) { contour.Features[vertex] = false; }
            contour.Normals[(3 * vertex)] = gradient.X;
            contour.Normals[((3 * vertex) + 1)] = gradient.Y;
            contour.Normals[((3 * vertex) + 2)] = gradient.Z;
        }

        return true;
    }
    // Splits a group's contour at its creases, then queues the first round of points its materials are sampled at
    // (SdfMeshMaterialCut).
    private void Split(Group group) {
        var started = Stopwatch.GetTimestamp();

        group.Contour!.SelectDiagonals();
        var contour = group.Contour!.SplitAtCreases();
        var cut = new SdfMeshMaterialCut(contour: contour);
        var points = cut.Points();

        Account(group: group, since: started, stage: BakeStage.Split);
        group.Contour = contour;
        group.Cut = cut;
        StagePoints(group: group, points: points);
        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            group.Next = PointChunk(group: group);
        }
    }
    // Fills a group's point round and the storage its answers land in, on the worker running the group's stage: no
    // request names the group's points until the gate publishes the round's first chunk.
    private static void StagePoints(Group group, ReadOnlySpan<Vector4> points) {
        group.Points.CopyFrom(source: points);
        group.PointMaterials.Resize(points.Length, clear: false, preserve: false);
        group.PointsAnswered = 0;
    }
    // The next chunk of a group's material points, at most a frame's share of field evaluations.
    private static SdfMeshBakeRequest PointChunk(Group group) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: LatticeTaps);

        return new SdfMeshBakeRequest(Cell: Vector3.One, Dims: (count, 1, 1), Gradients: false, Origin: Vector3.Zero, Selector: group.Selector) {
            FrameSlot = (group.Dynamic ? group.Slot : -1),
            Instances = group.Instances,
            Points = group.Points.Memory.Slice(group.PointsAnswered, count),
        };
    }
    // Answers a chunk of a group's material points; once a round's every point is answered, its material cut takes them
    // on the thread pool (Advance).
    private void AnswerPoints(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<int> materials, int kind) {
        materials[..request.Samples].CopyTo(destination: group.PointMaterials.GetSpan()[group.PointsAnswered..]);
        group.PointsAnswered += request.Samples;
        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            m_accounts.KindSamples[kind] += request.Samples;
            if (group.PointsAnswered < group.Points!.Length) {
                group.Next = PointChunk(group: group);
                return;
            }

            group.Next = null;
            group.Contouring = true;
            Schedule(group: group, work: () => Advance(group: group));
        }
    }
    // Hands a round's materials to the group's cut, then queues its next round, or meshes the group once none is left.
    private void Advance(Group group) {
        var cut = group.Cut!;
        var started = Stopwatch.GetTimestamp();

        cut.Answer(materials: group.PointMaterials.GetSpan());

        var points = cut.Points();

        Account(group: group, since: started, stage: BakeStage.Cut);

        if (points.Length == 0) {
            Build(group: group);
            return;
        }

        StagePoints(group: group, points: points);
        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            group.Next = PointChunk(group: group);
        }
    }
    // Builds a group's mesh from its contour, cut along its material boundaries.
    private void Build(Group group) {
        SdfMesh? mesh = null;
        var started = Stopwatch.GetTimestamp();

        try {
            mesh = group.Cut!.Build();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            SdfMeshBakeReport.Refused(refusal: exception.Message, selector: group.Selector);
        }
        Account(group: group, since: started, stage: BakeStage.Cut);

        var edges = group.Cut!.Edges;

        group.Contour = null;
        group.Creases = null;
        group.Cut = null;
        // A group whose shapes ride slots besides its own asks which shape wins at each vertex, so each vertex follows
        // its own shape's slot as it moves (SdfMesh.Members).
        var skinned = ((mesh is not null) && group.Dynamic && group.Geometry.Poses.Keys.Any(predicate: slot => (slot != group.Slot)));

        if (skinned) {
            mesh = SdfMeshSkin.Build(mesh!, m_program!, group, out var refusal);
            if (mesh is null) {
                SdfMeshBakeReport.SkinSplit(group.Selector, group.Sealed, refusal);
                mesh = SdfMeshSkin.Split(m_program!, group);
            }
        }
        lock (m_gate) { m_accounts.CutEdges += edges; }
        Land(group: group, mesh: mesh);
    }
    // Lands a group's finished mesh, its coplanar triangles merged into the polygons they tile (SdfMeshPlanarMerge) and its
    // coarser levels collapsed from those (SdfMeshCollapse): in the library, which keeps it for another program or
    // residency and its store, and as the group's drawn mesh.
    private void Land(Group group, SdfMesh? mesh) {
        var merged = 0;

        if (mesh is not null) {
            var contoured = mesh.TriangleCount;
            var started = Stopwatch.GetTimestamp();

            mesh = mesh with { BakeCell = group.DiagnosticCell };
            mesh = ((!group.Dynamic && !group.Glass && ((group.Instances > 1) || (mesh.TriangleCount >= SdfMeshChunks.WholeTriangles)))
                ? SdfMeshChunks.Bake(mesh, preserveFeatures: group.Geometry.ContainsGlyph)
                : SdfMeshChunks.Merge(levels: !group.Glass, mesh: mesh, preserveFeatures: group.Geometry.ContainsGlyph));
            merged = (contoured - mesh.TriangleCount);
            Account(detail: new(Count: contoured, Kept: mesh.TriangleCount), group: group, since: started, stage: BakeStage.Land);
            started = Stopwatch.GetTimestamp();
            mesh = Library.Store(form: (group.Dynamic ? group.Form : null), geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: mesh);
            Account(since: started, stage: BakeStage.Store);
        }

        lock (m_gate) {
            m_accounts.Merged += merged;
            group.Contouring = false;
            group.Done = true;
            Draw(group: group, mesh: mesh);
            group.Outcome = ((mesh is null) ? "mesh refused" : null);

            group.Points.Resize(0);
            group.PointMaterials.Resize(0);
            Finish();
        }
    }
    // Takes a group's mesh from the library's store; a group whose entry the store refuses or no longer holds bakes from
    // its coarse lattice.
    private void Load(Group group) {
        var loaded = Library.TryLoad(form: (group.Dynamic ? group.Form : null), geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var mesh, refusal: out var refusal);

        lock (m_gate) {
            group.Contouring = false;
            m_loadsDone++;
            m_loadLast = Stopwatch.GetTimestamp();
            if (refusal is not null) {
                SdfMeshBakeReport.StoreRefused(refusal: refusal, selector: group.Selector);
            }
            if (!loaded) {
                group.Next = group.Deferred;
                return;
            }
            m_accounts.Loaded++;
            group.Done = true;
            group.Stored = true;
            if (mesh is null) { group.Outcome = "no surface, as stored before"; }
            Draw(group: group, mesh: mesh);
            Finish();
        }
    }
    // Makes a group's new mesh the one it draws. A group rebaking after an edit keeps drawing its previous mesh until this
    // one lands, so the count of drawn meshes alone does not move: the product revision changes so the dresser lists the new one.
    private void Draw(Group group, SdfMesh? mesh) {
        group.Mesh = mesh;
        group.ResidentAt = 0;
        m_allResident = false;
        _ = m_retiredGroups.RemoveAll(match: static retired => retired.Released);
    }
    // Records the bake's wall time once its last group finishes, and reports it.
    private void Finish() {
        Follow();
        foreach (var group in m_groups) {
            if (!group.Done) { return; }
        }
        if (m_reported) { return; }
        m_seconds = m_clock.Elapsed.TotalSeconds;
        m_reported = true;
        SdfMeshBakeReport.Done(accounts: m_accounts, dispatches: DispatchReport, groups: [.. m_groups.Select(selector: static group => new SdfMeshBakeReport.Finished(Mesh: group.Mesh, Outcome: group.Outcome, Selector: group.Selector))], residency: Residency, residencyId: ResidencyId, seconds: m_seconds);
    }

    // How far off the surface, in the contour's finest cells, an unpinned vertex may lie and stay where the contour put it
    // (AnswerCreases): past the field's noise at a sharp crease, short of where a rounding near a cell's size leaves its
    // faces' meeting line.
    private const float CreaseTolerance = 0.02f;
    // The shortest field gradient at a vertex, as a share of the field's typical slope across its surface
    // (SdfSampledLattice.SurfaceSlope), that gives AnswerCreases a direction and distance to the surface. Gradient taps
    // straddling a sharp crease the vertex lies on read the two faces' gradients cancelling, a few hundredths of the
    // slope, and the value over that length puts the surface many cells off. A field that is no true distance reads well
    // under its slope in places (a fifth of the distance on the Moth's shins), whose vertices still move.
    private const float KinkGradient = 0.1f;
    // The contour's initial triangle facings and vertex-to-triangle rows, retained only during field projection.
    // A triangle's area normal is affine in one moving vertex, so its dot with this fixed reference clips a proposed
    // displacement exactly before the triangle reverses. Fixed references preserve orientation across all sequential
    // vertex moves; comparing only against each step's new normal would permit a cumulative reversal.
    private sealed class ContourProjection {
        private readonly int[] m_starts;
        private readonly int[] m_incident;
        private readonly Triangle[] m_triangles;

        private readonly record struct Triangle(int A, int B, int C, (double X, double Y, double Z) Reference);

        public ContourProjection(SdfContour contour) {
            var triangles = new List<Triangle>(capacity: (2 * contour.QuadCount));

            m_starts = new int[(contour.VertexCount + 1)];

            void Add(int a, int b, int c) {
                if ((a == b) || (a == c) || (b == c)) { return; }
                var pa = At(contour: contour, vertex: a);
                var pb = At(contour: contour, vertex: b);
                var pc = At(contour: contour, vertex: c);
                var normal = Area(a: pa, b: pb, c: pc);
                var longest = Math.Max(val1: Vector3.DistanceSquared(value1: pa, value2: pb), val2: Math.Max(val1: Vector3.DistanceSquared(value1: pa, value2: pc), val2: Vector3.DistanceSquared(value1: pb, value2: pc)));

                // A collinear triangle has no orientation to retain. Ignoring it lets a neighbouring move open it
                // into a valid triangle instead of making one degenerate face immobilize the whole fan.
                if (!(Dot(a: normal, b: normal) > ((1e-12 * longest) * longest))) { return; }
                triangles.Add(item: new Triangle(A: a, B: b, C: c, Reference: normal));
                m_starts[(a + 1)]++;
                m_starts[(b + 1)]++;
                m_starts[(c + 1)]++;
            }

            for (var quad = 0; (quad < contour.QuadCount); quad++) {
                var at = (4 * quad);

                var (a, b, c, d) = (contour.Quads[at], contour.Quads[(at + 1)], contour.Quads[(at + 2)], contour.Quads[(at + 3)]);

                Add(a: a, b: b, c: c);
                Add(a: a, b: c, c: d);
            }
            for (var vertex = 0; (vertex < contour.VertexCount); vertex++) { m_starts[(vertex + 1)] += m_starts[vertex]; }
            m_triangles = [.. triangles];
            m_incident = new int[m_starts[^1]];
            var filled = new int[contour.VertexCount];

            for (var index = 0; (index < m_triangles.Length); index++) {
                var triangle = m_triangles[index];

                foreach (var vertex in ((ReadOnlySpan<int>)[triangle.A, triangle.B, triangle.C])) {
                    m_incident[(m_starts[vertex] + filled[vertex]++)] = index;
                }
            }
        }

        // The way a vertex's own surface faces: the sum of its triangles' original area normals.
        public Vector3 Outward(int vertex) {
            var (x, y, z) = (0.0, 0.0, 0.0);

            for (var at = m_starts[vertex]; (at < m_starts[(vertex + 1)]); at++) {
                var reference = m_triangles[m_incident[at]].Reference;

                (x, y, z) = ((x + reference.X), (y + reference.Y), (z + reference.Z));
            }

            return new Vector3(x: ((float)x), y: ((float)y), z: ((float)z));
        }
        public Vector3 Move(SdfContour contour, int vertex, Vector3 displacement, out float amount) {
            var original = At(contour: contour, vertex: vertex);
            var target = (original + displacement);

            amount = 0f;
            if ((target == original) || !float.IsFinite(f: target.X) || !float.IsFinite(f: target.Y) || !float.IsFinite(f: target.Z)) { return original; }
            var limit = 1.0;

            for (var at = m_starts[vertex]; (at < m_starts[(vertex + 1)]); at++) {
                var triangle = m_triangles[m_incident[at]];
                var before = Facing(contour: contour, point: original, triangle: triangle, vertex: vertex);
                var after = Facing(contour: contour, point: target, triangle: triangle, vertex: vertex);
                var floor = Math.Min(val1: (Dot(a: triangle.Reference, b: triangle.Reference) * 1e-5), val2: (before * 0.5));

                if ((after < floor) && (after < before)) {
                    limit = Math.Min(val1: limit, val2: (0.99 * Math.Max(val1: 0.0, val2: ((before - floor) / (before - after)))));
                }
            }

            // Positions are ultimately floats. Check the actual rounded point too, backing off when a very narrow
            // triangle loses the analytic margin to coordinate rounding.
            for (var attempt = 0; ((attempt < 8) && (limit > 0.0)); attempt++, limit *= 0.5) {
                var point = ((limit >= 1.0) ? target : Vector3.Lerp(amount: ((float)limit), value1: original, value2: target));
                var valid = (point != original);

                for (var at = m_starts[vertex]; (valid && (at < m_starts[(vertex + 1)])); at++) {
                    var triangle = m_triangles[m_incident[at]];

                    valid = (Facing(contour: contour, point: point, triangle: triangle, vertex: vertex) > 0.0);
                }
                if (valid) {
                    amount = ((float)limit);
                    return point;
                }
            }

            return original;
        }

        private static Vector3 At(SdfContour contour, int vertex) => new(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)]);
        private static double Facing(SdfContour contour, Triangle triangle, int vertex, Vector3 point) => Dot(a: triangle.Reference, b: Area(
            a: ((triangle.A == vertex) ? point : At(contour: contour, vertex: triangle.A)),
            b: ((triangle.B == vertex) ? point : At(contour: contour, vertex: triangle.B)),
            c: ((triangle.C == vertex) ? point : At(contour: contour, vertex: triangle.C))));
        private static (double X, double Y, double Z) Area(Vector3 a, Vector3 b, Vector3 c) {
            var (ux, uy, uz) = ((((double)b.X) - a.X), (((double)b.Y) - a.Y), (((double)b.Z) - a.Z));
            var (vx, vy, vz) = ((((double)c.X) - a.X), (((double)c.Y) - a.Y), (((double)c.Z) - a.Z));

            return (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        }
        private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
    }
    // One bake group as the plan formed it (SdfMeshBakeGroup), and where its bake stands.
    private sealed class Group(int selector, bool dynamic) : SdfMeshBakeGroup(dynamic: dynamic, selector: selector) {
        // The mesh whose vertices' winning slots the skin round is asking for, until it lands.
        
        public bool Contouring { get; set; }

        private bool m_done;
        private bool m_inFlight;

        public IAllocator? Allocator { get; set; }
        public bool Done { get => m_done; set { m_done = value; if (value) { Memory.ReleaseReservation(); } TryRelease(); } }
        public bool InFlight { get => m_inFlight; set { m_inFlight = value; TryRelease(); } }

        public MemoryAccount.Owner Memory { get; } = MemoryAccount.Shared.CreateOwner("mesh-bake-group");

        private SdfMesh? m_mesh;

        // The bake whose group list holds the group, set by Restart and Replace, the list's only writers. Every change of
        // the mesh a listed group draws moves that bake's product revision here, the one door, so the dresser can trust
        // the revision: a group no list holds yet draws nothing.
        public SdfMeshPathBake? Owner { get; set; }

        public SdfMesh? Mesh {
            get => m_mesh;
            set {
                var old = m_mesh;

                m_mesh = (Retired ? null : value);
                if (Retired) { value?.Release(); }
                if (!ReferenceEquals(objA: old, objB: m_mesh) && (Owner is { } owner)) { owner.m_productRevision++; }
                old?.Release();
            }
        }
        private bool? m_readsOtherSlots;

        // Whether the group's geometry reads a slot besides its own: a skinned group, whose members drift (Settle).
        public bool ReadsOtherSlots => (m_readsOtherSlots ??= (Dynamic && Geometry.Poses.Keys.Any(predicate: slot => (slot != Slot))));
        public SdfMeshBakeRequest? Next { get; set; }
        public int Refinements { get; set; }
        // The final lattice's contour, its material cut, the points the cut's round samples, and their answers so far.
        public SdfContour? Contour { get; set; }
        public SdfMeshMaterialCut? Cut { get; set; }
        // While the contour's unpinned vertices are checked against the field, the vertices, the spacing their
        // gradient taps scale by, and the field's typical slope across its surface (SdfSampledLattice.SurfaceSlope).
        public int[]? Creases { get; set; }
        public ContourProjection? Projection { get; set; }

        public Vector3 CreaseSpacing { get; set; } = Vector3.One;

        public Vector3 DiagnosticCell { get; set; }

        public float CreaseSlope { get; set; } = 1f;

        private OwnedBuffer<Vector4>? m_points;

        public OwnedBuffer<Vector4> Points => m_points ??= new(allocator: Allocator, observer: Memory);

        private OwnedBuffer<int>? m_pointMaterials;

        public OwnedBuffer<int> PointMaterials => m_pointMaterials ??= new(allocator: Allocator, observer: Memory);
        public int PointsAnswered { get; set; }


        // The final lattice while its slabs and corner gradients are sampled.
        public FinalLattice? Final { get; set; }
        // Why a finished group has no mesh, or null.
        public string? Outcome { get; set; }
        // The identity of the bake the group started under, and the coarse lattice it bakes from when its load fails.
        public UInt128 Identity { get; set; }
        public SdfMeshBakeRequest? Deferred { get; set; }
        // Whether the mesh came from the library or its store rather than a bake of this group.
        public bool Stored { get; set; }
        // Whether the group has reserved its final lattice and result peak.
        public bool Admitted { get; set; }
        public bool DeferredReported { get; set; }
        // Whether a stage of the group threw (Fail), so no later program keeps it.
        public bool Failed { get; set; }
        // Whether the bake no longer plans the group (a later program replaced it, or its residency was released): the
        // pool drops its queued work. Written under the bake's gate, read under the pool's.
        public bool Retired {
            get => Volatile.Read(location: ref m_retired);
            set { Volatile.Write(location: ref m_retired, value: value); if (value) { Memory.Classify(category: MemoryCategory.Retirement); } TryRelease(); }
        }

        private bool m_retired;

        private readonly Lock m_lifecycle = new();

        private int m_jobs;
        private bool m_workspaceReleased;
        private bool m_released;

        public bool Released => Volatile.Read(location: ref m_released);

        public bool QueueWork() {
            lock (m_lifecycle) { if (Retired) { return false; } m_jobs++; return true; }
        }
        public void EndWork() {
            lock (m_lifecycle) { m_jobs--; TryRelease(); }
        }

        private void TryRelease() {
            lock (m_lifecycle) {
                if ((m_jobs != 0) || InFlight) { return; }
                if ((Done || Retired) && !m_workspaceReleased) {
                    m_workspaceReleased = true;
                    Final?.Dispose(); Final = null;
                    m_points?.Dispose();
                    m_pointMaterials?.Dispose();
                    Contour = null; Cut = null; Creases = null; Projection = null;
                    Next = null; Deferred = null;
                    Memory.Dispose();
                }
                if (Retired && !m_released) {
                    m_released = true;
                    m_mesh?.Release(); m_mesh = null;
                    Replaces?.RetireReplaced();
                }
            }
        }
        private void RetireReplaced() { Retired = true; }

        // The group of the same bake baking this group's geometry, whose mesh this one takes when it finishes, or null.
        public Group? Leader { get; set; }
        // The plan of this group at the newest sampled set, while this group still bakes an older one (Resample): never
        // begun, so it holds nothing, and started in this group's place once this group lands (Promote).
        public Group? Successor { get; set; }
        // The settle watch (SdfMeshPathBake.Settle): the group a settle rebake replaces until it lands, the slots besides
        // its own its geometry reads and their poses in its frame as baked, as this frame reads them, and as they stood
        // when the group last saw them change, when that was, and whether a settle rebake at that pose was refused.
        public Group? Replaces { get; set; }
        public Quaternion[]? BakedOrientations { get; private set; }
        public Vector3[]? BakedPositions { get; private set; }
        public bool Blocked { get; set; }
        public Quaternion[]? CurrentOrientations { get; private set; }
        public Vector3[]? CurrentPositions { get; private set; }
        public int[]? MemberSlots { get; private set; }
        public bool Seeded { get; set; }
        public Quaternion[]? StillOrientations { get; private set; }
        public Vector3[]? StillPositions { get; private set; }
        public float StillSince { get; set; }
        // The box a demand point measures the group by: its planned box, taken before the bake narrows it, as a center and
        // half extents in world space or, for a dynamic group, its slot's frame and units.
        public Vector3 DemandCenter { get; private set; }
        public Vector3 DemandHalf { get; private set; }
        public bool HasDemandBox { get; private set; }
        // When the frame thread first saw the group done, and its mesh's words in the mesh region (Stopwatch timestamps;
        // zero not yet).
        public long LandedAt { get; set; }
        public long ResidentAt { get; set; }
        // The display pixels its demand box projects at the demand point that sees it largest, as of the latest frame
        // that measured it (SdfMeshDemand.Priority); zero with no demand point.
        public float Priority { get; set; }

        // Takes the demand box from the planned box, once.
        public void TakeDemandBox() {
            if (HasDemandBox) { return; }
            DemandCenter = ((Low + High) * 0.5f);
            DemandHalf = Vector3.Abs(value: ((High - Low) * 0.5f));
            HasDemandBox = true;
        }
        // Draws its mesh renumbered into a later program's material ids, which number its shapes place for place.
        public void Renumber(int[] materials, SdfMesh mesh) {
            Renumber(materials: materials);
            Mesh = mesh;
        }
        // Takes the member slots and their baked poses from the group's geometry, once; or, for a mesh baked at another
        // pose of the group's form, each member's pose as that mesh's skin records it (SdfMesh.Members), by its offset
        // from the group's slot.
        public void Watch(SdfMesh? baked = null) {
            var members = Geometry.Poses.Where(predicate: pose => (pose.Key != Slot)).ToArray();

            MemberSlots = [.. members.Select(selector: static pose => pose.Key)];
            BakedPositions = [.. members.Select(selector: static pose => pose.Value.Position)];
            BakedOrientations = [.. members.Select(selector: static pose => pose.Value.Orientation)];
            CurrentPositions = new Vector3[members.Length];
            CurrentOrientations = new Quaternion[members.Length];
            StillPositions = new Vector3[members.Length];
            StillOrientations = new Quaternion[members.Length];
            if (baked is null) { return; }
            foreach (var member in baked.Members.Span) {
                var index = Array.IndexOf(array: MemberSlots, value: (Slot + member.SlotOffset));

                if (index < 0) { continue; }
                BakedPositions[index] = member.Position;
                BakedOrientations[index] = member.Orientation;
            }
        }
    }
    // A group's final lattice: its values and materials, sampled a slab at a time, then the sampled lattice built from
    // them and the stage of points it is answering: refinement's fine samples, bracketed edge roots, then the samples
    // its contour reads a gradient at.
    private sealed class FinalLattice(SdfMeshBakeRequest lattice, IAllocator? allocator, MemoryAccount.Owner owner) : IDisposable {
        public SdfMeshBakeRequest Lattice { get; } = lattice;

        // Allocated by the first slab's answer, which only a byte-admitted group takes, so a group waiting for
        // admission holds its lattice's description alone.
        public Span<float> Values { get { m_values.Resize(Lattice.Samples); return m_values.GetSpan(); } }
        public ReadOnlyMemory<int> MaterialMemory { get { _ = Materials; return m_materials.Memory; } }
        public Span<int> Materials { get { m_materials.Resize(Lattice.Samples); return m_materials.GetSpan(); } }
        public ReadOnlyMemory<float> ValueMemory { get { _ = Values; return m_values.Memory; } }

        private readonly OwnedBuffer<float> m_values = new(allocator: allocator, observer: owner);
        private readonly OwnedBuffer<int> m_materials = new(allocator: allocator, observer: owner);

        public int Planes { get; set; }
        public SdfSampledLattice? Sampled { get; set; }
        public FinalStage Stage { get; set; }

        // The current round, the lattice's own storage (SdfSampledLattice.Refine): all chunks of a round finish before the
        // lattice writes the next, which the group's running stage does on its worker before the gate publishes the
        // round's first chunk.
        public ReadOnlyMemory<Vector4> Points { get; set; }

        public void Dispose() { Sampled?.Dispose(); m_values.Dispose(); m_materials.Dispose(); }

        public int Answered { get; set; }
        // A union's refinement: the fine cells a coarse cell splits into (zero for the FineCell rule) and the members'
        // regions it measures whatever their samples show.
        public int Factor { get; init; }

        public SdfLatticeRegion[] Regions { get; init; } = [];
    }
    private enum FinalStage {
        Refinement,
        Crossings,
        Gradients,
    }
}
