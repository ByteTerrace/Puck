using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;

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
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => ((Dims.X * Dims.Y) * Dims.Z);
    /// <summary>Gets the field evaluations the dispatch makes: five a sample that measures the gradient, one
    /// otherwise.</summary>
    public int Taps => (Gradients ? (5 * Samples) : Samples);
    /// <summary>Gets the first plane along Z of its group's final lattice that a slab of it samples, or -1 for any other
    /// lattice.</summary>
    public int Slab { get; init; } = -1;
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
    /// <summary>Gets the point table entries the batch writes: its samples' points, then each lattice's poses.</summary>
    public int TableEntries {
        get {
            var entries = Samples;

            foreach (var (_, rows) in Poses) {
                entries += rows.Length;
            }

            return entries;
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
    /// <summary>Gets whether a bake pass recorded the batch (<see cref="SdfMeshPathBake.Record"/>); a batch no pass
    /// recorded returns to its groups when the next frame takes one.</summary>
    public bool Recorded { get; internal set; }

    private string? m_instance;

    /// <summary>Claims the batch for a view instance's mask and bake passes: the first instance to ask claims it, so a
    /// frame of several views of one residency dispatches its batch once.</summary>
    /// <param name="instance">The asking view instance.</param>
    /// <returns><see langword="true"/> when the batch is the instance's to dispatch.</returns>
    public bool Claim(string instance) =>
        string.Equals(a: (Interlocked.CompareExchange(comparand: null, location1: ref m_instance, value: instance) ?? instance), b: instance, comparisonType: StringComparison.Ordinal);

    /// <summary>Gets or sets whether the batch's samples were answered, which answers it once however many readbacks
    /// arrive.</summary>
    public bool Answered { get; set; }

    /// <summary>Writes the batch's point table: each points lattice's points at its first sample's place in the
    /// dispatch, then each lattice's poses from <see cref="PoseBase"/>.</summary>
    /// <param name="table">The table, at least <see cref="TableEntries"/> entries.</param>
    /// <returns>Whether the batch wrote an entry.</returns>
    public bool WritePoints(Span<Vector4> table) {
        var first = 0;
        var any = false;

        foreach (var lattice in Lattices) {
            if (!lattice.Points.IsEmpty) {
                lattice.Points.Span.CopyTo(destination: table[first..]);
                any = true;
            }
            first += lattice.Samples;
        }
        for (var index = 0; (index < Poses.Length); index++) {
            var rows = Poses[index].Rows;

            if (!rows.IsEmpty) {
                rows.Span.CopyTo(destination: table[PoseBase(lattice: index)..]);
                any = true;
            }
        }

        return any;
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
/// <summary>
/// The baked-mesh path's bake of one residency's program: a mesh per bake group, sampled from the program on the GPU and
/// contoured on the CPU, drawn in the frame's mesh draws by every view of the residency.
/// <para>A group is the instances one rigid frame moves: every active instance riding one dynamic-transform slot, or one
/// static copy, the run of static instances that bake with the first (<see cref="SdfInstanceRange.BakesWithPrevious"/>),
/// so shapes of one copy that share a face bake as their union. Each group's field is sampled through the frame's own
/// interpreter (<c>sdf-mesh-bake.comp.hlsl</c>) with only its instances in the walk's mask, lattices taken in the groups' order within a frame's share of field
/// evaluations (<see cref="FrameTaps"/>): a coarse lattice over the group's bounding sphere, refinements over the box of
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
    /// <summary>The field evaluations one frame's bake dispatch makes at most, past its first lattice, in a frame of
    /// <see cref="ReferenceFrameSeconds"/>: the bake pass's share of the frame. A longer frame takes the same share of its
    /// time (<see cref="FrameBudget"/>), and every lattice is sized by this share alone, so the meshes do not depend on the
    /// frame rate.</summary>
    public const int FrameTaps = 150_000;
    /// <summary>The frame, in seconds, <see cref="FrameTaps"/> is the bake's share of: a 60 Hz frame, so a display at 60 Hz
    /// or faster takes one share a frame, and only a frame longer than two reference frames takes more.</summary>
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
    // The finest spacing, in metres, of a final lattice along its longest axis.
    private const float MinimumCell = 0.015f;
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
    private const float RegionRatio = 1.5f;

    /// <summary>Gets the name of the residency whose program the bake meshes, which its start and done lines name.</summary>
    public string Residency { get; init; } = string.Empty;
    /// <summary>Gets the library the bake stores each group's mesh in and takes a group of geometry it holds from, rather
    /// than baking the group again: in a later program of the residency, or in another residency holding the same
    /// library.</summary>
    public SdfMeshLibrary Library { get; init; } = new();

    /// <summary>Gets or sets the identity of the bake on the kernels it dispatches (<see cref="SdfMeshStore.IdentityOf"/>),
    /// under which a new program's groups load from the library's store and its meshes are written there; the residency
    /// sets it from its kernel set. A group keeps the identity its program started under.</summary>
    public UInt128 Identity { get; set; }

    private readonly Lock m_gate = new();
    private readonly List<Group> m_groups = [];
    private readonly List<SdfMeshDraw> m_draws = [];
    private readonly Stopwatch m_clock = new();

    private SdfProgram? m_program;
    private IReadOnlyList<SdfMeshDraw>? m_frameDraws;

    private long m_frameDrawsRevision = -1;

    private long m_revision;

    private int m_drawnMeshes = -1;

    private long m_dispatches;
    private long m_samples;
    private long m_taps;
    private long m_discarded;
    private long m_refinedCells;
    // The mesh edges the material cuts searched for a boundary, and the vertices the field check moved onto the surface.
    private long m_cutEdges;
    private long m_softened;
    private double m_seconds;
    private bool m_reported;
    private long m_loaded;

    /// <summary>Gets the program the bake meshes: the one the last frame <see cref="Dress"/> took carried, or
    /// <see langword="null"/> before the first.</summary>
    public SdfProgram? Program {
        get {
            lock (m_gate) { return m_program; }
        }
    }
    /// <summary>Gets whether a group still owes a lattice the frame can dispatch, a contour still runs, or a field slice
    /// (<see cref="Probe"/>) still samples.</summary>
    public bool Pending {
        get {
            lock (m_gate) {
                if (m_slice is not null) { return true; }
                foreach (var group in m_groups) {
                    if (!group.Done) { return true; }
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

    /// <summary>Returns a frame that draws the bake's meshes beside its own mesh draws, restarting the bake when the frame
    /// carries a new program. A dynamic group draws at its slot's transform in the frame.</summary>
    /// <param name="frame">The captured frame.</param>
    /// <returns>The frame with the baked draws appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public SdfFrame Dress(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        lock (m_gate) {
            if (!ReferenceEquals(objA: frame.Program, objB: m_program)) {
                Restart(frame: frame);
            }
            Settle(frame: frame);

            var finished = 0;
            var moved = (!ReferenceEquals(objA: frame.MeshDraws, objB: m_frameDraws) || (frame.MeshDrawsRevision != m_frameDrawsRevision) || !ReferenceEquals(objA: frame.SlotClips, objB: m_slotClips));

            foreach (var group in m_groups) {
                if (group.Mesh is null) { continue; }
                finished++;
                if (group.Dynamic && (group.Slot < frame.DynamicTransforms.Count)) {
                    var transform = frame.DynamicTransforms[group.Slot];

                    if ((transform.Position != group.DrawnPosition) || (transform.Orientation != group.DrawnOrientation)) {
                        group.DrawnPosition = transform.Position;
                        group.DrawnOrientation = transform.Orientation;
                        moved = true;
                    }
                }
            }

            if (!moved && (finished == m_drawnMeshes) && ReferenceEquals(objA: frame, objB: m_dressedSource) && (m_dressed is { } dressed)) {
                return dressed;
            }
            if (moved || (finished != m_drawnMeshes)) {
                m_draws.Clear();
                m_draws.AddRange(collection: frame.MeshDraws);
                foreach (var group in m_groups) {
                    if (group.Mesh is not { } mesh) { continue; }
                    var matrix = (group.Dynamic
                        ? (Matrix4x4.CreateFromQuaternion(quaternion: group.DrawnOrientation) * Matrix4x4.CreateTranslation(position: group.DrawnPosition))
                        : Matrix4x4.Identity);

                    m_draws.Add(item: new SdfMeshDraw(Identity: group, Material: 0, Mesh: mesh, ObjectToWorld: matrix) { FrameSlot = (group.Dynamic ? group.Slot : null), IsDynamic = group.Dynamic, Glass = group.Glass });
                }
                // A body straddling a portal is drawn on both sides of it, each side clipped by its own glass.
                SdfSlotClip.Apply(
                    clips: frame.SlotClips,
                    draws: m_draws
                );
                m_slotClips = frame.SlotClips;
                m_frameDraws = frame.MeshDraws;
                m_frameDrawsRevision = frame.MeshDrawsRevision;
                m_drawnMeshes = finished;
                m_revision++;
            }

            m_dressedSource = frame;
            m_dressed = (frame with { MeshDraws = m_draws, MeshDrawsRevision = m_revision });

            return m_dressed;
        }
    }

    // The straddling slot runs the dressed draws were last clipped by.
    private IReadOnlyList<SdfSlotClip> m_slotClips = [];

    // The frame Dress last dressed and what it returned, which a still frame source capturing the same frame takes again
    // without a new frame.
    private SdfFrame? m_dressedSource;
    private SdfFrame? m_dressed;

    // The frame's lattices and their groups while TryTakeForFrame chooses them, reused frame to frame.
    private readonly List<SdfMeshBakeRequest> m_takenLattices = [];
    private readonly List<object> m_takenTickets = [];

    /// <summary>Returns the field evaluations a frame presented over <paramref name="frameSeconds"/> takes: the same share
    /// of its time a reference frame's <see cref="FrameTaps"/> are, between one and <see cref="MaxFrameShares"/> shares.</summary>
    /// <param name="frameSeconds">The interval the frame's presentation spans, in seconds; zero or less is a reference
    /// frame.</param>
    /// <returns>The frame's evaluations.</returns>
    public static int FrameBudget(double frameSeconds) =>
        (FrameTaps * ((int)Math.Clamp(value: Math.Floor(d: (frameSeconds / ReferenceFrameSeconds)), min: 1.0, max: MaxFrameShares)));
    /// <summary>Takes the lattices a frame dispatches: the first call of a frame takes the next lattice of each group that
    /// owes one and has none in flight, in the groups' order, up to a batch, and every later call of the same frame answers
    /// the same, so the frame's mask and bake passes agree. Taking them in order rather than round robin finishes the groups
    /// first in order first, each landing in the mesh library as it finishes, so a residency the program leaves mid-bake (a
    /// body crossing into another world) leaves the groups it finished there for the residency it reaches.</summary>
    /// <param name="frame">The package frame (<see cref="SdfWorldResidency.PackageFrame"/>).</param>
    /// <param name="batch">The lattices to dispatch and the group each answers, handed back to
    /// <see cref="Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/>.</param>
    /// <returns><see langword="true"/> when the frame dispatches a lattice.</returns>
    public bool TryTakeForFrame(long frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch) {
        lock (m_gate) {
            if (m_frameTaken != frame) {
                m_frameTaken = frame;
                // A batch no bake pass recorded, as while the view's graph rebuilds, answers nothing.
                if (m_frameBatch is { Recorded: false } unrecorded) { Return(batch: unrecorded); }
                m_frameBatch = null;

                var lattices = m_takenLattices;
                var tickets = m_takenTickets;

                lattices.Clear();
                tickets.Clear();
                m_takenPoses.Clear();
                var samples = 0;
                var entries = 0;
                var taps = 0;
                // A slice's lattices come first, within the same share, so a long bake never holds a diagnostic back.
                while ((m_slice is { } slice) && (lattices.Count < SdfWorldPackage.MeshBakeBatch)) {
                    if (slice.Take(instances: (m_program?.Instances.Count ?? 0), ticket: out var lattice, worldSelector: SdfMeshBakeGroup.WorldSelector) is not { } next) { break; }
                    if ((lattices.Count > 0) && (((taps + next.Taps) > m_frameBudget) || ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples))) {
                        slice.Return(ticket: lattice);
                        break;
                    }
                    samples += next.Samples;
                    entries += next.Samples;
                    taps += next.Taps;
                    lattices.Add(item: next);
                    tickets.Add(item: new SliceTicket(Generation: slice.Generation, Lattice: lattice, Slice: slice));
                    m_takenPoses.Add(item: (0, ReadOnlyMemory<Vector4>.Empty));
                }
                for (var index = 0; ((index < m_groups.Count) && (lattices.Count < SdfWorldPackage.MeshBakeBatch)); index++) {
                    var group = m_groups[index];

                    if (group.Done || group.InFlight || group.Contouring || (group.Next is not { } next)) { continue; }
                    if ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples) { break; }
                    // The point table holds the samples' points and the lattices' poses after them.
                    if (((entries + next.Samples) + group.PoseRows.Length) > SdfWorldPackage.MeshBakeMaxSamples) { break; }
                    // A frame's lattices stay within its share of field evaluations; its first is always taken, and no
                    // lattice holds more than a share.
                    if ((lattices.Count > 0) && ((taps + next.Taps) > m_frameBudget)) { break; }
                    group.InFlight = true;
                    samples += next.Samples;
                    entries += (next.Samples + group.PoseRows.Length);
                    taps += next.Taps;
                    lattices.Add(item: next);
                    tickets.Add(item: group);
                    m_takenPoses.Add(item: (group.PoseFirst, group.PoseRows));
                }
                if (lattices.Count > 0) {
                    m_frameBatch = new SdfMeshBakeBatch(Lattices: [.. lattices], Tickets: [.. tickets]) { Bake = this, Poses = [.. m_takenPoses] };
                }
            }
            batch = m_frameBatch;
            return (batch is not null);
        }
    }

    private readonly List<(int First, ReadOnlyMemory<Vector4> Rows)> m_takenPoses = [];
    private long m_frameTaken = -1;
    private int m_frameBudget = FrameTaps;

    private SdfMeshBakeBatch? m_frameBatch;

    private Vector4[] m_pointTable = [];

    /// <summary>Takes the frame's batch (<see cref="TryTakeForFrame"/>) before the frame's upload and writes its points
    /// into the tables' point table, which the bake kernel reads.</summary>
    /// <param name="frame">The package frame.</param>
    /// <param name="frameSeconds">The interval the frame's presentation spans, in seconds, which sizes its share
    /// (<see cref="FrameBudget"/>).</param>
    /// <param name="tables">The residency's tables.</param>
    public void Stage(long frame, double frameSeconds, SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        lock (m_gate) {
            if (m_frameTaken != frame) { m_frameBudget = FrameBudget(frameSeconds: frameSeconds); }
        }

        if (!TryTakeForFrame(batch: out var batch, frame: frame)) { return; }
        if (m_pointTable.Length < batch.TableEntries) { m_pointTable = new Vector4[SdfWorldPackage.MeshBakeMaxSamples]; }
        if (batch.WritePoints(table: m_pointTable)) {
            tables.WriteMeshBakePoints(points: m_pointTable.AsSpan(length: batch.TableEntries, start: 0));
        }
    }
    /// <summary>Returns a dispatched batch's lattices to their groups unanswered, as when its submission never completed;
    /// each group dispatches its lattice again.</summary>
    /// <param name="batch">The batch <see cref="TryTakeForFrame"/> took, or <see langword="null"/>.</param>
    public void Abandon(SdfMeshBakeBatch? batch) {
        if (batch is null) { return; }
        lock (m_gate) { Return(batch: batch); }
    }

    // Returns a batch's lattices to their groups, which dispatch them again; a recorded batch's samples were dispatched
    // for nothing.
    private void Return(SdfMeshBakeBatch batch) {
        for (var index = 0; (index < batch.Tickets.Length); index++) {
            if (batch.Tickets[index] is Group group) { group.InFlight = false; }
            if ((batch.Tickets[index] is SliceTicket ticket) && ReferenceEquals(objA: ticket.Slice, objB: m_slice) && (ticket.Generation == ticket.Slice.Generation)) {
                ticket.Slice.Return(ticket: ticket.Lattice);
            }
            m_discarded += (batch.Recorded ? batch.Lattices[index].Samples : 0);
        }
    }

    /// <summary>Marks a batch a bake pass records, which dispatches it, and counts its lattices, samples and field
    /// evaluations as the bake's work; a batch taken by a frame no bake pass records is no work.</summary>
    /// <param name="batch">The batch <see cref="TryTakeForFrame"/> took.</param>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    public void Record(SdfMeshBakeBatch batch) {
        ArgumentNullException.ThrowIfNull(argument: batch);

        lock (m_gate) {
            batch.Recorded = true;
            foreach (var lattice in batch.Lattices) {
                m_dispatches++;
                m_samples += lattice.Samples;
                m_taps += lattice.Taps;
            }
        }
    }
    /// <summary>Answers a dispatched batch with the samples its readback copied, each lattice its own slice.</summary>
    /// <param name="batch">The batch <see cref="TryTakeForFrame"/> took.</param>
    /// <param name="samples">The batch's samples, <see cref="SdfWorldPackage.MeshBakeSampleWords"/> words each, the
    /// lattices back to back.</param>
    public void Complete(SdfMeshBakeBatch batch, ReadOnlySpan<byte> samples) {
        ArgumentNullException.ThrowIfNull(argument: batch);

        lock (m_gate) {
            if (batch.Answered) { return; }
            batch.Answered = true;
        }

        var offset = 0;

        for (var index = 0; (index < batch.Lattices.Length); index++) {
            var lattice = batch.Lattices[index];
            var bytes = ((lattice.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint));

            Complete(request: in lattice, samples: samples.Slice(length: bytes, start: offset), ticket: batch.Tickets[index]);
            offset += bytes;
        }
    }

    // Answers one lattice: a refining lattice narrows its group's box, and a final one starts the group's contour on the
    // thread pool.
    private void Complete(object? ticket, in SdfMeshBakeRequest request, ReadOnlySpan<byte> samples) {
        if (ticket is SliceTicket sliced) {
            bool done;

            lock (m_gate) {
                // A slice a new program restarted, or one already finished, takes no answer from an earlier dispatch.
                if (!ReferenceEquals(objA: sliced.Slice, objB: m_slice) || (sliced.Slice.Generation != sliced.Generation)) { return; }
                done = sliced.Slice.Answer(samples: samples, ticket: sliced.Lattice);
            }
            if (done) {
                sliced.Slice.Complete();
                lock (m_gate) { m_slice = null; }
            }
            return;
        }
        if (ticket is not Group group) { return; }

        var count = request.Samples;
        var values = new float[count];
        var materials = new int[count];
        var gradients = (request.Gradients ? new float[(3 * count)] : null);
        // A points lattice without gradients answers each point's winning slot where a gradient would lie.
        var slots = ((!request.Gradients && !request.Points.IsEmpty) ? new int[count] : null);

        for (var index = 0; (index < count); index++) {
            var record = samples.Slice(start: ((index * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

            values[index] = BinaryPrimitives.ReadSingleLittleEndian(source: record);
            materials[index] = BinaryPrimitives.ReadInt32LittleEndian(source: record[4..]);
            if (slots is not null) {
                slots[index] = BinaryPrimitives.ReadInt32LittleEndian(source: record[8..]);
            }
            if (gradients is not null) {
                gradients[(3 * index)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[8..]);
                gradients[((3 * index) + 1)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[12..]);
                gradients[((3 * index) + 2)] = BinaryPrimitives.ReadSingleLittleEndian(source: record[16..]);
            }
        }

        if (!request.Points.IsEmpty) {
            lock (m_gate) {
                group.InFlight = false;
                if (!m_groups.Contains(item: group)) { return; }
                if (group.Skin is not null) {
                    AnswerPoints(group: group, materials: slots!, request: in request);
                } else if (group.Final is { } final) {
                    AnswerFinal(final: final, gradients: gradients, group: group, materials: materials, request: in request, values: values);
                } else if (group.Creases is not null) {
                    AnswerCreases(gradients: gradients!, group: group, request: in request, values: values);
                } else {
                    AnswerPoints(group: group, materials: materials, request: in request);
                }
            }
            return;
        }

        lock (m_gate) {
            group.InFlight = false;
            if (!m_groups.Contains(item: group)) { return; }
            if (request.Slab >= 0) {
                AnswerSlab(group: group, materials: materials, request: in request, values: values);
                return;
            }
            Refine(group: group, request: request, values: values);
        }
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
                bytes += (((((long)mesh.Positions.Length) * SdfMeshRegion.VertexBytes) + (((long)mesh.Indices.Length) * sizeof(uint))) + (((long)mesh.TriangleCount) * sizeof(uint)));
            }

            return $"[sdf.mesh-bake] residency={Residency} groups={m_groups.Count} meshes={meshes} pending={m_groups.Count(predicate: static group => !group.Done)} dispatches={m_dispatches} samples={m_samples} discarded={m_discarded} taps={m_taps} refined={m_refinedCells} edges={m_cutEdges} softened={m_softened} loaded={m_loaded} skinned={m_groups.Count(predicate: static group => (group.Mesh is { VertexMembers.IsEmpty: false }))} triangles={triangles} vertices={vertices} bytes={bytes} seconds={(m_reported ? m_seconds : m_clock.Elapsed.TotalSeconds):0.000}";
        }
    }

    // Starts the bake of a new program: one group per dynamic slot and per run of static instances baked together (one
    // rigid copy), each starting at the coarse lattice over its bounding box. The meshes of the previous program keep
    // drawing until their group's new mesh lands.
    private void Restart(SdfFrame frame) {
        var previous = m_groups.ToDictionary(keySelector: static group => group.Selector);

        m_program = frame.Program;
        m_groups.Clear();
        // A slice samples one program: a new one takes every lattice again.
        m_slice?.Reset();

        m_groups.AddRange(collection: SdfMeshBakeGroup.Plan(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: frame.FarDistance,
            program: frame.Program,
            transforms: frame.DynamicTransforms
        ));

        // A program rebuilt with a group's instructions unchanged keeps that group, its bake and any lattice in flight; only
        // a group whose geometry moved bakes again.
        var rebaked = 0;
        var loading = 0;

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (previous.TryGetValue(key: group.Selector, value: out var old) && (old.Sealed == group.Sealed) && old.MaterialOrder.AsSpan().SequenceEqual(other: group.MaterialOrder)) {
                m_groups[index] = old;
                continue;
            }
            // Geometry the library holds a mesh of, baked at another index or slot, in an earlier program (a body that left
            // the world and came back) or by another residency (a body that crossed from another world), takes that mesh: a
            // static group's is in world space and a dynamic group's in its slot's frame, which its geometry holds.
            if (Library.TryTake(geometry: group.Sealed, materials: group.MaterialOrder, mesh: out var baked)) {
                group.Mesh = baked;
                group.Done = true;
                continue;
            }

            if ((old is not null) && (old.Mesh is not null) && (old.Dynamic == group.Dynamic)) {
                group.Mesh = old.Mesh;
                group.DrawnPosition = old.DrawnPosition;
                group.DrawnOrientation = old.DrawnOrientation;
            }
            if (Begin(group: group)) {
                loading++;
                continue;
            }
            rebaked++;
        }
        if ((rebaked + loading) > 0) {
            m_dispatches = 0;
            m_samples = 0;
            m_taps = 0;
            m_discarded = 0;
            m_refinedCells = 0;
            m_cutEdges = 0;
            m_softened = 0;
            m_loaded = 0;
            m_reported = false;
            m_clock.Restart();
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] start residency={Residency} groups={m_groups.Count} baking={rebaked} loading={loading} dynamic={m_groups.Count(predicate: static group => group.Dynamic)} instances={frame.Program.Instances.Count}");
        }
        // The draws name their groups, so they are rebuilt only when a group was replaced or dropped; a program change
        // that keeps every group (a palette value) leaves the drawn meshes, and the mesh revision, alone.
        if (((rebaked + loading) > 0) || (previous.Count != m_groups.Count)) {
            m_drawnMeshes = -1;
        }
    }
    // Starts a group the library holds no mesh of at the coarse lattice over its box, under the bake's identity. Geometry
    // the library's store kept from an earlier run loads from disk off the frame's thread instead, and bakes only when the
    // store refuses its entry; true when it loads.
    private bool Begin(Group group) {
        // The coarse lattice is a cube about the bounding box's center, so its cells are cubes.
        var center = ((group.Low + group.High) * 0.5f);
        var half = (MathF.Max(x: (group.High.X - group.Low.X), y: MathF.Max(x: (group.High.Y - group.Low.Y), y: (group.High.Z - group.Low.Z))) * 0.5f);

        group.Low = (center - new Vector3(value: half));
        group.High = (center + new Vector3(value: half));
        group.Next = Lattice(gradients: false, group: group, longest: CoarseSamples, minimum: CoarseSamples);
        group.Identity = Identity;
        if (!Library.Holds(geometry: group.Sealed, identity: group.Identity)) { return false; }
        group.Deferred = group.Next;
        group.Next = null;
        group.Contouring = true;
        _ = Task.Run(action: () => Load(group: group));
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

        return new SdfMeshBakeRequest(Cell: cell, Dims: dims, Gradients: gradients, Origin: group.Low, Selector: group.Selector) { Instances = group.Instances };
    }
    // Narrows a group's box to the cells the surface can cross: every cell whose corners change sign, and every sample whose
    // value is within half the lattice's finest spacing of the surface, widened by one cell along each axis. A group with no
    // such cell has no surface. Once the box stops shrinking, the group's next lattice is the final one.
    private void Refine(Group group, in SdfMeshBakeRequest request, float[] values) {
        var (sx, sy, sz) = request.Dims;
        var near = (0.5f * MathF.Min(x: request.Cell.X, y: MathF.Min(x: request.Cell.Y, y: request.Cell.Z)));
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        int Index(int x, int y, int z) => (x + (sx * (y + (sy * z))));

        for (var z = 0; (z < sz); z++) {
            for (var y = 0; (y < sy); y++) {
                for (var x = 0; (x < sx); x++) {
                    var value = values[Index(x: x, y: y, z: z)];
                    var candidate = (MathF.Abs(x: value) <= near);

                    if (!candidate) {
                        var inside = (value < 0f);

                        candidate = ((((x + 1) < sx) && ((values[Index(x: (x + 1), y: y, z: z)] < 0f) != inside)) ||
                            (((y + 1) < sy) && ((values[Index(x: x, y: (y + 1), z: z)] < 0f) != inside)) ||
                            (((z + 1) < sz) && ((values[Index(x: x, y: y, z: (z + 1))] < 0f) != inside)));
                    }
                    if (!candidate) { continue; }

                    var point = (request.Origin + (new Vector3(x: x, y: y, z: z) * request.Cell));

                    low = Vector3.Min(value1: low, value2: point);
                    high = Vector3.Max(value1: high, value2: (point + request.Cell));
                }
            }
        }

        if (!(low.X <= high.X)) {
            group.Outcome = $"no surface after {group.Refinements} refinements";
            group.Next = null;
            group.Done = true;
            group.Mesh = null;
            Finish();
            return;
        }

        low -= request.Cell;
        high += request.Cell;
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
    // The final lattice of a group of one instance, or of a dynamic slot: FinalSamples along its longest axis, no finer than
    // MinimumCell. An instance that declares its own detail (SdfInstanceRange.Detail), static or riding the slot, refines
    // its bound's part of the box toward that spacing wherever the surface can enter a cell, as finely as
    // MaxDetailRefinement allows.
    private static FinalLattice OwnFinal(Group group) {
        var lattice = (Lattice(gradients: true, group: group, longest: FinalSamples, minimum: FinalMinimum) with { Gradients = false });
        var low = Vector3.Max(value1: group.Low, value2: group.DetailLow);
        var high = Vector3.Min(value1: group.High, value2: group.DetailHigh);

        if (!(group.Detail > 0f) || (high.X < low.X) || (high.Y < low.Y) || (high.Z < low.Z)) {
            return new FinalLattice(lattice: lattice);
        }

        var coarse = MathF.Max(x: lattice.Cell.X, y: MathF.Max(x: lattice.Cell.Y, y: lattice.Cell.Z));

        return new FinalLattice(lattice: lattice) {
            Factor = Math.Clamp(value: ((int)MathF.Ceiling(x: (coarse / group.Detail))), min: 2, max: MaxDetailRefinement),
            Regions = [new SdfLatticeRegion(High: high, Low: low)],
        };
    }
    // The final lattice of a union of static shapes (a copy's run), which keeps each shape at the resolution its own final
    // lattice would give it: a cubic lattice past the per-axis cap, at the finest member's spacing where that fits
    // UnionSamples and coarser where it does not, never coarser than the capped lattice over the union's box; a member
    // whose own spacing stays finer than that by more than RegionRatio is a region its refinement measures at its own
    // spacing (SdfSampledLattice.Refine). A member's own spacing is its bounding sphere's box at FinalSamples a side.
    private static FinalLattice UnionFinal(Group group) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var longest = MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z));
        var finest = float.PositiveInfinity;

        foreach (var (_, radius, _) in group.Members) {
            finest = MathF.Min(x: finest, y: MemberCell(radius: radius));
        }

        // A slab is whole planes along Z, so one plane stays within a frame's taps: half of them, leaving room for each
        // axis's end sample.
        var cell = MathF.Max(
            x: MathF.Min(x: (longest / (FinalSamples - 1)), y: MathF.Max(x: finest, y: MathF.Cbrt(x: (((extent.X * extent.Y) * extent.Z) / UnionSamples)))),
            y: MathF.Sqrt(x: ((extent.X * extent.Y) / (0.5f * FrameTaps)))
        );

        int Samples(float length) => Math.Max(val1: FinalMinimum, val2: (((int)MathF.Ceiling(x: (length / cell))) + 1));

        var dims = (X: Samples(length: extent.X), Y: Samples(length: extent.Y), Z: Samples(length: extent.Z));

        var spacing = new Vector3(x: (extent.X / (dims.X - 1)), y: (extent.Y / (dims.Y - 1)), z: (extent.Z / (dims.Z - 1)));
        var coarse = MathF.Max(x: spacing.X, y: MathF.Max(x: spacing.Y, y: spacing.Z));
        var regions = new List<SdfLatticeRegion>();
        var target = float.PositiveInfinity;
        var detailed = float.PositiveInfinity;

        foreach (var (center, radius, detail) in group.Members) {
            var own = ((detail > 0f) ? MathF.Min(x: MemberCell(radius: radius), y: detail) : MemberCell(radius: radius));

            if ((detail <= 0f) && ((own * RegionRatio) >= coarse)) { continue; }
            regions.Add(item: new SdfLatticeRegion(
                High: Vector3.Min(value1: group.High, value2: (center + new Vector3(value: radius))),
                Low: Vector3.Max(value1: group.Low, value2: (center - new Vector3(value: radius)))
            ));
            target = MathF.Min(x: target, y: own);
            if (detail > 0f) { detailed = MathF.Min(x: detailed, y: detail); }
        }

        var lattice = new SdfMeshBakeRequest(Cell: spacing, Dims: dims, Gradients: false, Origin: group.Low, Selector: group.Selector) { Instances = group.Instances };

        return new FinalLattice(lattice: lattice) {
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
        var planes = Math.Clamp(value: (FrameTaps / (x * y)), min: 1, max: (z - final.Planes));

        return final.Lattice with {
            Dims = (x, y, planes),
            Origin = (final.Lattice.Origin + new Vector3(x: 0f, y: 0f, z: (final.Planes * final.Lattice.Cell.Z))),
            Slab = final.Planes,
        };
    }
    // Answers a slab of a group's final lattice. Once every plane is answered, the lattice chooses the cells it
    // refines on the thread pool (Prepare).
    private void AnswerSlab(Group group, in SdfMeshBakeRequest request, float[] values, int[] materials) {
        var final = group.Final!;
        var start = (request.Slab * (request.Dims.X * request.Dims.Y));

        values.CopyTo(array: final.Values, index: start);
        materials.CopyTo(array: final.Materials, index: start);
        final.Planes += request.Dims.Z;
        if (final.Planes < final.Lattice.Dims.Z) {
            group.Next = SlabOf(group: group);
            return;
        }

        // A lattice all of one sign has no surface; it finishes here, in the frame its last slab lands.
        var inside = false;
        var outside = false;

        foreach (var value in final.Values) {
            inside |= (value < 0f);
            outside |= (value >= 0f);
        }
        if (!(inside && outside)) {
            group.Outcome = "no crossed cell in the final lattice";
            group.Final = null;
            group.Next = null;
            group.Done = true;
            group.Mesh = null;
            Finish();
            return;
        }

        group.Next = null;
        group.Contouring = true;
        _ = Task.Run(action: () => Prepare(final: final, group: group));
    }
    // Builds a group's sampled lattice from its final lattice and chooses the cells it refines: their fine samples are
    // measured next, or, with none, the gradients the contour reads.
    private void Prepare(Group group, FinalLattice final) {
        Vector4[]? points = null;
        string? refusal = null;

        try {
            var lattice = final.Lattice;

            final.Sampled = new SdfSampledLattice(
                cell: lattice.Cell,
                materials: final.Materials,
                origin: lattice.Origin,
                samplesX: lattice.Dims.X,
                samplesY: lattice.Dims.Y,
                samplesZ: lattice.Dims.Z,
                values: final.Values
            );

            var factor = ((final.Factor > 0)
                ? final.Factor
                : Math.Min(val1: MaxRefinement, val2: ((int)MathF.Ceiling(x: (MathF.Min(x: lattice.Cell.X, y: MathF.Min(x: lattice.Cell.Y, y: lattice.Cell.Z)) / FineCell)))));

            if (factor >= 2) {
                points = Homogeneous(points: final.Sampled.Refine(factor: factor, regions: final.Regions));
            }
            if ((points is null) || (points.Length == 0)) {
                points = ChooseCrossingPoints(final: final);
            } else {
                final.Stage = FinalStage.Refinement;
            }
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            refusal = exception.Message;
        }

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            if ((refusal is not null) || (points is null) || (points.Length == 0)) {
                if (refusal is not null) { Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} refused: {refusal}"); }
                group.Outcome = ((refusal is null) ? "no crossed cell in the final lattice" : "lattice refused");
                group.Final = null;
                group.Next = null;
                group.Done = true;
                group.Mesh = null;
                Finish();
                return;
            }
            final.Points = points;
            final.Answered = 0;
            group.Next = FinalChunk(final: final, group: group);
        }
    }
    private static Vector4[] Homogeneous(Vector3[] points) {
        var homogeneous = new Vector4[points.Length];

        for (var index = 0; (index < points.Length); index++) {
            homogeneous[index] = new Vector4(value: points[index], w: 1f);
        }

        return homogeneous;
    }
    // The next chunk of a group's fine, crossing-root or gradient samples, as many as a frame's share of field evaluations
    // takes. Gradient taps are spaced by the contour's finest cell.
    private static SdfMeshBakeRequest FinalChunk(Group group, FinalLattice final) {
        var gradients = (final.Stage == FinalStage.Gradients);
        var count = Math.Clamp(value: (FrameTaps / (gradients ? 5 : 1)), min: 1, max: (final.Points.Length - final.Answered));

        return new SdfMeshBakeRequest(Cell: final.Sampled!.ContourCell, Dims: (count, 1, 1), Gradients: gradients, Origin: Vector3.Zero, Selector: group.Selector) {
            Instances = group.Instances,
            Points = final.Points.AsMemory(length: count, start: final.Answered),
        };
    }
    // Answers fine samples, a round of edge-root values, or gradients. The next crossing round and the final gradients
    // are chosen on the thread pool; once every gradient is answered, the group contours there.
    private void AnswerFinal(Group group, FinalLattice final, in SdfMeshBakeRequest request, float[] values, int[] materials, float[]? gradients) {
        var sampled = final.Sampled!;

        if (final.Stage == FinalStage.Refinement) {
            sampled.AnswerRefinement(first: final.Answered, materials: materials, values: values);
        } else if (final.Stage == FinalStage.Crossings) {
            sampled.AnswerCrossings(first: final.Answered, values: values);
        } else {
            sampled.AnswerGradients(first: final.Answered, gradients: gradients!, values: values);
        }
        final.Answered += request.Samples;
        if (final.Answered < final.Points.Length) {
            group.Next = FinalChunk(final: final, group: group);
            return;
        }

        group.Next = null;
        group.Contouring = true;
        if (final.Stage != FinalStage.Gradients) {
            _ = Task.Run(action: () => ChooseCrossings(final: final, group: group));
            return;
        }

        group.Final = null;
        _ = Task.Run(action: () => Contour(group: group, lattice: sampled));
    }
    // Locates zeroes along the lattice edges before measuring their tangent planes. An off-edge normal projection of a
    // linear estimate can instead land on a different face of a corner, supplying the contour with the wrong plane.
    private static Vector4[] ChooseCrossingPoints(FinalLattice final) {
        var points = final.Sampled!.CrossingPoints();

        final.Stage = ((points.Length > 0) ? FinalStage.Crossings : FinalStage.Gradients);
        return Homogeneous(points: ((points.Length > 0) ? points : final.Sampled.GradientPoints()));
    }
    private void ChooseCrossings(Group group, FinalLattice final) {
        var refined = (final.Stage == FinalStage.Refinement);
        var points = ChooseCrossingPoints(final: final);

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            final.Points = points;
            final.Answered = 0;
            if (refined) { m_refinedCells += final.Sampled!.MeasuredCells; }
            group.Next = FinalChunk(final: final, group: group);
        }
    }
    // Contours a group's final lattice, then queues the first round of points its materials are sampled at
    // (SdfMeshMaterialCut).
    private void Contour(Group group, SdfSampledLattice lattice) {
        SdfContour? contour = null;

        try {
            contour = lattice.Contour();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} refused: {exception.Message}");
        }

        lock (m_gate) {
            group.Contouring = false;
            if ((contour is null) || (contour.QuadCount == 0)) {
                group.Outcome = ((contour is null) ? "contour refused" : "contour empty");
                group.Done = true;
                group.Mesh = null;
                Finish();
                return;
            }
            group.Contour = contour;

            var creases = new List<int>();

            for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
                if (((vertex < contour.Features.Length) && contour.Features[vertex]) || ((vertex < contour.Solved.Length) && contour.Solved[vertex])) { creases.Add(item: vertex); }
            }
            if (creases.Count == 0) {
                group.Contouring = true;
                _ = Task.Run(action: () => Split(group: group));
                return;
            }
            group.Creases = [.. creases];
            group.Projection = new ContourProjection(contour: contour);
            group.CreaseSpacing = lattice.ContourCell;
            group.CreaseSlope = lattice.SurfaceSlope();
            group.Points = new Vector4[creases.Count];
            for (var index = 0; (index < creases.Count); index++) {
                var vertex = creases[index];

                group.Points[index] = new Vector4(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)], w: 1f);
            }
            group.PointsAnswered = 0;
            group.Next = CreaseChunk(group: group);
        }
    }
    // The next chunk of the vertices a group's field check measures, as many as a frame's share of field evaluations takes.
    private static SdfMeshBakeRequest CreaseChunk(Group group) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: (FrameTaps / 5));

        return new SdfMeshBakeRequest(Cell: group.CreaseSpacing, Dims: (count, 1, 1), Gradients: true, Origin: Vector3.Zero, Selector: group.Selector) {
            Instances = group.Instances,
            Points = group.Points.AsMemory(length: count, start: group.PointsAnswered),
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
    // than KinkGradient of the field's typical slope sits on a crease the gradient's taps straddle and stays. Once every vertex is answered, the
    // contour splits at the creases that remain on the thread pool. Every movement is clipped before an incident
    // triangle reverses its original facing; an incomplete projection keeps its original crease and shading normal.
    private void AnswerCreases(Group group, in SdfMeshBakeRequest request, float[] values, float[] gradients) {
        var contour = group.Contour!;
        var tolerance = (CreaseTolerance * MathF.Min(x: group.CreaseSpacing.X, y: MathF.Min(x: group.CreaseSpacing.Y, y: group.CreaseSpacing.Z)));

        for (var index = 0; (index < request.Samples); index++) {
            var vertex = group.Creases![(group.PointsAnswered + index)];
            var gradient = new Vector3(x: gradients[(3 * index)], y: gradients[((3 * index) + 1)], z: gradients[((3 * index) + 2)]);
            var length = gradient.Length();

            if (!(length > (KinkGradient * group.CreaseSlope))) { continue; }

            var distance = (values[index] / length);

            if (!(MathF.Abs(x: distance) > tolerance)) { continue; }
            gradient /= length;
            var cut = ((vertex < contour.Beside.Length) && contour.Beside[vertex] && (distance < 0f));
            var point = group.Projection!.Move(amount: out var amount, contour: contour, displacement: (-distance * gradient), vertex: vertex);

            if (!(amount > 0f)) { continue; }
            contour.Positions[(3 * vertex)] = point.X;
            contour.Positions[((3 * vertex) + 1)] = point.Y;
            contour.Positions[((3 * vertex) + 2)] = point.Z;
            if ((MathF.Abs(x: distance) * (1f - amount)) <= tolerance) {
                if ((vertex < contour.Features.Length) && !cut) { contour.Features[vertex] = false; }
                contour.Normals[(3 * vertex)] = gradient.X;
                contour.Normals[((3 * vertex) + 1)] = gradient.Y;
                contour.Normals[((3 * vertex) + 2)] = gradient.Z;
            }
            m_softened++;
        }
        group.PointsAnswered += request.Samples;
        if (group.PointsAnswered < group.Points!.Length) {
            group.Next = CreaseChunk(group: group);
            return;
        }

        group.Creases = null;
        group.Projection = null;
        group.Points = null;
        group.Next = null;
        group.Contouring = true;
        _ = Task.Run(action: () => Split(group: group));
    }
    // Splits a group's contour at its creases, then queues the first round of points its materials are sampled at
    // (SdfMeshMaterialCut).
    private void Split(Group group) {
        group.Contour!.SelectDiagonals();
        var contour = group.Contour!.SplitAtCreases();

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            group.Contour = contour;
            group.Cut = new SdfMeshMaterialCut(contour: contour);
            group.Points = group.Cut.Points();
            group.PointMaterials = new int[group.Points.Length];
            group.PointsAnswered = 0;
            group.Next = PointChunk(group: group);
        }
    }
    // The next chunk of a group's material points, at most a frame's share of field evaluations.
    private static SdfMeshBakeRequest PointChunk(Group group) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: FrameTaps);

        return new SdfMeshBakeRequest(Cell: Vector3.One, Dims: (count, 1, 1), Gradients: false, Origin: Vector3.Zero, Selector: group.Selector) {
            Instances = group.Instances,
            Points = group.Points.AsMemory(length: count, start: group.PointsAnswered),
        };
    }
    // Answers a chunk of a group's material points; once a round's every point is answered, its material cut takes them
    // on the thread pool (Advance).
    private void AnswerPoints(Group group, in SdfMeshBakeRequest request, int[] materials) {
        materials.AsSpan(length: request.Samples, start: 0).CopyTo(destination: group.PointMaterials.AsSpan(start: group.PointsAnswered));
        group.PointsAnswered += request.Samples;
        if (group.PointsAnswered < group.Points!.Length) {
            group.Next = PointChunk(group: group);
            return;
        }

        group.Next = null;
        group.Contouring = true;
        _ = ((group.Skin is null) ? Task.Run(action: () => Advance(group: group)) : Task.Run(action: () => Land(group: group, mesh: Skinned(group: group))));
    }
    // Hands a round's materials to the group's cut, then queues its next round, or meshes the group once none is left.
    private void Advance(Group group) {
        var cut = group.Cut!;

        cut.Answer(materials: group.PointMaterials);

        var points = cut.Points();

        if (points.Length == 0) {
            Build(group: group);
            return;
        }

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { return; }
            group.Points = points;
            group.PointMaterials = new int[points.Length];
            group.PointsAnswered = 0;
            group.Next = PointChunk(group: group);
        }
    }
    // Builds a group's mesh from its contour, cut along its material boundaries.
    private void Build(Group group) {
        SdfMesh? mesh = null;

        try {
            mesh = group.Cut!.Build();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} refused: {exception.Message}");
        }

        lock (m_gate) {
            m_cutEdges += group.Cut!.Edges;
            group.Contour = null;
            group.Creases = null;
            group.Cut = null;
            // A group whose shapes ride slots besides its own asks which shape wins at each vertex, so each vertex follows
            // its own shape's slot as it moves (SdfMesh.Members).
            if ((mesh is not null) && group.Dynamic && group.Geometry.Poses.Keys.Any(predicate: slot => (slot != group.Slot))) {
                var positions = mesh.Positions.Span;
                var normals = mesh.Normals.Span;
                var points = new Vector4[positions.Length];

                for (var vertex = 0; (vertex < points.Length); vertex++) {
                    points[vertex] = new Vector4(value: (positions[vertex] + (normals.IsEmpty ? Vector3.Zero : (normals[vertex] * SkinLift))), w: 0f);
                }
                group.Contouring = false;
                group.Skin = mesh;
                group.Points = points;
                group.PointMaterials = new int[points.Length];
                group.PointsAnswered = 0;
                group.Next = PointChunk(group: group);
                return;
            }
        }
        Land(group: group, mesh: mesh);
    }
    // The skin round's answer: the group's mesh with each vertex riding the member slot that won at it, or the mesh as it
    // is when every vertex's winner is the group's own slot.
    private static SdfMesh Skinned(Group group) {
        var mesh = group.Skin!;
        var slots = group.PointMaterials;
        var others = group.Geometry.Poses.Where(predicate: pose => (pose.Key != group.Slot)).Take(count: SdfMesh.MaxMembers).ToArray();
        var vertexMembers = new byte[slots.Length];
        var any = false;

        for (var vertex = 0; (vertex < slots.Length); vertex++) {
            var member = Array.FindIndex(array: others, match: pose => (pose.Key == slots[vertex]));

            vertexMembers[vertex] = ((member < 0) ? SdfMesh.NoMember : (byte)member);
            any |= (member >= 0);
        }
        if (!any) { return mesh; }

        var members = others.Select(selector: pose => new SdfMeshMember(Orientation: pose.Value.Orientation, Position: pose.Value.Position, SlotOffset: (pose.Key - group.Slot))).ToArray();

        return new SdfMesh(indices: mesh.Indices, members: members, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: mesh.TriangleMaterials, vertexMembers: vertexMembers);
    }
    // Lands a group's finished mesh: in the library, which keeps it for another program or residency and its store, and as
    // the group's drawn mesh.
    private void Land(Group group, SdfMesh? mesh) {
        if (mesh is not null) {
            Library.Store(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: mesh);
        }

        lock (m_gate) {
            group.Contouring = false;
            group.Done = true;
            Draw(group: group, mesh: mesh);
            group.Outcome = ((mesh is null) ? "mesh refused" : null);
            group.Skin = null;
            group.Points = null;
            group.PointMaterials = [];
            Finish();
        }
    }
    // Takes a group's mesh from the library's store; a group whose entry the store refuses or no longer holds bakes from
    // its coarse lattice.
    private void Load(Group group) {
        var loaded = Library.TryLoad(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var mesh, refusal: out var refusal);

        lock (m_gate) {
            group.Contouring = false;
            if (refusal is not null) {
                Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} store refused: {refusal}");
            }
            if (!loaded) {
                group.Next = group.Deferred;
                return;
            }
            m_loaded++;
            group.Done = true;
            group.Stored = true;
            Draw(group: group, mesh: mesh);
            Finish();
        }
    }
    // Makes a group's new mesh the one it draws. A group rebaking after an edit keeps drawing its previous mesh until this
    // one lands, so the count of drawn meshes alone does not move: the draws are marked stale so Dress lists the new one.
    private void Draw(Group group, SdfMesh? mesh) {
        group.Mesh = mesh;
        m_drawnMeshes = -1;
    }
    // Records the bake's wall time once its last group finishes, and reports it.
    private void Finish() {
        foreach (var group in m_groups) {
            if (!group.Done) { return; }
        }
        if (m_reported) { return; }
        m_seconds = m_clock.Elapsed.TotalSeconds;
        m_reported = true;
        Console.Error.WriteLine(value: DescribeUnlocked());
    }
    private string DescribeUnlocked() {
        var meshes = m_groups.Count(predicate: static group => (group.Mesh is not null));
        var triangles = m_groups.Sum(selector: static group => ((long)(group.Mesh?.TriangleCount ?? 0)));
        var vertices = m_groups.Sum(selector: static group => ((long)(group.Mesh?.Positions.Length ?? 0)));
        // The meshes some of whose vertices ride a member's slot rather than the group's.
        var skinned = m_groups.Count(predicate: static group => (group.Mesh is { VertexMembers.IsEmpty: false }));

        // The groups that finished with no mesh, each with why: a group the bake drops renders nothing.
        var empty = string.Join(separator: "; ", values: m_groups.Where(predicate: static group => (group.Mesh is null)).Select(selector: static group => $"{group.Selector}: {group.Outcome}"));

        return ($"[sdf.mesh-bake] done residency={Residency} groups={m_groups.Count} meshes={meshes} dispatches={m_dispatches} samples={m_samples} discarded={m_discarded} taps={m_taps} refined={m_refinedCells} edges={m_cutEdges} softened={m_softened} loaded={m_loaded} skinned={skinned} triangles={triangles} vertices={vertices} seconds={m_seconds:0.000}" + ((empty.Length > 0) ? $" empty=[{empty}]" : string.Empty));
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
    // How far off the surface, along its normal, a vertex's skin point lies, where a hit reads the shape whose surface the
    // vertex lies on: the material cut's lift.
    private const float SkinLift = 0.0005f;

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
        public SdfMesh? Skin { get; set; }
        public bool Contouring { get; set; }
        public bool Done { get; set; }
        public bool InFlight { get; set; }
        public SdfMesh? Mesh { get; set; }
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
        public float CreaseSlope { get; set; } = 1f;

        public Vector4[]? Points { get; set; }

        public int[] PointMaterials { get; set; } = [];

        public Vector3 DrawnPosition { get; set; }
        public int PointsAnswered { get; set; }

        public Quaternion DrawnOrientation { get; set; } = Quaternion.Identity;

        // The final lattice while its slabs and corner gradients are sampled.
        public FinalLattice? Final { get; set; }
        // Why a finished group has no mesh, or null.
        public string? Outcome { get; set; }
        // The identity of the bake the group started under, and the coarse lattice it bakes from when its load fails.
        public UInt128 Identity { get; set; }
        public SdfMeshBakeRequest? Deferred { get; set; }
        // Whether the mesh came from the library or its store rather than a bake of this group.
        public bool Stored { get; set; }
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

        // Takes the member slots and their baked poses from the group's geometry, once.
        public void Watch() {
            var members = Geometry.Poses.Where(predicate: pose => (pose.Key != Slot)).ToArray();

            MemberSlots = [.. members.Select(selector: static pose => pose.Key)];
            BakedPositions = [.. members.Select(selector: static pose => pose.Value.Position)];
            BakedOrientations = [.. members.Select(selector: static pose => pose.Value.Orientation)];
            CurrentPositions = new Vector3[members.Length];
            CurrentOrientations = new Quaternion[members.Length];
            StillPositions = new Vector3[members.Length];
            StillOrientations = new Quaternion[members.Length];
        }
    }
    // A group's final lattice: its values and materials, sampled a slab at a time, then the sampled lattice built from
    // them and the stage of points it is answering: refinement's fine samples, bracketed edge roots, then the samples
    // its contour reads a gradient at.
    private sealed class FinalLattice(SdfMeshBakeRequest lattice) {
        public SdfMeshBakeRequest Lattice { get; } = lattice;
        public float[] Values { get; } = new float[lattice.Samples];
        public int[] Materials { get; } = new int[lattice.Samples];

        public int Planes { get; set; }
        public SdfSampledLattice? Sampled { get; set; }
        public FinalStage Stage { get; set; }

        public Vector4[] Points { get; set; } = [];

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
