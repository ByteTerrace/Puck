using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;
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

    /// <summary>Gets how far each gradient tap lies from its sample along every axis, in the frame <see cref="Origin"/> is
    /// in: <see cref="SdfMeshBakeTaps.Spacing"/> of <see cref="Cell"/>. The bake kernel reads it from the lattice's record
    /// (<see cref="SdfMeshBakeBlock.Write"/>) and every CPU answer of the lattice starts from it, so where the taps lie is
    /// decided here alone.</summary>
    public float TapSpacing => SdfMeshBakeTaps.Spacing(cell: Cell);
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => ((Dims.X * Dims.Y) * Dims.Z);
    // Final grid slabs carry materials even though their description requests gradients for planning.
    internal bool MaterialOnly { get; init; }
    internal bool WithMaterial { get; init; }
    internal bool WithGradient => Gradients && !Points.IsEmpty;
    internal uint OutputMode => WithGradient ? 3u : MaterialOnly ? 2u : WithMaterial || Slab >= 0 ? 1u : 0u;
    /// <summary>Number of bytes occupied by this lattice's output planes.</summary>
    public int OutputBytes => checked(Samples * sizeof(uint) * (OutputMode switch { 1 => 2, 3 => 4, _ => 1 }));

    /// <summary>Writes the selected fields of one CPU sample into this lattice's output planes.</summary>
    public void WriteSample(Span<byte> output, int local, float value, int material, Vector3 gradient) {
        var words = MemoryMarshal.Cast<byte, int>(output);
        var mode = OutputMode;
        words[local] = mode == 2 ? material : BitConverter.SingleToInt32Bits(value);
        if (mode == 1) { words[Samples + local] = material; }
        if (mode == 3) { MemoryMarshal.Cast<int, Vector3>(words[Samples..])[local] = gradient; }
    }

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

    /// <summary>Gets the cell the bake kernel thickens the lattice's glyphs to and the thinnest stroke it takes them to
    /// have, in units of a glyph's distance scale (the lettering stroke rule, <c>sdfGlyph</c>): its group's
    /// <see cref="SdfMeshBakeGroup.GlyphCell"/> and <see cref="SdfMeshGeometryBuilder.GlyphStroke"/>, the same for every
    /// lattice of the group, so its coarse samples, fine samples, gradient taps and material reads read one field. Zero
    /// cell, the default, thickens nothing.</summary>
    public (float Cell, float Stroke) Glyphs { get; init; }

    // Only a field slice observes live frame poses. A persisted bake uses its sealed pose rows.
    internal bool LivePoses { get; init; }
}
/// <summary>The lattices one bake dispatch samples, at most <see cref="SdfWorldPackage.MeshBakeBatch"/> holding at most
/// <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples together, and the group each answers.</summary>
/// <param name="Lattices">The lattices, in dispatch order.</param>
/// <param name="Tickets">The group each lattice answers.</param>
public sealed record SdfMeshBakeBatch(SdfMeshBakeRequest[] Lattices, object[] Tickets) {
    // The readback bytes the take granted the batch (SdfMeshPathBake.ReadbackBytes): handed once to the copy of its samples,
    // which gives them back when freed, or given back by the bake when the batch is abandoned before any copy is made.
    private long m_readback;

    internal long Readback { init => m_readback = value; }
    // Hands over the batch's granted readback bytes once; zero after the first call.
    internal long TakeReadback() => Interlocked.Exchange(location1: ref m_readback, value: 0L);
    /// <summary>Gets each lattice's group's poses: the first slot they cover and three rows a slot from it, laid out as
    /// the dynamic-transform table lays a slot out, which the bake kernel reads in place of the frame's transforms; no
    /// rows for a group that reads no slot.</summary>
    public (int First, ReadOnlyMemory<Vector4> Rows)[] Poses { get; init; } = [];
    // Each lattice's sample keys as the take that dispatched it decided them, null for a lattice whose samples are not
    // kept: one an entry for every batch a bake takes (SdfMeshPathBake.TryTake), read by its completion alone.
    internal SdfMeshPathBake.SampleKeys?[] Keeps { get; init; } = [];
    /// <summary>Gets the snapshots the batch's lattices sample, each at its field's handle in the program the batch samples:
    /// the ones its groups' geometries hashed (<see cref="SdfMeshBakeGroup.SampledSnapshots"/>), one snapshot a handle.</summary>
    public (int Handle, SdfSampledSnapshot Snapshot)[] Sampled { get; init; } = [];

    /// <summary>Gets the handles of the program the batch samples (<see cref="SdfProgram.SampledFields"/>), which its sampled
    /// table's directory holds a word for.</summary>
    public int SampledHandles { get; init; }
    /// <summary>Gets the byte length of the packed output of every lattice.</summary>
    public int OutputBytes => Lattices.Sum(static lattice => lattice.OutputBytes);
    /// <summary>Gets the field evaluations the batch's lattices make (<see cref="SdfMeshBakeRequest.Taps"/>), the unit the
    /// bake's GPU time is measured in.</summary>
    public long Taps {
        get {
            var taps = 0L;

            foreach (var lattice in Lattices) { taps += lattice.Taps; }
            return taps;
        }
    }
    /// <summary>Gets the words of the batch's sampled table (<see cref="WriteSampledDirectory"/>), at least one.</summary>
    public int SampledWords {
        get {
            var words = SampledHandles;

            foreach (var (_, snapshot) in Sampled) { words = checked((words + snapshot.Count)); }
            return Math.Max(val1: 1, val2: words);
        }
    }

    /// <summary>Writes each handle's snapshot offset, or the absent sentinel, into the sampled table directory.</summary>
    public void WriteSampledDirectory(Span<uint> directory) {
        directory.Fill(uint.MaxValue);
        var offset = SampledHandles;
        foreach (var (handle, snapshot) in Sampled) {
            directory[handle] = (uint)offset;
            offset += snapshot.Count;
        }
    }

    /// <summary>Gets the input prefix the batch reads: explicit points at their sample offsets, then saved poses after
    /// all samples. Grid samples without poses need no input rows, including trailing grid samples.</summary>
    public int InputRows {
        get {
            var first = 0;
            var required = 0;

            foreach (var lattice in Lattices) {
                if (!lattice.Points.IsEmpty) { required = Math.Max(val1: required, val2: checked((first + lattice.Points.Length))); }
                first = checked((first + lattice.Samples));
            }
            foreach (var (_, rows) in Poses) {
                first = checked((first + rows.Length));
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

    // Each lattice's ticket, written only under its bake's gate by its one transition (SdfMeshPathBake.MoveTicket).
    internal SdfMeshBakeTicket[] States { get; } = new SdfMeshBakeTicket[Tickets.Length];

    /// <summary>Returns where a lattice's ticket stands: taken until its samples reach its group or slice, or it is handed
    /// back unanswered.</summary>
    /// <param name="lattice">The lattice's index in <see cref="Lattices"/>.</param>
    /// <returns>The ticket's state.</returns>
    public SdfMeshBakeTicket StateOf(int lattice) {
        if (Bake is not { } bake) { return States[lattice]; }
        return bake.TicketOf(batch: this, lattice: lattice);
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
/// <see cref="Taken"/> once and settles as <see cref="Answered"/> or <see cref="Returned"/>, so answering and abandoning a batch,
/// in either order or in part, settle each lattice exactly once.</summary>
public enum SdfMeshBakeTicket {
    /// <summary>The lattice is dispatched or awaiting its samples, and its group takes no other lattice.</summary>
    Taken,
    /// <summary>The answer owns the destination and is copying this lattice's samples.</summary>
    Decoding,
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
/// order into batches the residency's background GPU work dispatches (<see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long)"/>, <see cref="SdfGpuWork"/>),
/// each lattice at most <see cref="LatticeTaps"/> field evaluations: a coarse lattice over the group's bounding sphere, refinements over the box of
/// the cells the surface can cross until the box stops shrinking, then a final lattice of at most 65 samples a side
/// whose spacing follows the box's own proportions (a union's cubic, keeping each of its shapes at the resolution its own
/// lattice would give it), sampled in slabs of whole planes, values and materials; then the fine samples
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
/// <para>A dynamic group whose shapes ride slots besides its own bakes a skin (<see cref="SdfMeshSkin"/>) from its own
/// sealed instructions (<see cref="SdfMeshSkinSource"/>), once at the pose its program arrived in. A group whose geometry
/// reads a frame input besides its program (<see cref="SdfMeshInputs"/>: a sampled field's snapshot, or an operand's pose
/// snapped to the group's step) is planned again when that input moves, and its new mesh replaces the drawn one when it
/// lands.</para>
/// </summary>
public sealed partial class SdfMeshPathBake {
    /// <summary>The field evaluations one lattice makes at most: long lattices and point runs are cut into chunks of this
    /// many evaluations (a gradient sample counts five), a few milliseconds of a current desktop GPU
    /// (<see cref="NanosecondsPerTap"/>). No lattice's spacing or extent reads it, so the meshes do not depend on it.</summary>
    public const int LatticeTaps = 1_000_000;
    /// <summary>The field evaluations one batch (<see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long)"/>) makes at most, past its first lattice, whatever
    /// its allowance: what a held frame's own batch takes, which queues no frame behind it. A budgeted batch is cut to
    /// the frame allowance at the bake's measured rate (<see cref="SdfMeshBakeGpuWork"/>), within this.</summary>
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
    // The fewest samples along any axis of a fitted text group's final lattice: one through the middle of its slab.
    private const int TextMinimum = 3;
    private const int MaxRefinements = 5;

    /// <summary>The finest spacing, in metres, of a final lattice along its longest axis: a part smaller than the lattice
    /// would resolve bakes at this spacing unless an instance declares a finer detail (<see cref="SdfInstanceRange.Detail"/>).
    /// A detail at or past it refines nothing the lattice does not already resolve.</summary>
    public const float MinimumCell = 0.015f;

    // A refinement whose box keeps this share of every extent has converged.
    private const float Converged = 0.7f;

    /// <summary>The spacing, in metres, a final lattice's refined cells aim for where no member declares a detail
    /// (<see cref="SdfInstanceRange.Detail"/>), floored by the group's tier (<see cref="SdfMeshTier"/>); the most fine cells
    /// a coarse cell splits into along each axis is <c>MaxRefinement</c> (<see cref="SdfSampledLattice.Refine"/>).</summary>
    public const float FineCell = 0.005f;

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
    public string ResidencyId { get; } = Guid.NewGuid().ToString(format: "N");

    /// <summary>Gets the selectors of the groups the bake bakes, every group when <see langword="null"/>: a group outside
    /// it finishes unbaked, with no mesh, and no selected group of its geometry waits on it (an offline audit of chosen
    /// groups).</summary>
    public IReadOnlySet<int>? Selection { get; init; }

    /// <summary>Gets the observer handed each baked group's selector and the map of which cells its final lattice's refinement
    /// flagged (<see cref="SdfSampledLattice.RefinementMap"/>), as the group's contour starts, on the thread running that
    /// stage; <see langword="null"/>, a residency's bake, copies nothing. An offline audit sets it to ask whether a defect's
    /// cell was ever flagged.</summary>
    public Action<int, SdfRefinementMap>? RefinementObserver { get; init; }

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

    // Reads a taken batch's ticket under the gate it is written under (SdfMeshBakeBatch.StateOf).
    internal SdfMeshBakeTicket TicketOf(SdfMeshBakeBatch batch, int lattice) {
        using (EnterGate(site: BakeGate.Ticket)) { return batch.States[lattice]; }
    }

    private readonly GroupList m_groups = new();
    private readonly List<Group> m_retiredGroups = [];

    // How many of the first groups Pending found done; every write of a group lowers it to that group's index (Replace).
    private int m_doneThrough;

    private readonly Stopwatch m_clock = new();

    private SdfProgram? m_program;
    // The field m_program derives, counted: it moves when the bake adopts a program that does not share the held one's
    // field (SdfProgram.SharesField), and only then. A group's checkpoint keys name the field they were taken from by this
    // count (CheckpointKeys.Field), never by the program, so keys held by many groups pin no program's tables.
    private long m_field;

    // Under the gate: holds a program, moving the field count when its field is not the held one's.
    private void Adopt(SdfProgram program) {
        if (!program.SharesField(other: m_program)) { Volatile.Write(location: ref m_field, value: (m_field + 1L)); }
        m_program = program;
    }

    // The baked sampled set the groups were planned at (SdfSampledSet.Baked): a frame carrying another resamples the groups
    // that read a moved field.
    private SdfSampledSet m_sampled = SdfSampledSet.Empty;
    // The indices, ascending, of the groups whose geometry reads a frame input (SdfMeshInputs): a sampled field or an
    // operand, the only groups a moved input can touch; and how many of them read an operand, which every frame visits.
    // Which groups read one is the program's (a group planned again at an index holds the same instances), so Restart, the
    // one writer of a program's group list, rebuilds it (ListReaders).
    private readonly List<int> m_inputReaders = [];

    private int m_operandReaders;

    // The groups holding a successor (Group.Successor), which Promote starts once their bake lands.
    private int m_successors;
    // Frames prepared; restarts and the groups they planned; resamples, the readers they walked, the groups an operand's
    // step planned again, the newer plans that replaced a waiting successor, and the successors started: what moved inputs
    // cost the frame thread, as counts.
    private long m_preparedFrames;
    private long m_restarts;
    private long m_restartGroups;
    private long m_resamples;
    private long m_resampleGroups;
    private long m_operandReplans;
    private long m_coalesced;
    private long m_promoted;
    private double m_seconds;
    private bool m_reported;

    /// <summary>Gets the program the bake meshes: the one the last prepared frame carried, or
    /// <see langword="null"/> before the first.</summary>
    public SdfProgram? Program {
        get {
            using (EnterGate(site: BakeGate.Read)) { return m_program; }
        }
    }

    /// <summary>Reads the mesh a group landed, whole: a chunked group's parent, whose surface concatenates the chunks
    /// <see cref="SdfMeshDresser"/> draws one by one.</summary>
    /// <param name="selector">The group's <see cref="SdfMeshBakeGroup.Selector"/>.</param>
    /// <param name="mesh">The landed mesh, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the group is planned and has landed a mesh.</returns>
    public bool TryLandedMesh(int selector, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh) {
        using (EnterGate(site: BakeGate.Read)) {
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
    /// reached the dressed frame yet: one the dresser has not listed, or one still landing in the mesh region ahead
    /// of the draws that will name it (<see cref="ObserveResidency"/>). Never waits: it reads what the latest release of
    /// the bake's lock left, and whether the dressed frame has taken the latest product.</summary>
    public bool Pending => ((Volatile.Read(location: ref m_owing) != 0) || DressOwed());
    /// <summary>Gets whether the frame's draws still follow the bake: <see cref="Pending"/> less the products landing
    /// in the mesh region ahead of the draws, which move no draw until they take them. A view whose latest render
    /// stands need not render again while only those wait. Never waits, as <see cref="Pending"/>.</summary>
    public bool DrawsPending => (((Volatile.Read(location: ref m_owing) & OwingWork) != 0) || DressOwed());

    // What the bake owed as the latest holder of its lock left it, for the frame's reads that must not wait on the
    // workers holding it (Pending, DrawsPending): products landing ahead of the draws, and work (Owes, less the dressed
    // frame's revision, which the dresser moves outside the lock and DressOwed reads live). Invariant: every change of
    // what it sums happens under the lock, and every release of the lock publishes the sum (GateScope.Dispose), so a
    // read answers as a read under the lock at the latest release would have.
    private int m_owing;

    private const int OwingAhead = 1;
    private const int OwingWork = 2;

    // Publishes what the bake owes, at a release of its lock. Under the gate.
    private void PublishOwing() => Volatile.Write(location: ref m_owing, value: (((m_ahead.Count > 0) ? OwingAhead : 0) | (OwesWork() ? OwingWork : 0)));
    // Whether a landed product has not reached the dressed frame: the dresser acknowledges outside the lock.
    private bool DressOwed() => ((Volatile.Read(location: ref m_program) is not null) && (Volatile.Read(location: ref m_dressedRevision) != Volatile.Read(location: ref m_productRevision)));

    /// <summary>Gets whether every group of the bake's program draws a mesh of its own geometry at some tier, or has
    /// finished with none: no group draws nothing while it bakes, and none still draws the mesh it took from the group it
    /// replaced, which shows the geometry before a change. The first frame that finds it true after a change shows the
    /// changed form whole, though finer tiers may still land. <see langword="false"/> before the first program. It walks
    /// every group under the bake's lock, so a caller reads it to measure, not every frame.</summary>
    public bool DrawsOwn {
        get {
            using (EnterGate(site: BakeGate.Read)) {
                if (m_program is null) { return false; }
                foreach (var group in m_groups) {
                    if (!group.DrawsOwn) { return false; }
                }
                return true;
            }
        }
    }

    /// <summary>Names what keeps the bake from settled: each part of <see cref="Pending"/> that holds (products landing
    /// ahead of the draws, a landed product the dressed frame has not taken, a field slice, successors, first details
    /// owed, groups still baking), then, when <see cref="DetailSettled"/> is false, what its latest tier decision found
    /// keeping the detail moving. A wait that releases unsettled prints it.</summary>
    /// <returns>The reasons, or an empty string when the bake is settled.</returns>
    public string DescribeUnsettled() {
        using (EnterGate(site: BakeGate.Read)) {
            var parts = new List<string>();

            if (m_ahead.Count > 0) { parts.Add(item: $"{m_ahead.Count} product(s) landing ahead of the draws"); }
            if ((m_program is not null) && (m_dressedRevision != m_productRevision)) { parts.Add(item: "a landed product the dressed frame has not taken"); }
            if (m_slice is not null) { parts.Add(item: "a field slice sampling"); }
            if (m_successors > 0) { parts.Add(item: $"{m_successors} successor(s) waiting"); }
            if (m_firstDetailsOwed > 0) { parts.Add(item: $"{m_firstDetailsOwed} first detail(s) owed"); }

            var baking = 0;
            var live = 0;

            for (var index = DoneThrough(); (index < m_groups.Count); index++) {
                if (m_groups[index].Done) { continue; }
                baking++;
                if (m_groups[index].Live) { live++; }
            }
            if (baking > 0) { parts.Add(item: $"{baking} group(s) baking ({live} live)"); }
            if (!DetailSettled && m_detailHold.Holds) { parts.Add(item: $"detail held: {m_detailHold}"); }
            return string.Join(separator: "; ", values: parts);
        }
    }

    // Whether the bake still owes the dressed frame a mesh, a slice, a successor or the demanded tier of a floor planned
    // on arrival. Under the gate.
    private bool Owes() => (DressOwed() || OwesWork());
    // Owes, less a landed product the dressed frame has not taken. Under the gate.
    private bool OwesWork() {
        if ((m_slice is not null) || (m_successors > 0) || (m_firstDetailsOwed > 0)) { return true; }
        return (DoneThrough() < m_groups.Count);
    }
    // Advances and returns the done cursor: the index every group before which is done. A group's Done only turns on, so
    // the groups before the cursor stay done until one is replaced. Under the gate.
    private int DoneThrough() {
        while ((m_doneThrough < m_groups.Count) && m_groups[m_doneThrough].Done) { m_doneThrough++; }
        return m_doneThrough;
    }

    /// <summary>Gets whether an opaque static group still owes a mesh, or a landed mesh has not reached the dressed
    /// frame. Only these groups enter the irradiance scene: animated groups, glass and diagnostic field slices must
    /// not hold its first lighting measurement or a later static-scene refresh behind unrelated work.</summary>
    public bool IrradiancePending {
        get {
            using (EnterGate(site: BakeGate.Read)) {
                // A floor planned on arrival stands in for its detail, which the scene would be measured again for.
                if (((m_program is not null) && (m_dressedRevision != m_productRevision)) || (m_firstDetailsOwed > 0)) { return true; }
                foreach (var group in m_ahead) {
                    if ((!group.Dynamic || m_fixedAssetSlots.Contains(item: group.Slot)) && !group.Glass && !group.Retired && group.Ahead) { return true; }
                }
                // Every group before the done cursor is done, so a settled bake answers without walking its groups.
                for (var index = DoneThrough(); (index < m_groups.Count); index++) {
                    var group = m_groups[index];

                    if ((!group.Dynamic || m_fixedAssetSlots.Contains(item: group.Slot)) && !group.Glass && !group.Done) { return true; }
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

        using (EnterGate(site: BakeGate.Read)) {
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
    private CopyLayout[] m_copyLayout = [];
    private int[] m_copyAssetSlots = [];
    // The same slots as a set, which CopiesOf asks of every dynamic group.
    private readonly HashSet<int> m_copyAssetSlotSet = [];
    // The copy journal's endpoint the layout was last compared at, the runs it named, and whether they are every copy
    // because the journal could not prove the window.
    private readonly GpuRowCursor m_copyCursor = new();
    private readonly GpuUploadRuns m_copyRuns = new(GpuRegion.MaxCopyRuns);
    private bool m_copiesWhole;

    private long m_copyLayoutRevision;

    private readonly record struct CopyLayout(int AssetSlot, SdfMeshTemplateRange? TemplateRange, bool IsDynamic, bool HasMesh, int[] OtherAssetSlots);

    // A changed landed product is acknowledged only after the dresser has published its draws and ownership claims.
    private long m_productRevision;

    private long m_dressedRevision = -1;
    // The groups whose product lands in the mesh region ahead of the draws (Group.Ahead); the count of changes to
    // that list, the count the tables were last named its meshes at, and those tables; and the products that took
    // their draws, those the region had no room for beside the mesh they replace among them. Under the gate.
    private readonly List<Group> m_ahead = [];
    private readonly List<SdfMesh> m_aheadMeshes = [];
    // Where each waiting product stands this frame, by its place in m_ahead (ServeAhead).
    private readonly List<AheadState> m_aheadStates = [];

    private long m_aheadRevision;

    private long m_aheadNamed = -1L;

    private SdfWorldTables? m_aheadTables;
    private long m_handed;
    private long m_handedRefused;

    // Whether a residency's tables land this bake's meshes: they have handed it their grant (ObserveResidency).
    // A bake no tables observe (an offline bake) draws every product at once, since nothing would hand it its draws.
    private bool LandsAhead => (m_detail is not null);

    // One more each time the group list or a group's kept floor changes, and its value when ServeAhead last named the
    // kept floors ahead.
    private long m_floorRevision;
    private long m_floorsNamed = -1L;

    private enum AheadState : byte { Landing, Stands, StandsRefused }

    private void AwaitAhead(Group group) {
        m_ahead.Add(item: group);
        m_aheadRevision++;
    }

    // The library's lost revision this bake last restarted at.
    private long m_lostRevision;

    // This is the only door from presentation to the bake's live groups. Preparation and reading share one gate;
    // workers cannot replace/release a landed mesh before the dresser has claimed it. No bake entry calls presentation.
    internal FrameAccess PrepareFrame(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        // The measure the frame before posted reads what this frame changes: the group list, the copy index and inputs.
        JoinMeasure();

        var scope = EnterFrameGate(site: BakeGate.Frame);

        try {
            m_preparedFrames++;
            SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.Prepared);
            // The copies are indexed and the frame's inputs copied in before anything plans: a plan measures a shared
            // geometry where they stand (Tiers).
            m_farDistance = frame.FarDistance;
            ResumeReloads();

            var reindexed = !SameCopyLayout(frame: frame);

            if (reindexed) { IndexCopies(frame: frame); }
            m_copyCursor.Commit();
            CopyFrameInputs(frame: frame, reindexed: reindexed);
            // The bake reads only the baked handles: a frame whose height fields' heights alone moved changes nothing here.
            // A lost mesh (SdfMeshLibrary.Lose) restarts the bake on the same program, which bakes again only the groups
            // whose meshes were lost (Keeps). An operand moves with the frame's transforms, so its readers are visited every
            // frame. A program that shares the held one's field (SdfProgram.SharesField: only its appearance moved) is
            // adopted as it stands: every group, plan and bake in flight reads the field alone.
            if (!ReferenceEquals(objA: frame.Program, objB: m_program) && frame.Program.SharesField(other: m_program)) {
                Adopt(program: frame.Program);
            }
            if (!ReferenceEquals(objA: frame.Program, objB: m_program) || (Library.LostRevision != m_lostRevision)) {
                m_lostRevision = Library.LostRevision;
                Restart(frame: frame);
            } else if (!ReferenceEquals(objA: frame.Sampled.Baked, objB: m_sampled) || (m_operandReaders > 0)) {
                Revisit(frame: frame);
            }
            if (m_successors > 0) { Promote(frame: frame); }
            scope.Continue(site: BakeGate.Retier);
            FinishReplacements();
            if (m_evictions.Count > 0) {
                scope.Continue(site: BakeGate.Evict);
                Evict(frame: frame);
                scope.Continue(site: BakeGate.Retier);
            }
            // The tiers are decided from the demand the frame before measured; this frame's measure and the groups' share
            // of priorities (Prioritize) run on the measure's lane while the frame is dressed and recorded.
            Retier(frame: frame);
            PostMeasure();
            scope.Continue(site: BakeGate.Judge);
            JudgeFloors();
            return new FrameAccess(owner: this, scope: scope);
        } catch { scope.Dispose(); throw; }
    }

    // The groups whose drawn state (their mesh, its tier or trace, or their place in the list) changed since the dresser
    // last read the groups, so the dresser reconciles those alone. Invariant: every change of what the dresser reads of a
    // group goes through Touch, which also moves the product revision the dresser stands its whole walk on, and a change
    // no group names (the list replaced, a verdict declined, which moves every floor's trace) marks the whole list
    // changed. FrameAccess takes the list under the gate, so a change made after that lands in the next. Under the gate.
    private readonly MarkedGroups m_touched = new(mark: GroupMarks.Touched);
    private bool m_touchedAll = true;

    private void Touch(Group group) {
        m_productRevision++;
        if (m_touchedAll) { return; }
        _ = m_touched.Add(group: group);
    }
    // Marks every group changed, for a change that names none.
    private void TouchAll() {
        m_productRevision++;
        m_touchedAll = true;
    }
    // Hands the dresser the groups changed since it last took them, and starts the next list.
    private List<Group> TakeTouched(out bool whole) {
        whole = m_touchedAll;
        m_touchedAll = false;

        return m_touched.Take();
    }

    // Invariant: the dresser reads the bake's groups (their list, drawn meshes, traces, tiers and copies) only while it
    // holds the gate, and a mesh it draws is claimed by its rows (SdfMeshRows) before the gate opens, so no worker
    // replaces or releases a landed mesh the dresser has not claimed. Enforced here: every group read throws once the
    // access is released (Release, or Dispose), so the dresser's work that reads no group (poses, uploads, publishing)
    // runs with the gate open, and a new read past the release cannot pass unseen.
    internal ref struct FrameAccess {
        private readonly SdfMeshPathBake m_owner;

        private readonly List<Group> m_touched;
        private GateScope m_scope;
        private bool m_released;

        internal FrameAccess(SdfMeshPathBake owner, GateScope scope) {
            m_owner = owner;
            m_scope = scope;
            m_scope.Continue(site: BakeGate.Dress);
            Revision = owner.m_productRevision;
            CopyLayoutRevision = owner.m_copyLayoutRevision;
            m_touched = owner.TakeTouched(whole: out var whole);
            Whole = whole;
        }

        internal long Revision { get; }
        internal long CopyLayoutRevision { get; }
        // Whether the groups changed since the dresser last took the list are not named (the list was replaced, or a
        // change named none), so the dresser reads every group.
        internal bool Whole { get; }
        // The groups changed since the dresser last took the list (Touch), read by index and valid only while the
        // access holds the gate; with `Whole` the list is not the changes.
        internal readonly int TouchedCount {
            get {
                _ = Owner;

                return m_touched.Count;
            }
        }
        // The groups are read by index, so a dress allocates no enumerator.
        internal readonly int GroupCount => Owner.m_groups.Count;

        internal readonly SdfMeshBakeGroup GroupAt(int index) => Owner.m_groups[index];
        internal readonly SdfMeshBakeGroup TouchedAt(int index) => Held(m_touched[index]);
        // Whether the group stands in the bake's list, which the groups the dresser reads by index all do.
        internal readonly bool Lists(SdfMeshBakeGroup group) => Held((Group)group).Listed;
        internal readonly SdfMesh? MeshOf(SdfMeshBakeGroup group) => Held((Group)group).Shown;
        // The mesh the irradiance volume traces in place of the group's drawn one (Group.Trace), null for the drawn one.
        internal readonly SdfMesh? TraceOf(SdfMeshBakeGroup group) => Held((Group)group).Trace;
        // The tier the mesh a group draws was baked at: the replaced group's while a replacement lands ahead.
        internal readonly int TierOf(SdfMeshBakeGroup group) => (Held((Group)group).Replaces ?? group).Tier;
        // The floor a group keeps of its own shape beside the finer tier it draws (Group.CurrentFloor), which a view too
        // coarse for that tier draws in its place (SdfMeshBand); null where the group draws its floor, keeps none, draws
        // another shape's stand-in, or is glass or skinned, whose passes take every mesh's own triangles.
        internal readonly SdfMesh? FloorOf(SdfMeshBakeGroup group) {
            var held = Held((Group)group);

            return (((held.CurrentFloor is { } floor) && (held.Shown is { } shown) && !ReferenceEquals(objA: floor, objB: shown) &&
                !(held.Replaces ?? held).AtFloor && !held.Glass && shown.Members.IsEmpty && floor.Members.IsEmpty)
                ? floor
                : null);
        }
        // The tier the floor a group keeps was baked at.
        internal readonly int FloorTierOf(SdfMeshBakeGroup group) => Held((Group)group).KeptFloorTier;
        internal readonly IReadOnlyList<int>? CopiesOf(SdfMeshBakeGroup group) => Owner.CopiesOf(group: group);
        // Records that the frame's draws stand at the captured product revision, once they are published; after the
        // release too, since it reads no group.
        internal readonly void Acknowledge() => Volatile.Write(location: ref m_owner.m_dressedRevision, value: Revision);

        // Opens the gate once the dress has read every group it reads and its rows claim every mesh it draws; a second
        // release, or the dispose after it, does nothing.
        internal void Release() {
            if (m_released) { return; }
            m_released = true;
            m_scope.Dispose();
        }
        public void Dispose() => Release();

        private readonly SdfMeshPathBake Owner => (m_released ? throw new InvalidOperationException(message: "The bake's groups are read only while the frame access holds its gate.") : m_owner);
        private readonly Group Held(Group group) => (m_released ? throw new InvalidOperationException(message: "The bake's groups are read only while the frame access holds its gate.") : group);
    }

    // Whether the frame's copies keep the layout the index was built from: compared at the copies the producer's journal
    // names changed since the last prepared frame, or at every copy when it cannot prove that window (a frame whose
    // template slots moved owes every copy). It collects the cursor PrepareFrame commits.
    private bool SameCopyLayout(SdfFrame frame) {
        var whole = m_copyCursor.Collect(frame.MeshCopyChanges, frame.MeshCopyChangesSerial, frame.MeshInstances, frame.MeshInstances.Count, 0,
            m_copyRuns, force: ((frame.MeshCopyChanges is null) || (m_copyLayout.Length != frame.MeshInstances.Count)));

        m_copiesWhole = whole;
        if ((m_copyLayout.Length != frame.MeshInstances.Count) || (m_copyAssetSlots.Length != frame.MeshAssetSlots.Count)) { return false; }
        if (whole) {
            for (var index = 0; (index < m_copyAssetSlots.Length); index++) { if (m_copyAssetSlots[index] != frame.MeshAssetSlots[index]) { return false; } }
        }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressCopiesVisited, amount: m_copyRuns.Length(index: run));
            for (var index = m_copyRuns.Start(index: run); (index < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); index++) {
                var old = m_copyLayout[index];
                var copy = frame.MeshInstances[index];

                if ((old.AssetSlot != copy.AssetSlot) || (old.TemplateRange != copy.TemplateRange) || (old.IsDynamic != copy.IsDynamic) || (old.HasMesh != (copy.Mesh is not null)) ||
                    (old.OtherAssetSlots.Length != copy.OtherAssetSlots.Count)) { return false; }
                for (var surface = 0; (surface < old.OtherAssetSlots.Length); surface++) {
                    if (old.OtherAssetSlots[surface] != copy.OtherAssetSlots[surface]) { return false; }
                }
            }
        }
        return true;
    }
    // The one membership index serves both pending-bake demand and landed draw dressing. It contains no poses:
    // a moving occurrence keeps its membership and supplies its current matrix through m_copyPoses.
    private void IndexCopies(SdfFrame frame) {
        m_slotCopies.Clear();
        m_instanceCopies.Clear();
        m_fixedAssetSlots.Clear();
        m_copyAssetSlots = frame.MeshAssetSlots.ToArray();
        m_copyAssetSlotSet.Clear();
        m_copyAssetSlotSet.UnionWith(other: m_copyAssetSlots);
        m_copyLayout = new CopyLayout[frame.MeshInstances.Count];
        for (var index = 0; (index < m_copyLayout.Length); index++) {
            var copy = frame.MeshInstances[index];

            m_copyLayout[index] = new(copy.AssetSlot, copy.TemplateRange, copy.IsDynamic, (copy.Mesh is not null), copy.OtherAssetSlots.ToArray());
        }
        m_copyLayoutRevision++;
        m_demandCopyLayout = true;
        for (var index = 0; (index < frame.MeshInstances.Count); index++) {
            var copy = frame.MeshInstances[index];

            if (copy.TemplateRange is { } range) {
                for (var instance = 0; (instance < range.InstanceCount); instance++) { AddCopyIndex(m_instanceCopies, (range.FirstInstance + instance), index); }
                continue;
            }
            AddCopyIndex(m_slotCopies, copy.AssetSlot, index);
            if (!copy.IsDynamic) { m_fixedAssetSlots.Add(item: copy.AssetSlot); }
            foreach (var slot in copy.OtherAssetSlots) {
                if (slot != copy.AssetSlot) { AddCopyIndex(copy: index, key: slot, table: m_slotCopies); }
                if (!copy.IsDynamic) { m_fixedAssetSlots.Add(item: slot); }
            }
        }
    }
    private static void AddCopyIndex(Dictionary<int, List<int>> table, int key, int copy) {
        if (!table.TryGetValue(key: key, value: out var indices)) { table.Add(key: key, value: indices = []); }
        if ((indices.Count == 0) || (indices[^1] != copy)) { indices.Add(item: copy); }
    }
    private IReadOnlyList<int>? CopiesOf(SdfMeshBakeGroup group) {
        if (!group.Dynamic) { return m_instanceCopies.GetValueOrDefault(key: group.FirstInstance); }
        if (!m_copyAssetSlotSet.Contains(item: group.Slot)) { return null; }
        return (m_slotCopies.TryGetValue(key: group.Slot, value: out var copies) ? copies : Array.Empty<int>());
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
    /// and <paramref name="mostTaps"/> evaluations. Each call takes a batch of its own, so several batches dispatch at
    /// once, each lattice in one of them, and a taken batch counts as the bake's work. Taking by priority rather than round
    /// robin finishes the groups about the views first, each landing in the mesh library as it finishes, so a residency the
    /// program leaves mid-bake (a body crossing into another world) leaves the groups it finished there for the residency
    /// it reaches.</summary>
    /// <param name="field">The program whose field the dispatch samples: the tables' uploaded program. The bake takes
    /// nothing unless it is the program the bake meshes (<see cref="Program"/>), so a batch never samples another
    /// program's field.</param>
    /// <param name="batch">The lattices to dispatch and the group each answers, handed back to
    /// <see cref="Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/> or <see cref="Abandon"/>.</param>
    /// <param name="mostTaps">The field evaluations the batch makes at most past its first lattice, which is always taken
    /// and holds at most <see cref="LatticeTaps"/>; held to <see cref="BatchTaps"/>. No lattice reads it, so the meshes do
    /// not depend on it.</param>
    /// <returns><see langword="true"/> when the bake takes a lattice.</returns>
    public bool TryTake(SdfProgram? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch, long mostTaps = BatchTaps) =>
        TryTake(batch: out batch, field: field, firstFits: false, heldTaps: out _, mostTaps: mostTaps);
    /// <summary>Takes the lattices of one dispatch as <see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long)"/>
    /// does, optionally holding its first lattice to <paramref name="mostTaps"/> too.</summary>
    /// <param name="field">The program whose field the dispatch samples: the tables' uploaded program.</param>
    /// <param name="batch">The lattices to dispatch and the group each answers.</param>
    /// <param name="mostTaps">The field evaluations the batch makes at most past its first lattice, and, when
    /// <paramref name="firstFits"/>, including it; held to <see cref="BatchTaps"/>.</param>
    /// <param name="firstFits">Whether the first lattice is held to <paramref name="mostTaps"/> too, so a batch past it
    /// is not taken at all: for background work queued behind other jobs ahead of a frame
    /// (<see cref="SdfGpuWorkAllowance.Queued"/>), which waits for them rather than overrun the frame's deadline.</param>
    /// <param name="heldTaps">The evaluations of the first lattice <paramref name="firstFits"/> held back, which the
    /// next take waits for room for; zero when none was.</param>
    /// <returns><see langword="true"/> when the bake takes a lattice.</returns>
    public bool TryTake(SdfProgram? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch, long mostTaps, bool firstFits, out long heldTaps) =>
        TryTake(batch: out batch, field: field, firstFits: firstFits, heldTaps: out heldTaps, mostTaps: mostTaps, owedOnly: false);
    /// <summary>Takes the lattices of one dispatch as
    /// <see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long, bool, out long)"/> does, from the groups that owe the
    /// frame alone (<see cref="OwedGroups"/>), under the allowance of the work the frame owes
    /// (<see cref="SdfGpuWorkBudget.OwedAllowance"/>). A field slice is never owed.</summary>
    /// <param name="field">The program whose field the dispatch samples: the tables' uploaded program.</param>
    /// <param name="batch">The lattices to dispatch and the group each answers.</param>
    /// <param name="mostTaps">The field evaluations the batch makes at most, as the other take reads it.</param>
    /// <param name="firstFits">Whether the first lattice is held to <paramref name="mostTaps"/> too.</param>
    /// <param name="heldTaps">The evaluations of the first lattice <paramref name="firstFits"/> held back; zero when none
    /// was.</param>
    /// <returns><see langword="true"/> when the bake takes a lattice.</returns>
    public bool TryTakeOwed(SdfProgram? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch, long mostTaps, bool firstFits, out long heldTaps) {
        if (OwedGroups == 0) {
            batch = null;
            heldTaps = 0L;
            return false;
        }
        return TryTake(batch: out batch, field: field, firstFits: firstFits, heldTaps: out heldTaps, mostTaps: mostTaps, owedOnly: true);
    }
    /// <summary>Gets how many groups owed the frame as of the latest prepared frame: an arrival's work, which a boot, a
    /// world reached through a portal and a form edit start with: a group still bringing its first mesh with nothing of
    /// its own drawn (a hole where a view looks, or a place none sees yet), or an arrival's first detail replacing the
    /// floor it landed first (<see cref="SdfMeshTier"/>). Ordinary background work in play (a tier the demand moved to, a
    /// resample, an eviction's floor) owes nothing. Read from any thread.</summary>
    public int OwedGroups => Volatile.Read(location: ref m_owedGroups);

    // The groups that owed the frame as of the latest Prioritize.
    private int m_owedGroups;

    // Whether a group owes the frame work it can bake: not done, baking for itself, and either drawing nothing yet or
    // an arrival's first detail (Group.FirstDetail). A follower's mesh is its leader's work, which takes the follower's
    // view (Prioritize); a group waiting on another residency's bake of its geometry holds no lattice.
    private static bool OwesFrame(Group group) => (!group.Done && !group.Retired && (group.Leader is null) && (group.Joined is null) && ((group.Shown is null) || group.FirstDetail));

    private bool TryTake(SdfProgram? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch, long mostTaps, bool firstFits, out long heldTaps, bool owedOnly) {
        batch = null;
        heldTaps = 0L;
        mostTaps = Math.Clamp(value: mostTaps, min: (firstFits ? 0L : 1L), max: BatchTaps);
        using (EnterFrameGate(site: BakeGate.Take)) {
            if ((field is null) || !ReferenceEquals(objA: field, objB: m_program)) { return false; }
            // Work that finishes from here on offers the next take.
            Volatile.Write(location: ref m_offered, value: 0);
            // A take asked with the program bound is what tells a waiter on this bake's shares that a frame drives it; a
            // waiter of this bake on one no frame drives starts its geometry here, where the take can then take it.
            _ = Interlocked.Increment(location: ref m_asks);
            if (m_joined.Count > 0) { DesertStalled(); }

            var lattices = m_takenLattices;
            var tickets = m_takenTickets;

            lattices.Clear();
            tickets.Clear();
            m_takenPoses.Clear();
            m_takenKeys.Clear();
            m_takenSampled.Clear();
            var samples = 0;
            var taps = 0;
            // The readback bytes the batch's lattices take, granted with the batch (SdfMeshPathBake.Readback).
            var readback = 0L;
            var from = DoneThrough();
            // The groups still baking are the ones from the done cursor on; their counts are kept as they change
            // (GroupTally), so a take walks no group for them. `awaiting` is the groups admitted whose first stage has not
            // reached the pool's queue: stages on their way. `waiting` counts the groups holding a lattice no dispatch has
            // taken, whether or not an owed take could take it.
            var pending = (from < m_groups.Count);

            // No lattice is taken, and no group admitted, without the readback reservation that answers it.
            if (!pending && (m_slice is null)) {
                IdleReadback();
            } else if (!HoldReadback()) {
                return false;
            }
            // A slice's lattices come first, within the same batch, so a long bake never holds a diagnostic back; it owes
            // the frame no hole, so an owed take leaves it to the next.
            while (!owedOnly && (m_slice is { } slice) && (lattices.Count < SdfWorldPackage.MeshBakeBatch)) {
                if (slice.Take(instances: (m_program?.Instances.Count ?? 0), ticket: out var lattice, worldSelector: SdfMeshBakeGroup.WorldSelector) is not { } next) { break; }
                if (((lattices.Count > 0) || firstFits) && (((taps + next.Taps) > mostTaps) || ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples))) {
                    if (lattices.Count == 0) { heldTaps = next.Taps; }
                    slice.Return(ticket: lattice);
                    break;
                }
                if ((readback + next.OutputBytes) > ReadbackRoom) {
                    slice.Return(ticket: lattice);
                    break;
                }
                samples += next.Samples;
                taps += next.Taps;
                readback += next.OutputBytes;
                lattices.Add(item: next);
                tickets.Add(item: new SliceTicket(Generation: slice.Generation, Lattice: lattice, Slice: slice));
                m_takenPoses.Add(item: (0, ReadOnlyMemory<Vector4>.Empty));
                m_takenKeys.Add(item: null);
                // A slice samples any instance, so it reads every field at the bake's set.
                for (var handle = 0; (handle < m_sampled.Count); handle++) { m_takenSampled[handle] = m_sampled[handle]; }
            }
            var (flying, working, admitted, awaiting, waiting) = (pending ? m_tally.Read() : default);

            if (pending) {
                if (m_parked.Count > 0) { ResumeParked(); }
                m_accounts.Takes++;
                m_accounts.WaitingGroups += waiting;
                m_accounts.FlyingGroups += flying;
                m_accounts.WorkingGroups += working;
            }
            var stop = 0;
            // The first group the account defers holds its place: no group behind it starts its final lattice
            // before it does, so a large group never starves behind a stream of smaller ones.
            var admissionWaits = false;
            var queued = -1;
            var ready = m_readyHeap;
            var wanting = m_wantingHeap;

            if (waiting > 0) {
                // While the pool holds the stages an admission's lead would only wait behind, no group can begin its final
                // lattice, so the groups wanting an admission are not read at all.
                if (admitted > 0) {
                    queued = QueuedStages();
                    admissionWaits = Backlogged(awaiting: awaiting, queued: queued);
                }
                m_tally.Offered(includeWanting: !admissionWaits, ready: m_offeredReady, wanting: m_offeredWanting);
                Rank(groups: m_offeredReady, heap: ready, owedOnly: owedOnly);
                Rank(groups: m_offeredWanting, heap: wanting, owedOnly: owedOnly);
            }
            // The better ranked of the two sets' next groups, each set in its own order, and the plan's order among equals.
            while (true) {
                (float Priority, int Index) readyAt = default;
                (float Priority, int Index) wantingAt = default;

                var haveReady = ready.TryPeek(element: out _, priority: out readyAt);
                var haveWanting = (!admissionWaits && wanting.TryPeek(element: out _, priority: out wantingAt));

                if (!haveReady && !haveWanting) { break; }

                var group = ((haveReady && (!haveWanting || (ByRank.Compare(x: readyAt, y: wantingAt) <= 0))) ? ready.Dequeue() : wanting.Dequeue());

                if (lattices.Count >= SdfWorldPackage.MeshBakeBatch) { stop = 3; break; }

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
                // A batch's first lattice is taken unless it must fit too, and no lattice holds more than LatticeTaps.
                if (((lattices.Count > 0) || firstFits) && ((taps + next.Taps) > mostTaps)) {
                    if (lattices.Count == 0) { heldTaps = next.Taps; }
                    stop = 1;
                    break;
                }
                // A lattice whose answer the readback reservation has no room left for waits until earlier answers end
                // (ReadbackBytes), and a smaller one behind it may fit; it is not admitted before then.
                if ((readback + next.OutputBytes) > ReadbackRoom) {
                    stop = 4;
                    continue;
                }
                // A group starting its final lattice holds that lattice and everything meshed from it until it lands,
                // so the process account admits its simultaneous peak; the rest keep their small refining lattices.
                // A first slab returned unanswered keeps the admission it took.
                if ((next.Slab == 0) && !group.Admitted) {
                    if (admissionWaits) { continue; }
                    // A group admitted while the pool already holds the stages its workers drain in an admission's
                    // lead would only wait there holding its lattice: admission waits for the pool (Backlogged).
                    if (queued < 0) { queued = QueuedStages(); }
                    if ((admitted > 0) && Backlogged(awaiting: awaiting, queued: queued)) {
                        admissionWaits = true;
                        continue;
                    }

                    var peak = Math.Max(val1: PeakOf(final: group.Final!), val2: group.PeakFloor);

                    // The account's host pool paces admission: its budget, and the machine's free memory as the
                    // account reads it.
                    if (!group.Memory.TryReserve(peak: peak)) {
                        // With nothing of the bake admitted, the group is admitted whatever the pool holds, as the
                        // process's one forced job, so the bake always progresses; while another bake holds that
                        // allowance this one waits for it, as it waits for room. The library's cache stays: the
                        // groups following a leader take its mesh from there rather than baking it again.
                        if ((admitted > 0) || !group.Memory.TryReserveForced(peak: peak)) {
                            if (!group.DeferredReported) { SdfMeshBakeReport.Deferred(admitted: admitted, peak: peak, residency: Residency, selector: group.Selector); group.DeferredReported = true; }
                            admissionWaits = true;
                            continue;
                        }
                        if (!group.DeferredReported) { SdfMeshBakeReport.AdmittedAlone(peak: peak, residency: Residency, selector: group.Selector); }
                    }
                    group.DeferredReported = false;
                    group.Admitted = true;
                    group.MarkAdmitted(timestamp: Stopwatch.GetTimestamp());
                    admitted++;
                    awaiting++;
                }
                var keys = SampleKeysOf(group: group);

                if (ProbeSamples(group, in next, keys)) { continue; }
                group.InFlight = true;
                group.TakenSamples += next.Samples;
                samples += next.Samples;
                taps += next.Taps;
                readback += next.OutputBytes;
                lattices.Add(item: next);
                tickets.Add(item: group);
                m_takenPoses.Add(item: (group.PoseFirst, group.PoseRows));
                m_takenKeys.Add(item: keys);
                foreach (var (handle, snapshot) in group.SampledSnapshots) { m_takenSampled[handle] = snapshot; }
            }
            // The heaps hold no group between takes, so none outlives the list that held it.
            ready.Clear();
            wanting.Clear();
            if (pending) {
                if (lattices.Count == 0) { m_accounts.IdleTakes++; }
                switch (stop) {
                    case 1: m_accounts.StopTaps++; break;
                    case 2: m_accounts.StopSamples++; break;
                    case 3: m_accounts.StopBatch++; break;
                    case 4: m_accounts.StopReadback++; break;
                    default: m_accounts.StopDrained++; break;
                }
            }
            if (lattices.Count == 0) { return false; }

            var sampled = new (int Handle, SdfSampledSnapshot Snapshot)[m_takenSampled.Count];
            var sampledAt = 0;

            foreach (var (handle, snapshot) in m_takenSampled) { sampled[sampledAt++] = (handle, snapshot); }
            GrantReadback(bytes: readback);
            batch = new SdfMeshBakeBatch(Lattices: [.. lattices], Tickets: [.. tickets]) {
                Bake = this,
                Readback = readback,
                Poses = [.. m_takenPoses],
                Keeps = [.. m_takenKeys],
                Sampled = sampled,
                SampledHandles = (m_program?.SampledFields.Count ?? 0),
            };
            m_accounts.Batches++;
            Library.Cache.Add(amount: batch.Lattices.Length, kind: SdfMeshCache.Dispatches);
            foreach (var ticket in batch.Tickets) {
                if (ticket is Group { Live: true }) { Library.Cache.Count(kind: SdfMeshCache.LiveDispatches); }
            }
            foreach (var lattice in batch.Lattices) {
                m_accounts.Dispatches++;
                m_accounts.Samples += lattice.Samples;
                m_accounts.Taps += lattice.Taps;
            }
            return true;
        }
    }

    private readonly List<(int First, ReadOnlyMemory<Vector4> Rows)> m_takenPoses = [];
    private readonly List<SampleKeys?> m_takenKeys = [];
    // The snapshots the batch being taken samples, by handle.
    private readonly Dictionary<int, SdfSampledSnapshot> m_takenSampled = [];

    // Whether a group's snapshots agree with the batch being taken, handle by handle.
    private bool SamplesAlike(Group group) {
        foreach (var (handle, snapshot) in group.SampledSnapshots) {
            if (m_takenSampled.TryGetValue(key: handle, value: out var taken) && !ReferenceEquals(objA: taken, objB: snapshot)) { return false; }
        }
        return true;
    }

    // The order a take offers groups in. Invariant: a take offers its candidates in the order a sort of them by ByRank
    // would give, each at the rank it holds when the take reads it. A take reads only the groups the tally holds as
    // offering (GroupTally), into two heaps (the ready, and the wanting an admission) it takes the better top of, so
    // the cost of a take is the groups holding a lattice, and a rank changing while Prioritize measures it needs no
    // index kept in step.
    private readonly List<Group> m_offeredReady = [];
    private readonly List<Group> m_offeredWanting = [];
    private readonly PriorityQueue<Group, (float Priority, int Index)> m_readyHeap = new(comparer: ByRank);
    private readonly PriorityQueue<Group, (float Priority, int Index)> m_wantingHeap = new(comparer: ByRank);

    // Puts the groups a take may take a lattice of into a heap by their ranks.
    private static void Rank(List<Group> groups, PriorityQueue<Group, (float Priority, int Index)> heap, bool owedOnly) {
        foreach (var group in groups) {
            if (Offers(group: group, owedOnly: owedOnly)) { heap.Enqueue(element: group, priority: (RankOf(group: group), group.Index)); }
        }
    }
    // Whether a take may take a lattice of a group: one it owes, with none in flight or being worked, and, for an owed
    // take, owed to the frame.
    private static bool Offers(Group group, bool owedOnly) =>
        !(group.Done || group.InFlight || group.Contouring || (group.Next is null) || (owedOnly && !OwesFrame(group: group)));

    // A group's rank among a take's candidates: every group in view (Group.InView) ahead of every group out of it, each
    // side by its priority. A priority is never negative, so an out-of-view group's rank is taken into [-1, 0) with its
    // order kept.
    private static float RankOf(Group group) => (group.InView ? group.Priority : (-1f / (1f + group.Priority)));
    // Higher priority first, then the plan's order.
    private static readonly SdfMeshPriorityOrder ByRank = new();

    /// <summary>Returns a taken batch's lattices still unanswered to their groups, as when its dispatch never completed or
    /// its answer stopped part way; each group dispatches its lattice again. Their samples count as discarded when the GPU
    /// sampled them, and as returned otherwise. A lattice already answered or returned stays as it is.</summary>
    /// <param name="batch">The batch <see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long)"/> took, or <see langword="null"/>.</param>
    /// <param name="sampled">Whether the batch's dispatch completed, so its samples were taken and are dropped.</param>
    public void Abandon(SdfMeshBakeBatch? batch, bool sampled = false) {
        if (batch is null) { return; }
        // A batch abandoned before its samples were copied gives back the readback bytes its take granted it; a copy
        // already made gives them back itself when it is freed.
        ReleaseReadback(bytes: batch.TakeReadback());
        using (EnterGate(site: BakeGate.Ticket)) {
            for (var index = 0; (index < batch.Tickets.Length); index++) { _ = MoveTicket(batch: batch, index: index, to: SdfMeshBakeTicket.Returned, sampled: sampled); }
        }
    }
    /// <summary>Answers a dispatched batch with the samples its readback copied, each lattice its own slice: each group's
    /// lattice is decoded on the calling thread and answered on the bake's workers. A lattice answered or returned already
    /// takes nothing; a decode failure returns its request, and <see cref="Abandon"/> returns every untaken answer after
    /// it.</summary>
    /// <param name="batch">The batch <see cref="TryTake(SdfProgram?, out SdfMeshBakeBatch?, long)"/> took.</param>
    /// <param name="samples">The output planes of each lattice, back to back, which the bake copies into the readback
    /// storage its take granted the batch before it answers any lattice.</param>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is shorter than the batch's output.</exception>
    public void Complete(SdfMeshBakeBatch batch, ReadOnlySpan<byte> samples) {
        var copy = CopySamples(batch: batch, fill: static (destination, source) => source.CopyTo(destination: destination), length: samples.Length, state: samples);

        try {
            Complete(batch: batch, samples: copy);
        } finally {
            copy.Release();
        }
    }
    // Answers a batch from its samples' copy (CopySamples), as the public Complete describes: each group's answer reads
    // its lattice's slice of the copy in place and holds a reference to the copy until the answer ends.
    internal void Complete(SdfMeshBakeBatch batch, AnsweredSamples samples) {
        ArgumentNullException.ThrowIfNull(argument: batch);

        var offset = 0;

        for (var index = 0; (index < batch.Lattices.Length); index++) {
            var lattice = batch.Lattices[index];
            var bytes = lattice.OutputBytes;
            var slice = samples.Span.Slice(length: bytes, start: offset);
            var at = offset;

            offset += bytes;
            // A group's lattice is answered off the caller's thread ahead of every other queued stage, reading its slice
            // of the copy in place; the group stays in flight until then, so the bake takes no lattice of it before its
            // answer lands. The queued job holds the copy until its answer ends and runs even for a retired group, so the
            // copy is always let go.
            // Invariant: a dispatched request and the group workspace its points lie in (a final lattice's point
            // buffer, the group's crease and material points) belong to whoever holds its ticket: the take until
            // dispatch, this reader while the ticket is Decoding, and the group's answer job once it is Answered. That
            // job runs the group's next stage, which rewrites or frees those points (ChooseCrossings, Contour), so the
            // reader reads the request and the group only inside Receive, before the ticket moves on; what the reader
            // needs of the group beyond that the take fixed in the batch (Poses, Keeps).
            if (batch.Tickets[index] is Group group) {
                using (EnterGate(site: BakeGate.Ticket)) {
                    if (batch.States[index] != SdfMeshBakeTicket.Taken) { continue; }
                    if (!group.QueueWork()) {
                        _ = MoveTicket(batch, index, SdfMeshBakeTicket.Returned, sampled: true);
                        continue;
                    }
                    _ = MoveTicket(batch, index, SdfMeshBakeTicket.Decoding);
                }
                Answer answer;
                try {
                    answer = Receive(group, batch.Keeps[index], in lattice, samples, at);
                } catch {
                    // No next lattice is published until AnswerGroup. Return the claimed request for retry.
                    using (EnterGate(site: BakeGate.Ticket)) {
                        _ = MoveTicket(batch, index, SdfMeshBakeTicket.Returned, from: SdfMeshBakeTicket.Decoding, sampled: true);
                    }
                    group.EndWork();
                    throw;
                }
                using (EnterGate(site: BakeGate.Ticket)) { _ = MoveTicket(batch, index, SdfMeshBakeTicket.Answered, from: SdfMeshBakeTicket.Decoding); }
                Enqueue(group: group, urgent: true, work: () => {
                    using (answer) { AnswerGroup(answer: answer, group: group, request: in lattice); }
                });
                continue;
            }
            Complete(batch: batch, index: index, request: in lattice, samples: slice);
        }
        // A slice's answer readies its next lattice with no stage queued for it.
        Offer();
    }

    // The one transition of a batch's ticket out of Taken, which answering and abandoning both take: Answered once its
    // samples reach its group or slice (the group stays in flight until its answer job runs), Returned when they never will,
    // which hands the lattice back to be taken again: its samples are discarded when the GPU took them (`sampled`), GPU
    // work spent for nothing, and returned when it never did. Under the gate; false when the ticket had left Taken already.
    private bool MoveTicket(SdfMeshBakeBatch batch, int index, SdfMeshBakeTicket to, SdfMeshBakeTicket from = SdfMeshBakeTicket.Taken, bool sampled = false) {
        if (batch.States[index] != from) { return false; }
        batch.States[index] = to;
        if (to != SdfMeshBakeTicket.Returned) { return true; }
        if (batch.Tickets[index] is Group group) { group.InFlight = false; }
        if ((batch.Tickets[index] is SliceTicket ticket) && ReferenceEquals(objA: ticket.Slice, objB: m_slice) && (ticket.Generation == ticket.Slice.Generation)) {
            ticket.Slice.Return(ticket: ticket.Lattice);
        }
        if (sampled) { m_accounts.Discarded += batch.Lattices[index].Samples; } else { m_accounts.Returned += batch.Lattices[index].Samples; }
        return true;
    }
    // Answers a field slice's lattice.
    private void Complete(SdfMeshBakeBatch batch, int index, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) {
        if (batch.Tickets[index] is SliceTicket sliced) {
            bool done;

            using (EnterGate(site: BakeGate.Slice)) {
                if (!MoveTicket(batch: batch, index: index, to: SdfMeshBakeTicket.Answered)) { return; }
                // A slice a new program restarted, or one already finished, takes no answer from an earlier dispatch.
                if (!ReferenceEquals(objA: sliced.Slice, objB: m_slice) || (sliced.Slice.Generation != sliced.Generation)) { return; }
                done = sliced.Slice.Answer(samples: samples, ticket: sliced.Lattice);
            }
            if (done) {
                sliced.Slice.Complete();
                using (EnterGate(site: BakeGate.Slice)) { m_slice = null; }
            }
        }
    }

    // One lattice's answer, read in place from its slice of the answered samples (AnsweredSamples), whose reference it
    // holds until its group's answer job disposes it: each sample's value and material, or its value and gradient, or,
    // for a points lattice without gradients, the slot that won at the point. A final lattice's slab answers nothing
    // here: the reader copied it into the lattice (Decode).
    private sealed class Answer(AnsweredSamples samples, int offset, in SdfMeshBakeRequest request) : IDisposable {
        private readonly AnsweredSamples m_samples = samples.Share();
        private readonly int m_offset = offset;
        private readonly int m_length = request.OutputBytes;
        private readonly int m_count = request.Samples;
        private readonly bool m_slab = (request.Slab >= 0);
        private readonly bool m_materialOnly = request.MaterialOnly;
        private readonly bool m_withMaterial = request.WithMaterial;
        private readonly bool m_withGradient = request.WithGradient;

        private ReadOnlySpan<int> Words => MemoryMarshal.Cast<byte, int>(m_samples.Span.Slice(length: m_length, start: m_offset));

        public ReadOnlySpan<float> Values => ((m_slab || m_materialOnly) ? default : MemoryMarshal.Cast<int, float>(Words[..m_count]));
        public ReadOnlySpan<int> Materials => (m_slab ? default : (m_materialOnly ? Words : (m_withMaterial ? Words[m_count..] : default)));
        public ReadOnlySpan<float> Gradients => ((!m_slab && !m_materialOnly && m_withGradient) ? MemoryMarshal.Cast<int, float>(Words[m_count..]) : default);

        public void Dispose() => m_samples.Release();
    }

    // Everything a reader does with a group's lattice while its ticket is Decoding (Complete's invariant): decodes its
    // samples and keeps them under the keys its take recorded, whose key reads the request's points.
    private Answer Receive(Group group, SampleKeys? keys, in SdfMeshBakeRequest request, AnsweredSamples samples, int offset) {
        var answer = Decode(group, in request, samples, offset);

        try {
            if (keys is not null) { KeepSamples(keys: keys, request: in request, samples: samples.Span.Slice(length: request.OutputBytes, start: offset)); }
            return answer;
        } catch {
            answer.Dispose();
            throw;
        }
    }

    // The ticket (or checkpoint worker) owns this group's slab until AnswerGroup publishes its successor. A slab is copied
    // into the group's final lattice, which its admission covers; every other answer reads the samples in place.
    private static Answer Decode(Group group, in SdfMeshBakeRequest request, AnsweredSamples samples, int offset) {
        if (request.Slab >= 0) {
            var count = request.Samples;
            var words = MemoryMarshal.Cast<byte, int>(samples.Span.Slice(length: request.OutputBytes, start: offset));
            var first = request.Slab * request.Dims.X * request.Dims.Y;

            MemoryMarshal.Cast<int, float>(words[..count]).CopyTo(group.Final!.Values[first..]);
            words[count..].CopyTo(group.Final.Materials[first..]);
        }
        return new Answer(offset: offset, request: in request, samples: samples);
    }
    // Answers one lattice of a group on a worker: a refining lattice narrows its group's box, a final slab fills its
    // lattice, and a points lattice answers its stage. Each handler works on the group's own storage outside the gate,
    // where only the group's one running job reads it, and takes the gate only to publish the group's next step.
    private void AnswerGroup(Group group, in SdfMeshBakeRequest request, Answer answer) {
        var answered = Stopwatch.GetTimestamp();
        var values = answer.Values;
        var materials = answer.Materials;

        if (!request.Points.IsEmpty) {
            if (group.Final is { } final) {
                AnswerFinal(final: final, gradients: answer.Gradients, group: group, materials: materials, request: in request, values: values);
            } else if (group.Creases is not null) {
                AnswerCreases(gradients: answer.Gradients, group: group, request: in request, values: values);
            } else {
                AnswerPoints(group: group, kind: 6, materials: materials, request: in request);
            }
        } else if (request.Slab >= 0) {
            AnswerSlab(group, in request);
        } else {
            Refine(group: group, request: in request, values: values);
        }
        Account(since: answered, stage: BakeStage.Answer);
    }

    /// <summary>Describes the bake: its groups, the work it dispatched, and its meshes' triangles, vertices and bytes,
    /// with the wall time from its start to its last mesh.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        using (EnterGate(site: BakeGate.Read)) {
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

            return $"[sdf.mesh-bake] residency={Residency} groups={m_groups.Count} meshes={meshes} pending={m_groups.Count(predicate: static group => !group.Done)} dispatches={m_accounts.Dispatches} samples={m_accounts.Samples} discarded={m_accounts.Discarded} returned={m_accounts.Returned} taps={m_accounts.Taps} refined={m_accounts.RefinedCells} edges={m_accounts.CutEdges} softened={m_accounts.Softened} merged={m_accounts.Merged} loaded={m_accounts.Loaded} skinned={m_groups.Count(predicate: static group => (group.Mesh is { VertexMembers.IsEmpty: false }))} triangles={triangles} vertices={vertices} bytes={bytes} seconds={(m_reported ? m_seconds : m_clock.Elapsed.TotalSeconds):0.000} {m_accounts.DescribeCheckpoints()}";
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
        // The selectors whose groups draw something and the shapes the bake holds, as the frame's thread left them:
        // every other selector, and every group of another shape, arrives and plans its floor first, as the restart
        // that adopts this plan decides from the same groups (Arrives).
        HashSet<int> drawing;
        HashSet<UInt128> shapes;

        using (EnterGate(site: BakeGate.Read)) {
            drawing = [.. m_groups.Where(predicate: static group => !Arrives(old: group)).Select(selector: static group => group.Selector)];
            shapes = [.. m_groups.Select(selector: static group => group.Shape)];
        }

        var groups = SdfMeshBakeGroup.Plan(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: farDistance,
            program: program,
            tier: Tiers(arrives: group => (!drawing.Contains(item: group.Selector) || NewShape(planned: group, shapes: shapes)), copies: false, transforms: transforms),
            transforms: transforms
        );

        var identity = Identity.Value;

        foreach (var group in groups) {
            if (Library.TryTake(geometry: group.Sealed, identity: identity, materials: group.MaterialOrder, mesh: out var warmed)) { warmed.Release(); }
        }

        using (EnterGate(site: BakeGate.Read)) {
            m_prepared = (program, farDistance, groups);
            m_planMilliseconds = Stopwatch.GetElapsedTime(startingTimestamp: planned).TotalMilliseconds;
        }
    }

    // Puts a group at an index of the list, under the gate: with Restart's clear, the list's one writer, so it moves the
    // product revision the dresser stands its group walk on (SdfMeshDresser).
    //
    // Invariant: a reader's newest plan (Group.Successor) and its liveness (Group.Live) belong to its place in the list,
    // not to the group that held the place when Revisit planned it. A tier's replacement taking the place, the group it
    // replaced taking it back (GiveBack) and an evicted floor all pass here, so each carries the successor and the
    // liveness of the group it displaces, and m_successors counts only successors Promote can reach.
    private void Replace(int index, Group group) {
        var displaced = m_groups[index];

        m_allResident = false;
        m_floorRevision++;
        group.Owner = this;
        if (!ReferenceEquals(objA: displaced, objB: group)) {
            group.InheritLive(from: displaced);
            if (displaced.Successor is { } successor) {
                displaced.Successor = null;
                // The displaced group's successor holds the newest inputs; one the incoming group held is older.
                if (group.Successor is not null) { m_successors--; }
                group.Successor = successor;
            }
        }
        // A group planned in a listed group's place ranks as that group last did, until a sweep measures it.
        if (!ReferenceEquals(objA: displaced, objB: group) && displaced.Listed) {
            group.Priority = displaced.Priority;
            group.InView = displaced.InView;
        }
        m_groups.Set(group: group, index: index);
        // A group set into a place may draw another tier than the one it demands: the tier decisions read it.
        Attend(group: group);
        Touch(group: group);
        if (!ReferenceEquals(objA: displaced, objB: group)) {
            Touch(group: displaced);
            CountHoles(group: displaced, listed: false);
        }
        CountHoles(group: group, listed: true);
        m_doneThrough = Math.Min(val1: m_doneThrough, val2: index);
    }
    // Starts the bake of a new program: one group per dynamic slot and per run of static instances baked together (one rigid
    // copy), each starting at the coarse lattice over its bounding box. The meshes of the previous groups keep drawing until
    // each new mesh lands. A frame of the same program whose frame inputs moved plans their readers again instead (Revisit).
    private void Restart(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var previous = m_groups.ToDictionary(keySelector: static group => group.Selector);
        var identity = Identity.Value;
        var program = frame.Program;
        var sampled = frame.Sampled.Baked;
        var plan = (((m_prepared is { } prepared) && ReferenceEquals(objA: prepared.Program, objB: program) && (prepared.FarDistance == frame.FarDistance)) ? prepared.Groups : null);

        m_allResident = false;
        Adopt(program: program);
        m_sampled = sampled;
        m_groups.Clear();
        TouchAll();
        m_doneThrough = 0;
        // A slice samples one program and set: a new one takes every lattice again.
        m_slice?.Reset();
        // A successor waits on a group of the previous program; this program's plan is made at the frame's set already.
        foreach (var old in previous.Values) {
            old.Successor = null;
            // A rung being filled that this program keeps is the tier its plan asks: it lands as any replacement does.
            old.Fill = false;
        }
        m_successors = 0;

        // A new program takes the plan a build already made off the frame's thread (PlanAhead), with each group this restart
        // cannot keep that read the transforms or a moved field planned again at the frame's; otherwise the whole plan.
        var shapes = previous.Values.Select(selector: static group => group.Shape).ToHashSet();
        var tiers = Tiers(arrives: group => PlansFloor(old: previous.GetValueOrDefault(key: group.Selector), planned: group, shapes: shapes), transforms: frame.DynamicTransforms);

        if (plan is not null) {
            // A group that read a transform is planned again unless the previous program keeps it; a static group that
            // read none is planned again only when the frame's demand moved its tier.
            _ = SdfMeshBakeGroup.Refresh(
                create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
                farDistance: frame.FarDistance,
                groups: plan,
                program: program,
                sampled: sampled,
                stale: group => ((group.Dynamic || group.Geometry.ReadsSlots)
                    ? !(previous.TryGetValue(key: group.Selector, value: out var old) && Keeps(group: group, identity: identity, old: old))
                    : Retiered(group: group, tiers: tiers)),
                tier: tiers,
                transforms: frame.DynamicTransforms
            );
            m_groups.Plan(groups: plan);
        } else {
            m_groups.Plan(groups: SdfMeshBakeGroup.Plan(
                create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
                farDistance: frame.FarDistance,
                program: program,
                sampled: sampled,
                tier: tiers,
                transforms: frame.DynamicTransforms
            ));
        }
        m_prepared = null;
        m_restarts++;
        m_restartGroups += m_groups.Count;

        // A program rebuilt with a group's instructions unchanged keeps that group, its bake and any lattice in flight; only
        // a group whose geometry moved bakes again (Place).
        var started = new PlaceCounts();
        // The group of this bake baking each geometry, which every later group of that geometry waits on (Follow).
        var leaders = new Dictionary<SdfMeshGeometry, Group>();

        for (var index = 0; (index < m_groups.Count); index++) {
            _ = Place(group: m_groups[index], identity: identity, index: index, leaders: leaders, old: previous.GetValueOrDefault(key: m_groups[index].Selector), started: ref started);
        }
        // A previous group this program does not keep is retired: its queued work is dropped (TakeWork).
        var planned = new HashSet<Group>(collection: m_groups, comparer: ReferenceEqualityComparer.Instance);

        foreach (var old in previous.Values) {
            if (!planned.Contains(item: old)) { m_retiredGroups.Add(item: old); old.Retired = true; }
        }
        ListReaders();
        ListKeepers();
        BeginOrphans(indices: null, started: ref started);
        m_firstDetailsOwed = m_groups.Count(predicate: static group => group.FirstDetailOwed);
        Prioritize(whole: true);
        BeginAccounts(cause: "program", fresh: true, instances: program.Instances.Count, restarted: restarted, started: started);
        m_restartMilliseconds = Stopwatch.GetElapsedTime(startingTimestamp: restarted).TotalMilliseconds;
    }
    // Starts each successor whose group has landed in that group's place (Revisit): the landed mesh draws until the
    // successor's lands.
    private void Promote(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var identity = Identity.Value;
        var started = new PlaceCounts();
        Dictionary<SdfMeshGeometry, Group>? leaders = null;

        foreach (var index in m_inputReaders) {
            var old = m_groups[index];

            if (!old.Done || (old.Successor is not { } plan)) { continue; }
            m_promoted++;
            _ = Succeed(identity: identity, index: index, leaders: (leaders ??= Leaders()), old: old, plan: plan, started: ref started);
        }
        if (leaders is null) { return; }
        BeginOrphans(indices: m_inputReaders, started: ref started);
        BeginAccounts(cause: "promote", fresh: false, instances: frame.Program.Instances.Count, restarted: restarted, started: started);
    }
    // Puts a reader's newer plan in the place of the reader, which has landed (Place), dropping any successor the reader
    // held, which the plan replaces, and retiring the reader once its place holds another group. The one door a replanned
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
    // snapshot and operand poses, so it is a reader too: the readers alone hold every leader a replanned group can follow.
    private Dictionary<SdfMeshGeometry, Group> Leaders() {
        var leaders = new Dictionary<SdfMeshGeometry, Group>();

        foreach (var index in m_inputReaders) {
            var group = m_groups[index];

            if (!group.Done && (group.Leader is null) && (group.Replaces is null)) { _ = leaders.TryAdd(key: group.Sealed, value: group); }
        }
        return leaders;
    }

    // What the groups a restart, resample or promotion placed began: a bake, a load from the store, a wait on another
    // group of the same geometry in this restart (Shared, Place), or a wait through the library on a group outside it
    // (Joined, Start).
    private struct PlaceCounts {
        public int Baking;
        public int Loading;
        public int Shared;
        public int Joined;
        // The first groups begun, which the start line names.
        public List<SdfMeshBakeReport.Begun>? Named;

        public void Begun(Group group, Started how) {
            if (how == Started.Joined) {
                Joined++;
                return;
            }
            var loading = (how == Started.Loading);

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
        // Before StoredRung asks whether the group may bake: a later program's plan of a moved sampled reader is live.
        if (old is not null) { group.InheritLive(from: old); }
        if ((old is not null) && Keeps(group: group, identity: identity, old: old)) {
            old.AdoptSampled(planned: group);
            // A group kept mid-bake carries on under the new program: its checkpoints are keyed again under it, so the
            // stages and lattices still to come are kept where a later bake of this program looks. Its sealed geometry,
            // and so its mesh, is the same under either program, as the library's key says.
            if (!old.Done && (old.Checkpoints is { Samples.Store: var store } keys) && (keys.Field != m_field)) { KeyCheckpoints(old, store); }
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
            ShowReplaced(group: group, old: old);
            group.Mesh = baked;
            group.Done = true;
            return true;
        }
        // Geometry an earlier bake found no surface in meshes nothing again.
        if (Library.HoldsEmpty(geometry: group.Sealed, identity: identity)) {
            group.FoundNoSurface(how: ", as baked before");
            group.Done = true;
            return true;
        }
        // A dynamic group of a form the library holds a mesh of, baked with its other slots at another pose (a body
        // crossing from another world mid-stride, or wearing a creation another body wears), takes that skinned mesh,
        // whose skin carries each member through its own slot. An operand's snapped pose is in the form, so a group
        // reading one takes only a mesh cut where its operand stands.
        if (group.Dynamic && Library.TryTakeForm(form: group.Form, identity: identity, materials: group.MaterialOrder, mesh: out var shaped)) {
            ShowReplaced(group: group, old: old);
            group.Mesh = shaped;
            group.Done = true;
            group.Stored = true;
            return true;
        }

        // A bake that draws stored rungs alone puts the nearest stored rung of the group's ladder in its place, and
        // places that rung as it would any plan.
        if (StoredRung(identity: identity, planned: group) is { } rung) {
            group.Retired = true;
            return Place(group: rung, identity: identity, index: index, leaders: leaders, old: old, started: ref started);
        }
        ShowReplaced(group: group, old: old);
        if (leaders.TryGetValue(key: group.Sealed, value: out var leader)) {
            group.Leader = leader;
            started.Shared++;
            return true;
        }
        leaders.Add(key: group.Sealed, value: group);
        started.Begun(group: group, how: Start(group: group));
        return true;
    }
    // Has a group newly put in a landed group's place draw what that group draws until its own mesh takes the draws
    // (Group.Mesh): a static mesh lies in world space and a dynamic one in its slot's frame, so only a group of the
    // same kind can.
    // It keeps the replaced group's floor (Group.KeepFloorOf) too: a floor of a geometry since replanned still stands
    // in for the group until its own lands, rather than a hole.
    private static void ShowReplaced(Group group, Group? old) {
        if ((old is null) || (old.Dynamic != group.Dynamic)) { return; }
        group.KeepFloorOf(old: old);
        if (old.Shown is { } shown) {
            group.Mesh = shown.Acquire();
            group.MarkStandIn();
        }
    }
    // Starts each group, of the listed indices or every one, still waiting on a leader the bake has retired: it starts its
    // geometry itself (Start).
    private void BeginOrphans(IReadOnlyList<int>? indices, ref PlaceCounts started) {
        var count = (indices?.Count ?? m_groups.Count);

        for (var at = 0; (at < count); at++) {
            var group = m_groups[(indices?[at] ?? at)];

            if (group.Done || (group.Leader is not { Retired: true })) { continue; }
            group.Leader = null;
            started.Begun(group: group, how: Start(group: group));
        }
    }
    // Opens the accounts and the start line of the work a restart, resample or promotion began, if any: a new program's, or
    // any once the bake before it reported, whose counts and clock start again; otherwise work added to the bake still
    // running, whose done line then covers it. A bake that began nothing and has every group done reports here or never.
    private void BeginAccounts(string cause, bool fresh, int instances, long restarted, PlaceCounts started) {
        var begun = (((started.Baking + started.Loading) + started.Shared) + started.Joined);

        if ((begun > 0) && (fresh || m_reported || !m_clock.IsRunning)) {
            m_accounts.Clear();
            m_accounts.Joined = started.Joined;
            m_reported = false;
            m_clock.Restart();
            m_bakeStarted = restarted;
            m_loadsQueued = started.Loading;
            m_loadsDone = 0;
            m_loadFirst = ((started.Loading > 0) ? restarted : 0);
            m_loadLast = 0;
            SdfMeshBakeReport.Start(baking: started.Baking, cause: cause, disk: Library.Disk, dynamic: m_groups.Count(predicate: static group => group.Dynamic), groups: m_groups.Count, identity: Identity, instances: instances, joined: started.Joined, loading: started.Loading, named: started.Named, residency: Residency, residencyId: ResidencyId, shared: started.Shared);
        } else if (begun > 0) {
            m_accounts.Joined += started.Joined;
            m_loadsQueued += started.Loading;
            if ((started.Loading > 0) && (m_loadFirst == 0)) { m_loadFirst = restarted; }
        }
        if (m_clock.IsRunning) { Finish(); }
    }
    // Whether a new program's group stays the group the previous program planned at its selector: the same geometry and
    // materials, or, for a dynamic group, the same form and materials, which are the same shapes in another relative pose,
    // so the skinned mesh drawing (or baking) carries on and rides each member's slot. A group the previous program planned
    // under another bake identity holds a product of other code or kernels, so it is never kept.
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
    // Starts a group the library holds no mesh of at the coarse lattice over its box, under the bake's identity. A group
    // whose box is fitted to its surface (SdfMeshBakeGroup.Fitted) starts at its final lattice instead: the box is
    // widened by one cell of the lattice a search would end on, as a searched box is, so the surface lies off the final
    // lattice's edge, and no coarse lattice, which would step over features thinner than its cells, narrows it. Geometry
    // the library's store kept from an earlier run loads from disk off the frame's thread instead, and bakes only when the
    // store refuses its entry; true when it loads.
    private bool Begin(Group group) {
        group.Allocator = Library.Allocator;
        group.TakeDemandBox();
        if (group.Fitted) {
            var cell = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: RefineMinimum).Cell;

            group.Low -= cell;
            group.High += cell;
            StartFinal(group: group);
        } else {
            // The coarse lattice is a cube about the bounding box's center, so its cells are cubes.
            var center = ((group.Low + group.High) * 0.5f);
            var half = (MathF.Max(x: (group.High.X - group.Low.X), y: MathF.Max(x: (group.High.Y - group.Low.Y), y: (group.High.Z - group.Low.Z))) * 0.5f);

            group.Low = (center - new Vector3(value: half));
            group.High = (center + new Vector3(value: half));
            group.Next = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: CoarseSamples);
        }
        group.Identity = Identity.Value;
        if (Identity.HasParts && Library.Disk is { } disk) { KeyCheckpoints(group, disk); }
        if (!Library.MayHold(geometry: group.Sealed, identity: group.Identity) && !MayHoldCheckpoint(group)) {
            // A dynamic group whose form the store holds a mesh of, baked at another pose, loads that mesh, as one the
            // library holds is taken (Place): its skin carries each member through its own slot.
            if (!HoldsForm(group: group)) {
                Library.Cache.Count(kind: (group.Live ? SdfMeshCache.LiveBaked : SdfMeshCache.GroupsBaked));
                return false;
            }
            group.LoadsForm = true;
        }
        group.Deferred = group.Next;
        group.Next = null;
        group.Contouring = true;
        ScheduleLoad(group: group);
        return true;
    }
    // The lattice over a group's box: `longest` samples along its longest axis and the same spacing along the others,
    // never fewer than `minimum` samples along any. A final lattice's spacing is never finer than MinimumCell, so a small
    // part meshes at the detail a view resolves rather than at its own scale.
    private static SdfMeshBakeRequest Lattice(Group group, int longest, int minimum, bool gradients) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var target = (MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) / (longest - 1));

        if (gradients) { target = MathF.Max(x: target, y: MinimumCell); }

        var (x, y, z) = SdfMeshLattice.Dims(cell: target, extent: extent, minimum: minimum);

        return Spanning(dims: (Math.Min(val1: x, val2: FinalSamples), Math.Min(val1: y, val2: FinalSamples), Math.Min(val1: z, val2: FinalSamples)), extent: extent, gradients: gradients, group: group);
    }
    // The request for a lattice of `dims` samples spanning a group's box of `extent` from its low corner, each axis's
    // spacing the extent over its cells.
    private static SdfMeshBakeRequest Spanning(Group group, Vector3 extent, (int X, int Y, int Z) dims, bool gradients) {
        var cell = new Vector3(x: (extent.X / (dims.X - 1)), y: (extent.Y / (dims.Y - 1)), z: (extent.Z / (dims.Z - 1)));

        return RequestOf(cell: cell, dims: dims, gradients: gradients, group: group, origin: group.Low);
    }
    // A lattice of a group: the one place a request takes its group's selector, frame, instance run and glyph
    // thickening (SdfMeshBakeRequest.Glyphs), so every lattice of the group samples one field.
    private static SdfMeshBakeRequest RequestOf(Group group, Vector3 cell, (int X, int Y, int Z) dims, bool gradients, Vector3 origin) =>
        new(Cell: cell, Dims: dims, Gradients: gradients, Origin: origin, Selector: group.Selector) {
            FrameSlot = (group.Dynamic ? group.Slot : -1),
            Glyphs = (group.GlyphCell, group.Geometry.GlyphStroke),
            Instances = group.Instances,
        };
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
        if (empty) { StoreEmpty(group); }
        using (EnterGate(site: BakeGate.Refine)) {
            group.InFlight = false;
            if (!GroupList.Holds(group: group)) { return; }
            m_accounts.KindSamples[0] += request.Samples;
            if (empty) {
                group.FoundNoSurface(how: $" after {group.Refinements} refinements");
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
                StartFinal(group: group);
                return;
            }
            group.Next = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: RefineMinimum);
        }
    }
    // Gives a group its final lattice over its box as it stands, a union's for several members, and makes that lattice's
    // first slab its next request. The lattice holds its description alone until that slab is answered (FinalLattice).
    private static void StartFinal(Group group) {
        group.Final = ((group.Members.Count > 1) ? UnionFinal(group: group) : OwnFinal(group: group));
        group.Next = SlabOf(group: group);
    }
    // The final lattice of a group of one member (SdfMeshBakeGroup.Members): FinalSamples along its longest axis, no finer
    // than MinimumCell. An instance that declares its own detail (SdfInstanceRange.Detail), static or riding a slot, refines
    // its bound's part of the box toward that spacing wherever the surface can enter a cell, as finely as
    // MaxDetailRefinement allows. A fitted group (SdfMeshBakeGroup.Fitted: a run of text, whose box is its glyphs') whose
    // lattice that refinement cannot take to its detail is spaced MaxDetailRefinement details apart instead, as budgeted
    // as a union's (BudgetedLattice), so its refinement reaches what it declares: a line of lettering is long and thin, and
    // a feature narrower than a fine cell is lost or bridged by the contour. Its box holds its surface, so the finer
    // lattice costs only the cells that box spans. Its glyphs (SdfMeshBakeGroup.Glyphs, or a fitted text group's own box)
    // are a region of their own at their own detail, in which every cell the surface can enter refines, since a glyph's
    // field is a bound past its atlas band (SdfLatticeRegion.Bound); the rest of the group refines toward its own detail,
    // so lettering engraved in a large creation refines that creation's surface no finer than the creation asks.
    private static FinalLattice OwnFinal(Group group) {
        // A fitted group holding glyphs is text alone: its glyphs' box is its own (a dynamic one's included), at its detail.
        var text = (group.Fitted && group.Geometry.ContainsGlyph);
        // Every cell of text the surface can enter refines (its glyph region is a bound's), so the slab's thin axis needs
        // only a sample through its middle; FinalMinimum there would have the refinement split cells already finer than
        // the stroke detail as many times again, spending the fine-sample budget across the slab's depth.
        // A tier past the group's floor coarsens the lattice itself, and its thin axes with it.
        var longest = FinalLongest(group: group);
        var minimum = Math.Min(val1: (text ? TextMinimum : FinalMinimum), val2: longest);
        var lattice = (Lattice(gradients: true, group: group, longest: longest, minimum: minimum) with { Gradients = false });
        var coarse = MathF.Max(x: lattice.Cell.X, y: MathF.Max(x: lattice.Cell.Y, y: lattice.Cell.Z));
        // The group's tier floors every spacing it asks: a view that resolves no finer cell is baked none.
        var tierCell = SdfMeshTier.Cell(tier: group.Tier);
        var glyphs = (group.Glyphs ?? ((text && (group.Detail > 0f)) ? new SdfGlyphSpan(Detail: group.Detail, High: group.High, Low: group.Low) : null));
        var detail = ((group.Detail > 0f) ? MathF.Max(x: group.Detail, y: tierCell) : float.PositiveInfinity);
        var glyphDetail = ((glyphs is { Detail: > 0f } span) ? MathF.Max(x: span.Detail, y: tierCell) : float.PositiveInfinity);
        var finest = MathF.Min(x: detail, y: glyphDetail);

        if (group.Fitted && float.IsFinite(f: finest) && ((finest * MaxDetailRefinement) < coarse)) {
            lattice = BudgetedLattice(finest: (finest * MaxDetailRefinement), group: group, minimum: minimum, widest: coarse);
            coarse = MathF.Max(x: lattice.Cell.X, y: MathF.Max(x: lattice.Cell.Y, y: lattice.Cell.Z));
        }

        // A detail at or past the lattice's own spacing is one the lattice already resolves, as a union's member's is.
        var regions = new List<SdfLatticeRegion>(capacity: 2);
        var low = Vector3.Max(value1: group.Low, value2: group.DetailLow);
        var high = Vector3.Min(value1: group.High, value2: group.DetailHigh);

        if ((detail < coarse) && (low.X <= high.X) && (low.Y <= high.Y) && (low.Z <= high.Z)) {
            regions.Add(item: new SdfLatticeRegion(Factor: RegionFactor(coarse: coarse, most: MaxDetailRefinement, spacing: detail), High: high, Low: low));
        }
        if ((glyphs is { } box) && (glyphDetail < coarse)) {
            var glyphLow = Vector3.Max(value1: group.Low, value2: box.Low);
            var glyphHigh = Vector3.Min(value1: group.High, value2: box.High);

            if ((glyphLow.X <= glyphHigh.X) && (glyphLow.Y <= glyphHigh.Y) && (glyphLow.Z <= glyphHigh.Z)) {
                regions.Add(item: new SdfLatticeRegion(Bound: true, Factor: RegionFactor(coarse: coarse, most: MaxDetailRefinement, spacing: glyphDetail), High: glyphHigh, Low: glyphLow));
            }
        }
        if (regions.Count == 0) {
            return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory) { PastFloor = (group.Tier > group.FloorTier), TierCell = tierCell };
        }

        return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory) {
            Factor = regions.Max(selector: static region => region.Factor),
            Regions = [.. regions],
            PastFloor = (group.Tier > group.FloorTier),
            TierCell = tierCell,
        };
    }
    // The samples a group's final lattice takes along the longest axis of its box as it stands: FinalSamples at or under
    // its floor tier, and past it the corners of the cells its tier's cell fits there (SdfMeshTier.LatticeSamples).
    private static int FinalLongest(Group group) {
        var extent = (group.High - group.Low);

        return SdfMeshTier.LatticeSamples(floor: group.FloorTier, longestExtent: MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)), most: FinalSamples, tier: group.Tier);
    }
    // The fine cells along each axis a region whose own spacing is `spacing` asks a final lattice of `coarse` spacing to
    // split a coarse cell into: the fewest that reach it, from two to `most`. The lattice splits each region cell the
    // fewest times the finest region's factor divides into by a power of two that still reach its own
    // (SdfSampledLattice.Refine).
    private static int RegionFactor(float coarse, float spacing, int most) =>
        Math.Clamp(value: ((int)MathF.Ceiling(x: (coarse / spacing))), min: 2, max: most);
    // The final lattice of a union of members (a static copy's run, a dynamic slot's several instances, or the parts a pooled
    // group declares), which keeps each member at the resolution its own final lattice would give it, in the group's frame
    // and alike for static and dynamic groups: a cubic lattice past the per-axis cap, at the finest member's spacing where that fits
    // UnionSamples and coarser where it does not, never coarser than the capped lattice over the union's box; a member
    // whose own spacing stays finer than that by more than RegionRatio, or that declares a detail finer than it, is a
    // region its refinement measures at its own factor (SdfSampledLattice.Refine), so one fine member refines no other
    // member's box. A member's own spacing is its bounding sphere's box at FinalSamples a side.
    private static FinalLattice UnionFinal(Group group) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var longest = MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z));
        var finest = float.PositiveInfinity;

        foreach (var (_, radius, _, _, _) in group.Members) {
            finest = MathF.Min(x: finest, y: MemberCell(radius: radius));
        }

        // A tier past the group's floor coarsens the lattice itself: no member's own spacing is finer than its cell.
        var samples = FinalLongest(group: group);
        var widest = (longest / (samples - 1));
        var lattice = BudgetedLattice(finest: ((samples < FinalSamples) ? widest : finest), group: group, minimum: Math.Min(val1: FinalMinimum, val2: samples), widest: widest);
        var spacing = lattice.Cell;
        var coarse = MathF.Max(x: spacing.X, y: MathF.Max(x: spacing.Y, y: spacing.Z));
        var regions = new List<SdfLatticeRegion>();
        var factor = 0;
        var tierCell = SdfMeshTier.Cell(tier: group.Tier);

        foreach (var (_, radius, detail, low, high) in group.Members) {
            // The group's tier floors each member's own spacing: a view that resolves no finer cell is baked none.
            var own = MathF.Max(x: ((detail > 0f) ? MathF.Min(x: MemberCell(radius: radius), y: detail) : MemberCell(radius: radius)), y: tierCell);

            // A member the lattice already resolves refines nothing: a declared detail at or past the coarse spacing (a
            // slab's half thickness over a lattice finer than it) would only spend the refinement's sample budget on
            // surface the coarse contour holds, leaving the thin members that need it the coarse cell.
            if ((detail > 0f) ? (own >= coarse) : ((own * RegionRatio) >= coarse)) { continue; }

            // Each member refines toward its own spacing, so one fine member refines no other member's box.
            var ownFactor = RegionFactor(coarse: coarse, most: ((detail > 0f) ? MaxDetailRefinement : MaxRefinement), spacing: own);

            regions.Add(item: new SdfLatticeRegion(
                High: Vector3.Min(value1: group.High, value2: high),
                Low: Vector3.Max(value1: group.Low, value2: low),
                Factor: ownFactor
            ));
            factor = Math.Max(val1: factor, val2: ownFactor);
        }

        return new FinalLattice(lattice: lattice, allocator: group.Allocator, owner: group.Memory) {
            Factor = factor,
            Regions = [.. regions],
            PastFloor = (group.Tier > group.FloorTier),
            TierCell = tierCell,
        };
    }
    // A final lattice over a group's box with cubic cells past the per-axis cap: `finest` apart where that fits
    // UnionSamples and coarser where it does not, never coarser than `widest`; then no plane along Z holds more than
    // UnionPlaneSamples and no axis more samples than a slab's first plane can name (SdfMeshBakeBlock.MaxSlabPlanes),
    // whatever `widest` says. Every axis keeps `minimum` samples.
    private static SdfMeshBakeRequest BudgetedLattice(Group group, float widest, float finest, int minimum = FinalMinimum) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var longest = MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z));
        var cell = MathF.Max(
            x: MathF.Max(
                x: MathF.Min(x: widest, y: MathF.Max(x: finest, y: MathF.Cbrt(x: (((extent.X * extent.Y) * extent.Z) / UnionSamples)))),
                y: MathF.Sqrt(x: ((extent.X * extent.Y) / UnionPlaneSamples))
            ),
            y: (longest / (SdfMeshBakeBlock.MaxSlabPlanes - 2))
        );

        return Spanning(dims: SdfMeshLattice.Dims(cell: cell, extent: extent, minimum: minimum), extent: extent, gradients: false, group: group);
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
    private void AnswerSlab(Group group, in SdfMeshBakeRequest request) {
        var final = group.Final!;
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
            if (empty) { StoreEmpty(group); }
        }

        FinalLattice? emptied = null;

        using (EnterGate(site: BakeGate.Slab)) {
            group.InFlight = false;
            if (!GroupList.Holds(group: group)) { return; }
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
                SchedulePrepare(final: final, group: group);
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
                points = final.Sampled.Refine(factor: factor, regions: final.Regions, thinFactor: ThinSplit(final: final, factor: factor));
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
            if (refusal is not null) { SdfMeshBakeReport.Refused(refusal: refusal, selector: group.Selector); } else { StoreEmpty(group); }
        } else {
            // The refinement now names its fine samples, so the admission narrows from their bound to their count.
            _ = group.Memory.TryReserve(peak: Math.Max(val1: PeakOf(final: final, fineSamples: ((final.Stage == FinalStage.Refinement) ? points.Length : 0)), val2: group.PeakFloor));
            final.Points = points;
        }

        FinalLattice? emptied = null;

        using (EnterGate(site: BakeGate.Prepare)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group)) { return; }
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
    // as reach FineCell floored by the group's tier, from two to MaxRefinement. A lattice already at that spacing still
    // refines twofold where its samples cannot resolve the surface at all (SdfSampledLattice.Refine's essential cells: a
    // sheet or gap thinner than a cell, two patches in one cell): those lose a feature's whole extent, not a fraction of
    // a cell, so no tier's pixel bound covers them; with no region, only those cells refine.
    private static int FactorOf(FinalLattice final) {
        var cell = final.Lattice.Cell;

        return ((final.Factor > 0)
            ? final.Factor
            : Math.Clamp(value: ((int)MathF.Ceiling(x: (MathF.Min(x: cell.X, y: MathF.Min(x: cell.Y, y: cell.Z)) / MathF.Max(x: FineCell, y: final.TierCell)))), min: 2, max: MaxRefinement));
    }

    // The workspace bytes a final lattice's bake holds at once, per lattice sample and per fine sample: the lattice's
    // values, materials and gradients, its per-cell refinement state and contour, the fine samples' keys, values,
    // materials, slots and points, and the crossing and gradient rounds' points. An upper estimate of the sum of those
    // buffers, so an admitted group's charges stay within its reservation.
    private const long WorkspaceBytesPerSample = 96;

    // Let detected sheets use a finer split when a part's ordinary grid still leaves room above the bake's fine spacing.
    // A finer virtual grid also subdivides transition cells, so keep a lattice already near that spacing on its own grid.
    // A lattice past its group's floor (FinalLattice.PastFloor) splits a sheet's cells no finer than PastFloorThinShare of
    // its tier's cell: the view that demanded the tier resolves no finer, and a sheet split to the bake's fine spacing
    // would cost such a rung the triangles the tier exists to drop.
    private static int ThinSplit(FinalLattice final, int factor) {
        var cell = final.Lattice.Cell;
        var shortest = MathF.Min(x: cell.X, y: MathF.Min(x: cell.Y, y: cell.Z));
        var finest = (final.PastFloor ? MathF.Max(x: FineCell, y: (final.TierCell * PastFloorThinShare)) : FineCell);

        while ((factor <= (MaxDetailRefinement / 2)) && (shortest >= (2 * factor * finest))) { factor *= 2; }

        return factor;
    }
    // The share of its tier's cell a lattice past its group's floor splits a sheet's cells to at the finest.
    private const float PastFloorThinShare = 0.25f;

    // A final lattice's simultaneous peak: its own samples, and the fine samples its refinement measures, at most the
    // bound its spacing and factor allow (SdfSampledLattice.FineSampleBound) until the refinement names them.
    private static long PeakOf(FinalLattice final, int? fineSamples = null) {
        var (x, y, z) = final.Lattice.Dims;
        var factor = FactorOf(final: final);
        var fine = (fineSamples ?? SdfSampledLattice.FineSampleBound(factor: ThinSplit(final: final, factor: factor), samplesX: x, samplesY: y, samplesZ: z));

        return checked(((((long)final.Lattice.Samples) + fine) * WorkspaceBytesPerSample));
    }
    // The next chunk of a group's fine, crossing-root or gradient samples, as many as a frame's share of field evaluations
    // takes. Gradient taps are spaced by the contour's finest cell.
    private static SdfMeshBakeRequest FinalChunk(Group group, FinalLattice final) {
        var gradients = (final.Stage == FinalStage.Gradients);
        var count = Math.Clamp(value: (LatticeTaps / (gradients ? 5 : 1)), min: 1, max: (final.Points.Length - final.Answered));

        return RequestOf(cell: final.Sampled!.ContourCell, dims: (count, 1, 1), gradients: gradients, group: group, origin: Vector3.Zero) with {
            WithMaterial = final.Stage == FinalStage.Refinement,
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

        using (EnterGate(site: BakeGate.Final)) {
            group.InFlight = false;
            if (!GroupList.Holds(group: group)) { return; }
            m_accounts.KindSamples[(2 + ((int)final.Stage))] += request.Samples;
            if (final.Answered < final.Points.Length) {
                group.Next = FinalChunk(final: final, group: group);
                return;
            }

            group.Next = null;
            group.Contouring = true;
            if (final.Stage != FinalStage.Gradients) {
                ScheduleChooseCrossings(final: final, group: group);
                return;
            }

            ScheduleContour(group: group, lattice: sampled);
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

        using (EnterGate(site: BakeGate.Crossings)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group)) { return; }
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
        RefinementObserver?.Invoke(arg1: group.Selector, arg2: lattice.RefinementMap());
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
            if (contour is not null) { StoreEmpty(group); }
        } else if (contour is not null) {
            creases = CreaseVertices(allocator: group.Allocator, contour: contour, workspace: group.Memory);
            if (creases.Length > 0) {
                group.Projection = new ContourProjection(allocator: group.Allocator, contour: contour, observer: group.Memory);
                group.CreaseSpacing = lattice.ContourCell;
                group.CreaseSlope = lattice.SurfaceSlope();
                group.Points.Resize(creases.Length, clear: false, preserve: false);
                for (var index = 0; (index < creases.Length); index++) {
                    var vertex = creases[index];

                    group.Points[index] = new Vector4(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)], w: 1f);
                }
            }
        }

        using (EnterGate(site: BakeGate.Contour)) {
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
                ScheduleSplit(group: group);
                return;
            }
            group.Creases = creases;
            group.PointsAnswered = 0;
            group.Next = PointsChunk(gradients: true, group: group);
        }
    }
    // The vertices a contour's field check measures (AnswerCreases): every vertex no crossing pins to the surface.
    private static int[] CreaseVertices(SdfContour contour, IAllocationObserver? workspace, IAllocator? allocator) {
        using var found = new OwnedBuffer<int>(allocator: allocator, observer: workspace);

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            if (((vertex < contour.Features.Length) && contour.Features[vertex]) || ((vertex < contour.Solved.Length) && contour.Solved[vertex])) { found.Add(item: vertex); }
        }

        return found.ToArray();
    }
    // The next chunk of the vertices a group's field check measures (gradients at taps the crease spacing apart) or of its
    // material points (values alone), as many as a frame's share of field evaluations takes.
    private static SdfMeshBakeRequest PointsChunk(Group group, bool gradients) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: (LatticeTaps / (gradients ? 5 : 1)));

        return RequestOf(cell: (gradients ? group.CreaseSpacing : Vector3.One), dims: (count, 1, 1), gradients: gradients, group: group, origin: Vector3.Zero) with {
            MaterialOnly = !gradients,
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
            group.Projection?.Dispose();
            group.Projection = null;
            group.Points.Resize(0);
        }
        using (EnterGate(site: BakeGate.Creases)) {
            group.InFlight = false;
            if (!GroupList.Holds(group: group)) { return; }
            m_accounts.KindSamples[5] += request.Samples;
            m_accounts.Softened += softened;
            if (more) {
                group.Next = PointsChunk(gradients: true, group: group);
                return;
            }
            group.Next = null;
            group.Contouring = true;
            ScheduleSplit(group: group);
        }
    }
    // A stage's scheduling is a method of its own: a lambda that captures a parameter makes its method allocate the
    // closure on entry, before any early return, and the answer methods return early on every chunk but a round's last.
    private void ScheduleLoad(Group group) => Schedule(group: group, work: () => Load(group: group));
    private void SchedulePrepare(Group group, FinalLattice final) => Schedule(group: group, work: () => Prepare(final: final, group: group));
    private void ScheduleChooseCrossings(Group group, FinalLattice final) => Schedule(group: group, work: () => ChooseCrossings(final: final, group: group));
    private void ScheduleContour(Group group, SdfSampledLattice lattice) => Schedule(group: group, work: () => Contour(group: group, lattice: lattice));
    private void ScheduleSplit(Group group) => Schedule(group: group, work: () => Split(group: group));
    private void ScheduleAdvance(Group group) => Schedule(group: group, work: () => Advance(group: group));
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

        var contour = group.Contour!.Finished(allocator: group.Allocator, cell: group.DiagnosticCell, workspace: group.Memory);
        var cut = new SdfMeshMaterialCut(contour: contour);
        var points = cut.Points(into: group.Points);

        Account(group: group, since: started, stage: BakeStage.Split);
        group.Contour = contour;
        group.Cut = cut;
        StagePoints(group: group, count: points);
        using (EnterGate(site: BakeGate.Split)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group)) { return; }
            group.Next = PointsChunk(gradients: false, group: group);
        }
    }
    // Fills a group's point round and the storage its answers land in, on the worker running the group's stage: no
    // request names the group's points until the gate publishes the round's first chunk.
    private static void StagePoints(Group group, int count) {
        group.PointMaterials.Resize(count, clear: false, preserve: false);
        group.PointsAnswered = 0;
    }
    // Answers a chunk of a group's material points; once a round's every point is answered, its material cut takes them
    // on the thread pool (Advance).
    private void AnswerPoints(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<int> materials, int kind) {
        materials[..request.Samples].CopyTo(destination: group.PointMaterials.GetSpan()[group.PointsAnswered..]);
        group.PointsAnswered += request.Samples;
        using (EnterGate(site: BakeGate.Points)) {
            group.InFlight = false;
            if (!GroupList.Holds(group: group)) { return; }
            m_accounts.KindSamples[kind] += request.Samples;
            if (group.PointsAnswered < group.Points!.Length) {
                group.Next = PointsChunk(gradients: false, group: group);
                return;
            }

            group.Next = null;
            group.Contouring = true;
            ScheduleAdvance(group: group);
        }
    }
    // Hands a round's materials to the group's cut, then queues its next round, or meshes the group once none is left.
    private void Advance(Group group) {
        var cut = group.Cut!;
        var started = Stopwatch.GetTimestamp();

        cut.Answer(materials: group.PointMaterials.GetSpan());

        var points = cut.Points(into: group.Points);

        Account(group: group, since: started, stage: BakeStage.Cut);

        if (points == 0) {
            Build(group: group);
            return;
        }

        StagePoints(group: group, count: points);
        using (EnterGate(site: BakeGate.Advance)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group)) { return; }
            group.Next = PointsChunk(gradients: false, group: group);
        }
    }
    // Builds a group's mesh from its contour, cut along its material boundaries.
    private void Build(Group group) {
        SdfMesh? mesh = null;
        var started = Stopwatch.GetTimestamp();

        try {
            mesh = group.Cut!.Build(group.Memory, group.Allocator);
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            SdfMeshBakeReport.Refused(refusal: exception.Message, selector: group.Selector);
        }
        Account(group: group, since: started, stage: BakeStage.Cut);

        var edges = group.Cut!.Edges;

        group.Contour = null;
        group.Creases = null;
        group.Cut = null;
        // The CPU influence reading propagates owners through composition once per exact position. Copies at a
        // crease or material cut carry the same motion, independently of their normals. It reads the group's own
        // instructions (SdfMeshSkinSource), so the skin stays a function of the geometry the mesh is stored under.
        if ((mesh is not null) && (group.Skin is { } skin)) {
            mesh = SdfMeshSkin.Build(mesh: mesh, refusal: out var refusal, skin: skin);
            if (mesh is null) {
                SdfMeshBakeReport.SkinSplit(group.Selector, group.Sealed, refusal);
                mesh = SdfMeshSkin.Split(skin: skin);
            }
        }
        using (EnterGate(site: BakeGate.Land)) { m_accounts.CutEdges += edges; }
        if (mesh is not null) {
            mesh = mesh with { BakeCell = group.DiagnosticCell };
            KeepCheckpoint(group, mesh, simplified: false);
        }
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

            mesh = SimplificationStage.Run(mesh, group.Dynamic, group.Glass, group.Instances, group.Geometry.ContainsGlyph, group.Memory, group.Allocator);
            merged = (contoured - mesh.TriangleCount);
            Account(detail: new(Count: contoured, Kept: mesh.TriangleCount), group: group, since: started, stage: BakeStage.Land);
            KeepCheckpoint(group, mesh, simplified: true);
        }
        PublishMesh(group, mesh, merged);
    }

    // Publishes a group's mesh: copies it into the library's storage under the process account and lands it. The group's
    // final lattice went with its contour, so before the copy its admission gives up the room it no longer holds, which
    // the copy's own owners then charge. A copy the account still has no room for waits, parked with the bytes refused,
    // until the group can reserve them again (ResumeParked), and is published then: the group never fails for it.
    private void PublishMesh(Group group, SdfMesh? mesh, int merged) {
        if (mesh is not null) {
            var started = Stopwatch.GetTimestamp();
            var baked = mesh;

            _ = group.Memory.TryReserve(peak: group.Memory.Bytes);
            try {
                mesh = StageCodeCurrent(group)
                    ? Library.Store(form: (group.Dynamic ? group.Form : null), geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: baked)
                    : Library.CopyUnstored(baked);
            } catch (HostMemoryRefusedException refused) {
                Park(bytes: Math.Max(val1: refused.Bytes, val2: baked.StorageBytes), group: group, refused: refused, work: () => PublishMesh(group: group, mesh: baked, merged: merged));
                return;
            }
            Account(since: started, stage: BakeStage.Store);
        } else if ((group.Outcome is not null) && StageCodeCurrent(group)) {
            Library.StoreEmpty(geometry: group.Sealed, identity: group.Identity, persist: true);
        }

        using (EnterGate(site: BakeGate.Land)) {
            m_accounts.Merged += merged;
            group.Contouring = false;
            group.Done = true;
            Draw(group: group, mesh: mesh);
            group.Outcome = ((mesh is null) ? (group.Outcome ?? "mesh refused") : null);

            group.Points.Resize(0);
            group.PointMaterials.Resize(0);
            Finish();
        }
    }
    // Takes a group's mesh from the library's store; a group whose entry the store refuses or no longer holds bakes from
    // its coarse lattice.
    private void Load(Group group) {
        var loaded = Library.TryLoad(declined: out var declined, form: (group.Dynamic ? group.Form : null), geometry: (group.LoadsForm ? SdfMeshLibrary.FormGeometry(form: group.Form) : group.Sealed), identity: group.Identity, materials: group.MaterialOrder, mesh: out var mesh, refusal: out var refusal);

        // A bake that draws stored rungs alone never bakes what the store holds because the account had no room to
        // read it: a tier's replacement gives its place back, so the group keeps drawing the tier it has room for, and
        // a group with nothing of its own to draw reads its entry again later.
        if (!loaded && declined && (Library.Policy == SdfMeshCachePolicy.Stored)) {
            Library.Cache.Count(kind: SdfMeshCache.LoadsDeclined);
            if (!Decline(group: group)) { Reload(group: group); }
            return;
        }
        // A dynamic group whose own entry the store turned out not to hold loads the mesh stored of its form.
        if (!loaded && !declined && !group.LoadsForm && HoldsForm(group: group)) {
            group.LoadsForm = true;
            ScheduleLoad(group: group);
            return;
        }
        if (!loaded && ResumeCheckpoint(group)) {
            using (EnterGate(site: BakeGate.Load)) { m_loadsDone++; m_loadLast = Stopwatch.GetTimestamp(); }
            return;
        }

        using (EnterGate(site: BakeGate.Load)) {
            group.Contouring = false;
            m_loadsDone++;
            m_loadLast = Stopwatch.GetTimestamp();
            if (refusal is not null) {
                SdfMeshBakeReport.StoreRefused(refusal: refusal, selector: group.Selector);
            }
            if (!loaded) {
                group.LoadsForm = false;
                group.Next = group.Deferred;
                Library.Cache.Count(kind: (group.Live ? SdfMeshCache.LiveBaked : SdfMeshCache.GroupsBaked));
                return;
            }
            m_accounts.Loaded++;
            group.Done = true;
            group.Stored = true;
            if (mesh is null) { group.FoundNoSurface(how: ", as stored before"); }
            Draw(group: group, mesh: mesh);
            Finish();
        }
    }
    // Makes a group's new mesh its product. A group rebaking after an edit keeps drawing its previous mesh until this
    // one lands, and then until its words stand in the mesh region (Group.Mesh): the product revision changes when the
    // drawn mesh does, so the dresser lists the new one.
    private void Draw(Group group, SdfMesh? mesh) {
        // A rung a ladder bake fills is its store entry alone: the claim handed in is let go, and the group keeps
        // drawing the mesh of the group it stands in for until that group takes its place back (FinishReplacements).
        if (group.Fill) {
            mesh?.Release();
            return;
        }
        group.Mesh = mesh;
        group.ResidentAt = 0;
        m_allResident = false;
        _ = m_retiredGroups.RemoveAll(match: static retired => retired.Released);
    }
    // Records the bake's wall time once its last group finishes, and reports it.
    private void Finish() {
        Follow();
        if (DoneThrough() < m_groups.Count) { return; }
        if (m_reported) { return; }
        m_seconds = m_clock.Elapsed.TotalSeconds;
        m_reported = true;
        // The large pool keeps its arrays past the bake: a world in play restarts a bake at every program change, in each
        // residency, and the next one rents the same scratch. The process account trims the pool under pressure
        // (MemoryAccount.TrimCaches), which is what bounds the memory it holds.
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
    // vertex moves; comparing only against each step's new normal would permit a cumulative reversal. A triangle is held
    // the same way to the lattice edge its quad crosses (SdfContour.QuadAxes, SdfContourFacing), which the contour's
    // settle left it facing out along: its own facing can turn edge-on to that direction and past it without reversing
    // against where it began.
    // It lives across the group's field-check rounds, each a GPU round trip, so its tables are the group's owned storage,
    // charged to the group's account, never collected garbage: the field check disposes it once its last vertex moves, a
    // retired group's release once its last job ends.
    private sealed class ContourProjection : IDisposable {
        private readonly OwnedBuffer<int> m_starts;
        private readonly OwnedBuffer<int> m_incident;
        private readonly OwnedBuffer<Triangle> m_triangles;
        private int m_triangleCount;

        public void Dispose() {
            m_starts.Dispose();
            m_incident.Dispose();
            m_triangles.Dispose();
            m_triangleCount = 0;
        }

        // `Axis` is zero where the contour records no lattice edge; `Patches` is the sum of the corners' patch facings
        // (SdfContour.Patches), zero where it records none.
        private readonly record struct Triangle(int A, int B, int C, (double X, double Y, double Z) Reference, sbyte Axis, (double X, double Y, double Z) Patches);

        public ContourProjection(SdfContour contour, IAllocator? allocator = null, IAllocationObserver? observer = null) {
            m_triangles = new(allocator: allocator, observer: observer);
            m_starts = new(allocator: allocator, observer: observer);
            m_incident = new(allocator: allocator, observer: observer);
            m_triangles.Resize((2 * contour.QuadCount), clear: false, preserve: false);
            m_starts.Resize((contour.VertexCount + 1), clear: true, preserve: false);

            var triangles = m_triangles;

            double Patch(int vertex, int axis) => ((((3 * vertex) + axis) < contour.Patches.Length) ? contour.Patches[((3 * vertex) + axis)] : 0.0);
            void Add(int a, int b, int c, sbyte axis) {
                if ((a == b) || (a == c) || (b == c)) { return; }
                var pa = contour.PositionOf(vertex: a);
                var pb = contour.PositionOf(vertex: b);
                var pc = contour.PositionOf(vertex: c);
                var normal = Area(a: pa, b: pb, c: pc);
                var longest = Math.Max(val1: Vector3.DistanceSquared(value1: pa, value2: pb), val2: Math.Max(val1: Vector3.DistanceSquared(value1: pa, value2: pc), val2: Vector3.DistanceSquared(value1: pb, value2: pc)));

                // A collinear triangle has no orientation to retain. Ignoring it lets a neighbouring move open it
                // into a valid triangle instead of making one degenerate face immobilize the whole fan.
                if (!(Dot(a: normal, b: normal) > ((1e-12 * longest) * longest))) { return; }
                triangles[m_triangleCount++] = (new Triangle(A: a, Axis: axis, B: b, C: c, Patches: (((Patch(axis: 0, vertex: a) + Patch(axis: 0, vertex: b)) + Patch(axis: 0, vertex: c)), ((Patch(axis: 1, vertex: a) + Patch(axis: 1, vertex: b)) + Patch(axis: 1, vertex: c)), ((Patch(axis: 2, vertex: a) + Patch(axis: 2, vertex: b)) + Patch(axis: 2, vertex: c))), Reference: normal));
                m_starts[(a + 1)]++;
                m_starts[(b + 1)]++;
                m_starts[(c + 1)]++;
            }

            for (var quad = 0; (quad < contour.QuadCount); quad++) {
                var at = (4 * quad);

                var (a, b, c, d) = (contour.Quads[at], contour.Quads[(at + 1)], contour.Quads[(at + 2)], contour.Quads[(at + 3)]);
                var axis = ((quad < contour.QuadAxes.Length) ? contour.QuadAxes[quad] : ((sbyte)0));

                Add(a: a, axis: axis, b: b, c: c);
                Add(a: a, axis: axis, b: c, c: d);
            }
            for (var vertex = 0; (vertex < contour.VertexCount); vertex++) { m_starts[(vertex + 1)] += m_starts[vertex]; }
            m_incident.Resize(m_starts[contour.VertexCount], clear: false, preserve: false);
            using var filledLease = RentedArrays.Rent<int>(array: out var filled, minimumLength: contour.VertexCount, owner: observer);

            filled.AsSpan(start: 0, length: contour.VertexCount).Clear();
            for (var index = 0; (index < m_triangleCount); index++) {
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
            var original = contour.PositionOf(vertex: vertex);
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
                if (triangle.Axis == 0) { continue; }

                var alongBefore = Along(contour: contour, point: original, triangle: triangle, vertex: vertex);
                var alongAfter = Along(contour: contour, point: target, triangle: triangle, vertex: vertex);
                var alongFloor = Math.Min(val1: (Math.Sqrt(d: Dot(a: triangle.Reference, b: triangle.Reference)) * 1e-5), val2: (alongBefore * 0.5));

                if ((alongAfter < alongFloor) && (alongAfter < alongBefore)) {
                    limit = Math.Min(val1: limit, val2: (0.99 * Math.Max(val1: 0.0, val2: ((alongBefore - alongFloor) / (alongBefore - alongAfter)))));
                }
            }

            // Positions are ultimately floats. Check the actual rounded point too, backing off when a very narrow
            // triangle loses the analytic margin to coordinate rounding.
            for (var attempt = 0; ((attempt < 8) && (limit > 0.0)); attempt++, limit *= 0.5) {
                var point = ((limit >= 1.0) ? target : Vector3.Lerp(amount: ((float)limit), value1: original, value2: target));
                var valid = (point != original);

                for (var at = m_starts[vertex]; (valid && (at < m_starts[(vertex + 1)])); at++) {
                    var triangle = m_triangles[m_incident[at]];

                    valid = ((Facing(contour: contour, point: point, triangle: triangle, vertex: vertex) > 0.0) &&
                        ((triangle.Axis == 0) || FacesOut(contour: contour, point: point, triangle: triangle, vertex: vertex) || !FacesOut(contour: contour, point: original, triangle: triangle, vertex: vertex)));
                }
                if (valid) {
                    amount = ((float)limit);
                    return point;
                }
            }

            return original;
        }

        private static double Facing(SdfContour contour, Triangle triangle, int vertex, Vector3 point) => Dot(a: triangle.Reference, b: Area(
            a: ((triangle.A == vertex) ? point : contour.PositionOf(vertex: triangle.A)),
            b: ((triangle.B == vertex) ? point : contour.PositionOf(vertex: triangle.B)),
            c: ((triangle.C == vertex) ? point : contour.PositionOf(vertex: triangle.C))));
        // The triangle's area normal along its quad's lattice edge with `vertex` at `point` (SdfContourFacing.Along).
        private static double Along(SdfContour contour, Triangle triangle, int vertex, Vector3 point) {
            var (a, b, c) = Corners(contour: contour, point: point, triangle: triangle, vertex: vertex);

            return SdfContourFacing.Along(a: a, axis: triangle.Axis, b: b, c: c);
        }
        // Whether the triangle faces out as the lattice fixed its quad's facing with `vertex` at `point`
        // (SdfContourFacing.FacesOut): the analytic clip keeps it facing out along its edge, and this also refuses a move
        // that leaves it edge-on to that edge and turned against its corners' patches.
        private static bool FacesOut(SdfContour contour, Triangle triangle, int vertex, Vector3 point) {
            var (a, b, c) = Corners(contour: contour, point: point, triangle: triangle, vertex: vertex);

            return SdfContourFacing.FacesOut(a: a, axis: triangle.Axis, b: b, c: c, patches: triangle.Patches);
        }
        // The triangle's corners with `vertex` at `point`.
        private static ((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C) Corners(SdfContour contour, Triangle triangle, int vertex, Vector3 point) {
            var a = ((triangle.A == vertex) ? point : contour.PositionOf(vertex: triangle.A));
            var b = ((triangle.B == vertex) ? point : contour.PositionOf(vertex: triangle.B));
            var c = ((triangle.C == vertex) ? point : contour.PositionOf(vertex: triangle.C));

            return ((a.X, a.Y, a.Z), (b.X, b.Y, b.Z), (c.X, c.Y, c.Z));
        }
        private static (double X, double Y, double Z) Area(Vector3 a, Vector3 b, Vector3 c) {
            var (ux, uy, uz) = ((((double)b.X) - a.X), (((double)b.Y) - a.Y), (((double)b.Z) - a.Z));
            var (vx, vy, vz) = ((((double)c.X) - a.X), (((double)c.Y) - a.Y), (((double)c.Z) - a.Z));

            return (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        }
        private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
    }
    // One bake group as the plan formed it (SdfMeshBakeGroup), and where its bake stands.
    private sealed class Group(int selector, bool dynamic) : SdfMeshBakeGroup(dynamic: dynamic, selector: selector) {
        public bool Contouring { get => m_contouring; set { m_contouring = value; Settle(); } }
        // Whether the group stands at an index of its bake's group list (GroupList, its one writer).
        public bool Listed {
            get => m_listed;
            set {
                m_listed = value;
                Settle();
                if (value) { AwaitResidency(); }
            }
        }
        // The index the group stands at in its bake's group list (GroupList.Set), which breaks ties among equal ranks.
        public int Index { get; set; }

        private bool m_contouring;
        private bool m_done;
        private bool m_inFlight;
        private bool m_listed;
        private long m_residentAt;
        private int m_awaitsResidency;

        // What the group adds to its bake's counts (GroupTally), as recorded under the tally's lock, and where it stands
        // in the set of groups holding a lattice it is in.
        public int Tallied { get; set; }
        public int OfferedAt { get; set; }
        // What the group last added to the counts of the frame's holes, and the epoch it was counted in (CountHoles).
        public int HoleBits { get; set; }
        public int HoleEpoch { get; set; }

        private int m_awaitsJudging;

        // Takes the group's place in the queue of groups the trace judge may owe a verdict on (Judgeable): true once until
        // the scan reads it.
        public bool TryQueueJudging() => (Interlocked.CompareExchange(location1: ref m_awaitsJudging, value: 1, comparand: 0) == 0);
        public void ClearJudging() => Volatile.Write(location: ref m_awaitsJudging, value: 0);

        public int TallyOf() {
            var next = Next;

            return GroupTally.Of(admitted: Admitted, awaiting: AwaitsStage, contouring: m_contouring, done: m_done, firstSlab: (next is { Slab: 0 }), hasNext: (next is not null), inFlight: m_inFlight, listed: m_listed);
        }
        // Moves the group's contribution to its bake's counts to what its state now is; a group no bake owns yet adds none.
        private void Settle() => Owner?.m_tally.Settle(group: this);
        // Hands a landed group whose mesh has not stood in the mesh region to its bake's observer (ObserveResidency): the
        // one door by which a group joins the set it walks, from every change that can make a listed group owe it (done,
        // listed, or drawn again). The observer drops it once it stands.
        private void AwaitResidency() {
            if (m_done && (m_residentAt == 0) && (Owner is { } owner) && (Interlocked.CompareExchange(location1: ref m_awaitsResidency, value: 1, comparand: 0) == 0)) { owner.m_landed.Enqueue(item: this); }
        }
        // Lets go of the group once the observer has dropped it, so a later change can hand it over again.
        public void StoodOrLeft() => Volatile.Write(location: ref m_awaitsResidency, value: 0);

        public IAllocator? Allocator { get; set; }
        public bool Done {
            get => m_done;
            set {
                m_done = value;
                Settle();
                if (value) {
                    // A done group may now be the trace of its own floor (Trace), which the dresser reads, and owe the
                    // judge a verdict on it.
                    Owner?.Touch(group: this);
                    Owner?.Judgeable(group: this);
                    // One that meshed nothing past its floor owes a finer rung, which the next tier decision reads.
                    if ((m_mesh is null) && (Tier > FloorTier)) { Owner?.Attend(group: this); }
                    AwaitResidency();
                    Memory.ReleaseReservation();
                    EndShare();
                    // Its followers are finished at the next Follow, which visits only the leaders done since it last ran.
                    if ((m_followers is { Count: > 0 }) && (Owner is { } owner)) { owner.m_followable.Add(item: this); }
                }
                TryRelease();
            }
        }
        public bool InFlight { get => m_inFlight; set { m_inFlight = value; TryRelease(); Settle(); } }

        public MemoryAccount.Owner Memory { get; } = MemoryAccount.Shared.CreateOwner(allowance: MemoryAllowance.Authoring, memoryClass: MemoryClass.Workspace, name: "mesh-bake-group");

        private SdfMesh? m_mesh;

        // The bake whose group list holds the group, set by Restart and Replace, the list's only writers. Every change of
        // the mesh a listed group draws moves that bake's product revision here, the one door, so the dresser can trust
        // the revision: a group no list holds yet draws nothing.
        public SdfMeshPathBake? Owner { get; set; }

        // The mesh whose region bytes PlacedBytes last read, and those bytes: Retier and FinerBytes read a drawn group's
        // bytes on every decision and every recount, and a mesh's bytes never change, so each mesh is read once.
        private SdfMesh? m_bytesOf;
        private long m_placedBytes;

        // The bytes the group's product takes of the mesh region (SdfMeshRegion.PlacedBytesOf), zero with none. On the
        // frame's thread, under the gate.
        public long PlacedBytes {
            get {
                if (m_mesh is not { } mesh) {
                    m_bytesOf = null;
                    return 0L;
                }
                if (!ReferenceEquals(objA: mesh, objB: m_bytesOf)) {
                    m_placedBytes = SdfMeshRegion.PlacedBytesOf(mesh: mesh);
                    m_bytesOf = mesh;
                }
                return m_placedBytes;
            }
        }
        // The group's product, and the mesh the frame draws for it (Shown). Invariant: a draw names a mesh that
        // replaces a drawn one only once its words stand in the mesh region. This setter is the one door a product
        // arrives by: where a residency's tables land the bake's meshes (LandsAhead), a product that replaces a drawn
        // mesh waits ahead of the draws, the group still drawing what it drew, and ObserveResidency hands it the draws
        // (ShowOwn) once the tables say it stands. A group's first mesh, and any product of a bake no tables observe,
        // is drawn at once. The setter takes the claim handed in and lets go of the product's it replaces.
        public SdfMesh? Mesh {
            get => m_mesh;
            set {
                var old = m_mesh;
                var next = (Retired ? null : value);
                var owner = Owner;

                if (Retired) { value?.Release(); }
                if (ReferenceEquals(objA: old, objB: next)) {
                    // The same product again: the claim handed in is one too many.
                    if (!Retired) { old?.Release(); }
                    return;
                }
                if ((next is not null) && ((m_shown ?? old) is not null) && (owner is { LandsAhead: true })) {
                    // A product that never took the draws is let go; the mesh the frame draws stays.
                    if (m_shown is null) {
                        m_shown = old;
                        AheadFrames = 0;
                        owner.AwaitAhead(group: this);
                    } else {
                        old?.Release();
                    }
                    m_mesh = next;
                    SdfMeshSkin.Count(counters: owner.Library.Skins, mesh: next);
                    owner.Judgeable(group: this);
                    return;
                }
                m_mesh = next;
                if (m_shown is { } shown) {
                    m_shown = null;
                    shown.Release();
                }
                if (!ReferenceEquals(objA: next, objB: m_standIn)) { m_standIn = null; }
                if (owner is not null) {
                    owner.Touch(group: this);
                    owner.Judgeable(group: this);
                    if (next is not null) { SdfMeshSkin.Count(counters: owner.Library.Skins, mesh: next); }
                }
                old?.Release();
            }
        }
        // The mesh the frame draws for the group: the one its product replaces while that lands ahead, else its product.
        public SdfMesh? Shown => (m_shown ?? m_mesh);
        // Whether the group's product waits for its words to stand in the mesh region before it takes the draws.
        public bool Ahead => (m_shown is not null);
        // The frames the product has waited ahead of the draws (SdfMeshPathBake.ServeAhead).
        public int AheadFrames { get; set; }

        // Hands the draws to the product, letting go of the mesh it replaces. Under the owner's gate.
        public void ShowOwn() {
            if (m_shown is not { } shown) { return; }
            m_shown = null;
            shown.Release();
            if (ReferenceEquals(objA: shown, objB: m_standIn)) { m_standIn = null; }
            Owner?.Touch(group: this);
        }
        // Marks the mesh the group draws as the stand-in it took from the group it replaced (ShowReplaced), which shows
        // the geometry before the change rather than its own.
        public void MarkStandIn() => m_standIn = Shown;
        // Whether the group draws a mesh of its own geometry at some tier, or has finished with none: not the stand-in
        // it took from the group it replaced, and not nothing while it bakes.
        public bool DrawsOwn => ((Shown is { } shown) ? !ReferenceEquals(objA: shown, objB: m_standIn) : Done);

        private SdfMesh? m_shown;
        // The replaced group's mesh the group draws until a product of its own takes the draws; held only for that
        // comparison and cleared once the stand-in no longer draws, so it never keeps a released mesh reachable.
        private SdfMesh? m_standIn;

        public SdfMeshBakeRequest? Next { get => m_next; set { m_next = value; Settle(); } }

        private SdfMeshBakeRequest? m_next;
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
        // Records that the group found no surface, `how` saying where the answer came from. A group whose bound the plan
        // clamped to the frame's far distance (a world that authors none bakes 40 m about each instance's centre) says so,
        // since its empty answer is about the clamp, not the geometry.
        public void FoundNoSurface(string how) =>
            Outcome = (Clamped ? $"no surface{how} (bound clamped to the far distance)" : $"no surface{how}");
        // The identity of the bake the group started under, and the coarse lattice it bakes from when its load fails.
        public UInt128 Identity { get; set; }
        // The keys the group's checkpoints and sample sets are kept under (KeyCheckpoints), replaced whole and read by
        // workers off the gate; null for a group that keeps none.
        private CheckpointKeys? m_checkpoints;

        public CheckpointKeys? Checkpoints { get => Volatile.Read(location: ref m_checkpoints); set => Volatile.Write(location: ref m_checkpoints, value: value); }
        public bool CheckedCheckpoints { get; set; }
        // Whether the group's next lattice missed its probe, so the next take dispatches it unprobed (ProbeSamples).
        public bool ProbeMissed { get; set; }
        public SdfMeshBakeRequest? Deferred { get; set; }
        // Whether the mesh came from the library or its store rather than a bake of this group.
        public bool Stored { get; set; }
        // Whether the group has reserved its final lattice and result peak.
        public bool Admitted { get => m_admitted; set { m_admitted = value; Settle(); } }

        private bool m_admitted;
        // When the group was admitted (a Stopwatch timestamp), until its first stage is queued: its lead, which the
        // pool's backlog measures, and the mark of a group still on its way to the workers. Zero otherwise.
        private long m_admittedAt;

        public bool AwaitsStage => (Volatile.Read(location: ref m_admittedAt) != 0L);

        public void MarkAdmitted(long timestamp) {
            Volatile.Write(location: ref m_admittedAt, value: timestamp);
            Settle();
        }
        public long TakeAdmittedAt() {
            var at = Interlocked.Exchange(location1: ref m_admittedAt, value: 0L);

            Settle();

            return at;
        }
        public bool DeferredReported { get; set; }
        // The least peak the group's admission reserves, raised each time the account refused one of its stages
        // (Readmit) to what it held then and the bytes refused; zero for a group never refused.
        public long PeakFloor { get; set; }
        // Whether a stage of the group threw (Fail), so no later program keeps it.
        public bool Failed { get; set; }
        // Whether the bake no longer plans the group (a later program replaced it, or its residency was released): the
        // pool drops its queued work. Written under the bake's gate, read under the pool's.
        public bool Retired {
            get => Volatile.Read(location: ref m_retired);
            set { Volatile.Write(location: ref m_retired, value: value); if (value) { Memory.Classify(category: MemoryCategory.Retirement); EndShare(); } TryRelease(); }
        }

        private bool m_retired;

        // The share of the group's geometry the group leads (SdfMeshLibrary.Share), until it is done or retired; the
        // share it waits on instead, with its place among the waiters, until it takes the leader's mesh, starts again or
        // retires (Joined, Waiter: written under the bake's gate). While it waits it holds no lattice (Next is null).
        private SdfMeshBakeShare? m_share;

        public SdfMeshBakeShare? Share { get => Volatile.Read(location: ref m_share); set => Volatile.Write(location: ref m_share, value: value); }
        public SdfMeshBakeShare? Joined { get; set; }
        public SdfMeshBakeShare.Waiter? Waiter { get; set; }
        // The samples the group's lattices dispatched, which a waiter on its share saves.
        public long TakenSamples { get; set; }
        // While the group waits: the leader's bake's asks when they last moved, and this bake's asks and the time then
        // (DesertStalled).
        public long LeaderAsks { get; set; }
        public long StillAsks { get; set; }
        public long StillSince { get; set; }

        // The one door a share leaves its leader by: a group done (landed, empty, failed or taken whole) or retired resolves
        // the share it leads, handing each waiter back to its own bake, and a retired waiter leaves the share it waits on.
        private void EndShare() {
            if (Interlocked.Exchange(location1: ref m_share, value: null) is { } led) {
                foreach (var waiter in led.Bake.Library.Resolve(samples: TakenSamples, share: led)) {
                    _ = waiter.Bake.ScheduleJoined(group: ((Group)waiter.Group), share: led);
                }
            }
            if (Retired && (Joined is { } joined) && (Waiter is { } place)) { joined.Bake.Library.Leave(desert: false, share: joined, waiter: place); }
        }

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
                    Contour = null; Cut = null; Creases = null; Projection?.Dispose(); Projection = null;
                    Next = null; Deferred = null;
                    Skin?.Release();
                    Memory.Dispose();
                }
                if (Retired && !m_released) {
                    m_released = true;
                    m_mesh?.Release(); m_mesh = null;
                    m_shown?.Release(); m_shown = null;
                    m_floor?.Release(); m_floor = null;
                    Replaces?.RetireReplaced();
                }
            }
        }
        private void RetireReplaced() { Retired = true; }

        // The group of the same bake baking this group's geometry, whose mesh this one takes when it finishes, or null.
        // Setting a leader lists the group among the leader's followers, the index Follow reads; a follower that leaves
        // its leader (null, or another) stays listed and is passed over there.
        public Group? Leader {
            get => m_leader;
            set {
                m_leader = value;
                if (value is not null) { (value.m_followers ??= []).Add(item: this); }
            }
        }

        private Group? m_leader;
        private List<Group>? m_followers;

        // The groups that named this group their leader and have not been handed over (TakeFollowers), some of which have
        // since left it.
        public IReadOnlyList<Group>? Followers => m_followers;

        // Hands over the groups that named this group their leader since the last hand-over.
        public List<Group> TakeFollowers() {
            var followers = (m_followers ?? []);

            m_followers = null;
            return followers;
        }
        // The plan of this group at its newest frame inputs, while this group still bakes older ones (Revisit): never
        // begun, so it holds nothing, and started in this group's place once this group lands (Promote).
        public Group? Successor { get; set; }
        // The group a tier's replacement (Retier) replaces until it lands and takes the draws.
        public Group? Replaces { get; set; }
        // The box a demand point measures the group by: its planned box, taken before the bake narrows it, as a center and
        // half extents in world space or, for a dynamic group, its slot's frame and units.
        public Vector3 DemandCenter { get; private set; }
        public Vector3 DemandHalf { get; private set; }
        public bool HasDemandBox { get; private set; }
        // When the frame thread first saw the group done, and its mesh's words in the mesh region (Stopwatch timestamps;
        // zero not yet).
        public long LandedAt { get; set; }
        public long ResidentAt {
            get => m_residentAt;
            set {
                m_residentAt = value;
                if (value == 0) { AwaitResidency(); }
            }
        }
        // The display pixels its demand box projects at the demand point that sees it largest, as of the latest frame
        // that measured it (SdfMeshDemand.Priority); zero with no demand point.
        public float Priority { get; set; }
        // Whether a view may see the group as of the latest frame that measured it (SdfMeshDemand.Sees), which every group
        // is before its first measure: the bake takes those in view first (RankOf), and only one arriving in view plans
        // its floor first (Tiers).
        public bool InView { get; set; } = true;
        // The tier the demand asked of the group at the latest frame (Retier), the frames in a row it has owed a finer
        // tier than it draws, and whether a finer tier's bake meshed nothing at the demanded tier, so none is tried until
        // the demand asks another.
        public int DemandedTier { get; set; }

        // The metres one display pixel covers at its demand box, as the bake last measured it (MeasureDemands); not a
        // number before the first measure.
        public float DemandPixel {
            get {
                Owner?.ThrowIfMeasuring();
                return m_demandPixel;
            }
            set {
                Owner?.ThrowIfMeasuring();
                m_demandPixel = value;
            }
        }

        private float m_demandPixel = float.NaN;
        // What the demand watch's latest measure stood on and when each of its clocks next makes it due (DemandWatch).
        public DemandWatch Watch;
        // Its record among the frustum watches (FrustumWatches), while a frustum bounded to its view could move its tier;
        // -1 for none.
        public int FrustumWatch = -1;
        // The marked lists the group stands in (MarkedGroups): each list's own bit, set while the group is in it; the demand
        // watch's lists apart (GroupMarks.Demand), since its measure writes them off the frame's thread.
        public GroupMarks Marks;
        public GroupMarks DemandMarks;

        public int OwedFrames { get; set; }
        // The frames in a row the demand has asked a coarser tier than it draws, and, for a tier's replacement, the
        // bytes of mesh region it is expected to add (a finer tier) or give back (a coarser one, negative).
        public int SpareFrames { get; set; }
        public long RetierDeltaBytes { get; set; }
        public bool RetierRefused { get; set; }
        // Whether the group arrived with nothing to draw and planned its floor first, so it owes its demanded tier, which
        // Retier starts as soon as the floor stands, outside the budget's hold and its replacements in flight; whether
        // the group is that first replacement; and whether it is a floor a refused or evicted detail fell back to, whose
        // detail returns only through the budget.
        public bool FirstDetailOwed { get; set; }
        public bool FirstDetail { get; set; }
        public bool Evicted { get; set; }
        // Whether the group is a rung a ladder bake fills (SdfMeshPathBake.FillLadders): its product is its store entry
        // alone, so it never takes the draws, and the group it stands in for takes its place back when it is done.
        public bool Fill { get; set; }
        // Whether the group loads the mesh the store holds of its form, baked at another pose, in place of its own
        // geometry's (SdfMeshLibrary.FormGeometry).
        public bool LoadsForm { get; set; }
        // Whether the group's geometry is made as the world runs: it reads an operand's pose, which no two runs stand
        // at alike, or it was planned again because a sampled field it reads published another snapshot (Revisit). No
        // cache made beforehand holds such geometry, so it bakes under every cache policy, is counted apart, and has
        // no ladder filled.
        public bool Live { get => (m_live || Geometry.ReadsOperands); set => m_live = value; }

        private bool m_live;

        // Takes the liveness of a group planned at the same place: a sampled reader planned again at another tier, by a
        // later program or in an evicted floor's place reads the moved snapshot its predecessor was planned again for, so
        // it is live too (SdfMeshPathBake.Replace, Place, StartReplacement). An operand reader is live by its geometry.
        public void InheritLive(Group from) {
            if (from.m_live && Geometry.ReadsSampled) { m_live = true; }
        }
        // The loads of the group the account has had no room for in a row (SdfMeshPathBake.Reload).
        public int LoadRetries { get; set; }
        // Whether the group is a tier's replacement whose stored mesh the account had no room to read
        // (SdfMeshPathBake.Decline): it is done with nothing of its own, and the group it replaces takes its place back.
        public bool Declined { get; set; }

        private SdfMesh? m_floor;

        // The mesh a group planned at or past its floor tier landed, kept with its own claim while a finer tier draws, so
        // the group shows it again the moment that tier is refused or evicted (SdfMeshPathBake.Evict); the geometry it
        // was baked from, which a floor carried across a replan no longer matches, and the tier it was baked at. The
        // group's own product is its floor when it is planned at or past its floor tier (Floor).
        public SdfMesh? KeptFloor => m_floor;
        public SdfMeshGeometry? FloorGeometry { get; private set; }
        public int KeptFloorTier { get; private set; }
        // The shape (Shape) of the group the kept floor was baked for: a floor of another shape (an edit since, or a
        // selector another placement now holds) stands in for the group but is not its geometry.
        private UInt128 m_floorShape;
        // What the trace judge said of the kept floor (SdfMeshTraceJudge), shared by every group that keeps the same
        // floor, so a verdict reaching a group the floor has since passed to still counts.
        public TraceVerdict? FloorVerdict { get; private set; }
        // The group's floor: its own product at or past its floor tier, else the one it keeps.
        public SdfMesh? Floor => (AtFloor ? Mesh : m_floor);
        // The kept floor where it is of the group's own shape.
        public SdfMesh? CurrentFloor => (((m_floor is { } floor) && (m_floorShape == Shape)) ? floor : null);
        // The group's own product, once it has landed: not the stand-in it took from the group it replaced.
        private SdfMesh? OwnProduct => ((Done && (m_mesh is { } mesh) && !ReferenceEquals(objA: mesh, objB: m_standIn)) ? mesh : null);

        // The mesh the irradiance volume traces in place of the one the group draws (SdfMeshDraw.Trace), or null to trace
        // the drawn one: the group's floor, which the plan alone chooses, so a view's retier or the budget's bias leaves
        // the trace scene standing. A floor of the group's own shape kept beside a finer tier, unless the trace judge
        // declined it; at or past the floor tier the group's own floor product once it draws it, and until then what the
        // group it replaces traces, where that group is of the same shape. Nothing else: a group drawing its stand-in of
        // another shape, or no floor of its own shape, traces what it draws.
        public SdfMesh? Trace {
            get {
                if (CurrentFloor is { } kept) { return ((FloorVerdict?.Declined ?? false) ? null : kept); }
                if (!AtFloor) { return null; }
                if (!Ahead && (OwnProduct is { } own)) { return own; }
                return (((Replaces is { } replaced) && (replaced.Shape == Shape)) ? replaced.Trace : null);
            }
        }
        // Whether the trace judge owes a verdict on the kept floor: it is of the group's shape, unjudged, and the group's
        // own finer product has landed to hold it to.
        public bool OwesVerdict => ((CurrentFloor is not null) && (FloorVerdict is { State: TraceVerdict.Unjudged }) && !AtFloor && (OwnProduct is not null));
        // The finer product a verdict holds the kept floor to (OwesVerdict).
        public SdfMesh? JudgedAgainst => OwnProduct;

        // Keeps the floor of the group this one replaces: that group's own product at or past its floor tier, or the
        // floor it kept, with its shape, its tier and its verdict. A group planned at or past its floor tier keeps none,
        // since its product is its floor. Takes a claim of its own.
        public void KeepFloorOf(Group old) {
            if (AtFloor) { return; }

            var atFloor = old.AtFloor;
            var (floor, geometry, shape, tier) = (atFloor ? (old.Mesh, old.Sealed, old.Shape, old.Tier) : (old.m_floor, old.FloorGeometry, old.m_floorShape, old.KeptFloorTier));

            if ((floor is null) || ReferenceEquals(objA: floor, objB: m_floor)) { return; }
            m_floor?.Release();
            m_floor = floor.Acquire();
            FloorGeometry = geometry;
            KeptFloorTier = tier;
            m_floorShape = shape;
            FloorVerdict = (atFloor ? new TraceVerdict() : old.FloorVerdict);
            if (Owner is { } owner) {
                owner.m_floorRevision++;
                owner.Touch(group: this);
                owner.Keep(group: this);
                owner.Judgeable(group: this);
            }
        }

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

        // The group's tier's cell (SdfMeshTier), which floors FineCell for the essential cells' split.
        public float TierCell { get; init; }
        // Whether the group bakes past its floor tier, where the tier coarsened the lattice itself.
        public bool PastFloor { get; init; }
    }
    private enum FinalStage {
        Refinement,
        Crossings,
        Gradients,
    }
}
