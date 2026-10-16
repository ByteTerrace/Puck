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
/// <param name="Selector">A dynamic-transform slot whose instances the bake samples when non-negative, the one instance
/// <c>-(Selector + 1)</c> otherwise.</param>
/// <param name="Gradients">Whether the sampler measures the field's gradient at each sample: a points lattice's, at the
/// corners of the final lattice's crossed cells.</param>
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
}
/// <summary>The lattices one bake dispatch samples, at most <see cref="SdfWorldPackage.MeshBakeBatch"/> holding at most
/// <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples together, and the group each answers.</summary>
/// <param name="Lattices">The lattices, in dispatch order.</param>
/// <param name="Tickets">The group each lattice answers.</param>
public sealed record SdfMeshBakeBatch(SdfMeshBakeRequest[] Lattices, object[] Tickets) {
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => Lattices.Sum(selector: static lattice => lattice.Samples);
    /// <summary>Gets or sets whether a bake pass recorded the batch; a batch no pass recorded returns to its groups when
    /// the next frame takes one.</summary>
    public bool Recorded { get; set; }
    /// <summary>Writes the batch's point table: each points lattice's points at its first sample's place in the
    /// dispatch.</summary>
    /// <param name="table">The table, at least <see cref="Samples"/> entries.</param>
    /// <returns>Whether the batch holds a points lattice.</returns>
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

        return any;
    }
}
/// <summary>
/// The baked-mesh path's bake of one residency's program: a mesh per bake group, sampled from the program on the GPU and
/// contoured on the CPU, drawn in the frame's mesh draws while a view of the residency takes the mesh path.
/// <para>A group is the instances one rigid frame moves: every active instance riding one dynamic-transform slot, or one
/// static instance. Each group's field is sampled through the frame's own interpreter (<c>sdf-mesh-bake.comp.hlsl</c>)
/// with only its instances in the walk's mask, lattices round robin across the groups within a frame's share of field
/// evaluations (<see cref="FrameTaps"/>): a coarse lattice over the group's bounding sphere, refinements over the box of
/// the cells the surface can cross until the box stops shrinking, then a final lattice of at most 65 samples a side
/// whose spacing follows the box's own proportions, sampled in slabs of whole planes, values alone, and then gradients
/// at the corners of the cells whose corners disagree in sign alone, the only samples a contour reads one at. The final
/// samples contour on the thread pool (<see cref="SdfSampledLattice"/>). A dynamic group bakes in its slot's frame: its
/// box, lattices and points are in that frame, and the kernel carries every sample through the slot's transform in the
/// frame that dispatches it, so a body moving while it bakes is sampled where it is. Its vertices stay in that frame, and
/// its draw follows the slot's live transform; a static group's are in world space.</para>
/// <para>A new program restarts the bake; the meshes of the last finished bake keep drawing until each group's new mesh
/// lands. The bake reports its counted work and the wall time it took (<see cref="Describe"/>).</para>
/// </summary>
public sealed class SdfMeshPathBake {
    /// <summary>The field evaluations one frame's bake dispatch makes at most, past its first lattice: the bake pass's
    /// share of the frame.</summary>
    public const int FrameTaps = 150_000;
    // Samples along the longest axis of a coarse or refining lattice, and of the final one.
    private const int CoarseSamples = 33;
    private const int FinalSamples = (SdfWorldPackage.MeshBakeMaxSamplesPerAxis);
    // The fewest samples along any axis of a refining lattice, and of the final one, so a thin axis keeps an inside sample.
    private const int RefineMinimum = 17;
    private const int FinalMinimum = 9;
    private const int MaxRefinements = 5;
    // The finest spacing, in metres, of a final lattice along its longest axis.
    private const float MinimumCell = 0.015f;
    // The selector of the group of segments no instance owns, which the bake kernel samples with every instance masked
    // out and the world segments kept. KEEP IN SYNC with sdf-mesh-bake.comp.hlsl.
    private const int WorldSelector = int.MinValue;
    // A refinement whose box keeps this share of every extent has converged.
    private const float Converged = 0.7f;

    private readonly Lock m_gate = new();
    private readonly List<Group> m_groups = [];
    private readonly List<SdfMeshDraw> m_draws = [];
    private readonly Stopwatch m_clock = new();

    private SdfProgram? m_program;
    private int m_cursor;
    private IReadOnlyList<SdfMeshDraw>? m_frameDraws;
    private long m_frameDrawsRevision = -1;
    private long m_revision;
    private int m_drawnMeshes = -1;
    private long m_dispatches;
    private long m_samples;
    private long m_taps;
    private double m_seconds;
    private bool m_reported;

    /// <summary>Gets whether a group still owes a lattice the frame can dispatch, or a contour still runs.</summary>
    public bool Pending {
        get {
            lock (m_gate) {
                foreach (var group in m_groups) {
                    if (!group.Done) { return true; }
                }
                return false;
            }
        }
    }

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

            var finished = 0;
            var moved = (!ReferenceEquals(objA: frame.MeshDraws, objB: m_frameDraws) || (frame.MeshDrawsRevision != m_frameDrawsRevision));

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

            if (moved || (finished != m_drawnMeshes)) {
                m_draws.Clear();
                m_draws.AddRange(collection: frame.MeshDraws);
                foreach (var group in m_groups) {
                    if (group.Mesh is not { } mesh) { continue; }
                    var matrix = (group.Dynamic
                        ? (Matrix4x4.CreateFromQuaternion(quaternion: group.DrawnOrientation) * Matrix4x4.CreateTranslation(position: group.DrawnPosition))
                        : Matrix4x4.Identity);

                    m_draws.Add(item: new SdfMeshDraw(Identity: group, Material: 0, Mesh: mesh, ObjectToWorld: matrix) { FrameSlot = (group.Dynamic ? group.Slot : null), IsDynamic = group.Dynamic });
                }
                m_frameDraws = frame.MeshDraws;
                m_frameDrawsRevision = frame.MeshDrawsRevision;
                m_drawnMeshes = finished;
                m_revision++;
            }

            return frame with { MeshDraws = m_draws, MeshDrawsRevision = m_revision };
        }
    }
    /// <summary>Takes the lattices a frame dispatches: the first call of a frame takes the next lattice of each group that
    /// owes one and has none in flight, round robin across the groups, up to a batch, and every later call of the same
    /// frame answers the same, so the frame's mask and bake passes agree.</summary>
    /// <param name="frame">The package frame (<see cref="SdfWorldResidency.PackageFrame"/>).</param>
    /// <param name="batch">The lattices to dispatch and the group each answers, handed back to
    /// <see cref="Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/>.</param>
    /// <returns><see langword="true"/> when the frame dispatches a lattice.</returns>
    public bool TryTakeForFrame(long frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch) {
        lock (m_gate) {
            if (m_frameTaken != frame) {
                m_frameTaken = frame;
                // A batch no bake pass recorded, as while the view's graph rebuilds onto the mesh path, answers nothing.
                if (m_frameBatch is { Recorded: false } unrecorded) {
                    foreach (var ticket in unrecorded.Tickets) {
                        if (ticket is Group stale) { stale.InFlight = false; }
                    }
                }
                m_frameBatch = null;

                var lattices = new List<SdfMeshBakeRequest>();
                var tickets = new List<object>();
                var samples = 0;
                var taps = 0;
                var start = m_cursor;

                for (var step = 0; (step < m_groups.Count) && (lattices.Count < SdfWorldPackage.MeshBakeBatch); step++) {
                    var index = ((start + step) % m_groups.Count);
                    var group = m_groups[index];

                    if (group.Done || group.InFlight || group.Contouring || (group.Next is not { } next)) { continue; }
                    if ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples) { break; }
                    // A frame's lattices stay within its share of field evaluations; its first is always taken, and no
                    // lattice holds more than a share.
                    if ((lattices.Count > 0) && ((taps + next.Taps) > FrameTaps)) { break; }
                    group.InFlight = true;
                    m_cursor = ((index + 1) % m_groups.Count);
                    m_dispatches++;
                    m_samples += next.Samples;
                    m_taps += next.Taps;
                    samples += next.Samples;
                    taps += next.Taps;
                    lattices.Add(item: next);
                    tickets.Add(item: group);
                }
                if (lattices.Count > 0) {
                    m_frameBatch = new SdfMeshBakeBatch(Lattices: [.. lattices], Tickets: [.. tickets]);
                }
            }
            batch = m_frameBatch;
            return (batch is not null);
        }
    }

    private long m_frameTaken = -1;
    private SdfMeshBakeBatch? m_frameBatch;
    private Vector4[] m_pointTable = [];

    /// <summary>Takes the frame's batch (<see cref="TryTakeForFrame"/>) before the frame's upload and writes its points
    /// into the tables' point table, which the bake kernel reads.</summary>
    /// <param name="frame">The package frame.</param>
    /// <param name="tables">The residency's tables.</param>
    public void Stage(long frame, SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        if (!TryTakeForFrame(batch: out var batch, frame: frame)) { return; }
        if (m_pointTable.Length < batch.Samples) { m_pointTable = new Vector4[SdfWorldPackage.MeshBakeMaxSamples]; }
        if (batch.WritePoints(table: m_pointTable)) {
            tables.WriteMeshBakePoints(points: m_pointTable.AsSpan(length: batch.Samples, start: 0));
        }
    }
    /// <summary>Returns a dispatched batch's lattices to their groups unanswered, as when its submission never completed;
    /// each group dispatches its lattice again.</summary>
    /// <param name="batch">The batch <see cref="TryTakeForFrame"/> took, or <see langword="null"/>.</param>
    public void Abandon(SdfMeshBakeBatch? batch) {
        if (batch is null) { return; }
        lock (m_gate) {
            foreach (var ticket in batch.Tickets) {
                if (ticket is Group group) { group.InFlight = false; }
            }
        }
    }
    /// <summary>Answers a dispatched batch with the samples its readback copied, each lattice its own slice.</summary>
    /// <param name="batch">The batch <see cref="TryTakeForFrame"/> took.</param>
    /// <param name="samples">The batch's samples, <see cref="SdfWorldPackage.MeshBakeSampleWords"/> words each, the
    /// lattices back to back.</param>
    public void Complete(SdfMeshBakeBatch batch, ReadOnlySpan<byte> samples) {
        ArgumentNullException.ThrowIfNull(argument: batch);

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
        if (ticket is not Group group) { return; }

        var count = request.Samples;
        var values = new float[count];
        var materials = new int[count];
        var gradients = (request.Gradients ? new float[(3 * count)] : null);

        for (var index = 0; (index < count); index++) {
            var record = samples.Slice(start: ((index * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

            values[index] = BinaryPrimitives.ReadSingleLittleEndian(source: record);
            materials[index] = BinaryPrimitives.ReadInt32LittleEndian(source: record[4..]);
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
                if (request.Gradients) {
                    AnswerGradients(gradients: gradients!, group: group, request: in request);
                } else {
                    AnswerPoints(group: group, materials: materials, request: in request);
                }
            }
            return;
        }

        // The interpreter's walk can answer with a shape outside the group where it shares compiled parts with another
        // instance; a sample whose winning material no shape of the group carries reads as outside, so the group meshes
        // its own surface alone.
        if (group.Materials.Count > 0) {
            for (var index = 0; (index < count); index++) {
                if (!group.Materials.Contains(item: materials[index])) { values[index] = MathF.Abs(values[index]); }
            }
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
                bytes += (((long)mesh.Positions.Length * SdfMeshRegion.VertexBytes) + ((long)mesh.Indices.Length * sizeof(uint)) + ((long)mesh.TriangleCount * sizeof(uint)));
            }

            return $"[sdf.mesh-bake] groups={m_groups.Count} meshes={meshes} pending={m_groups.Count(predicate: static group => !group.Done)} dispatches={m_dispatches} samples={m_samples} taps={m_taps} triangles={triangles} vertices={vertices} bytes={bytes} seconds={(m_reported ? m_seconds : m_clock.Elapsed.TotalSeconds):0.000}";
        }
    }

    // Starts the bake of a new program: one group per dynamic slot and per static instance, each starting at the coarse
    // lattice over its bounding sphere. The meshes of the previous program keep drawing until their group's new mesh lands.
    private void Restart(SdfFrame frame) {
        var previous = m_groups.ToDictionary(keySelector: static group => group.Selector);

        m_program = frame.Program;
        m_groups.Clear();
        m_cursor = 0;

        var bySlot = new Dictionary<int, Group>();
        var instructions = frame.Program.Instructions;
        var instances = frame.Program.Instances;
        var reach = MathF.Max(1f, frame.FarDistance);

        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];

            if (!instance.Active || instance.CameraHidden) { continue; }

            var radius = (float.IsFinite(f: instance.Radius) ? MathF.Min(instance.Radius, reach) : reach);
            // What the instance contributes to its group's geometry: its bound and every instruction it owns.
            var key = HashCode.Combine(value1: instance.Center, value2: instance.Radius, value3: (instance.End - instance.First));

            var owned = new HashSet<int>();

            for (var instruction = instance.First; (instruction < instance.End); instruction++) {
                key = HashCode.Combine(value1: key, value2: instructions[instruction]);
                if (instructions[instruction].Op == SdfOp.ShapeBlend) { _ = owned.Add(item: (int)instructions[instruction].Material); }
            }

            if (instance.IsDynamic) {
                // A dynamic bound is a sphere about the slot's position offset along world axes; about the slot's origin
                // in its own frame, its center is that offset turned back by the slot's orientation in this frame.
                var orientation = ((instance.Slot < frame.DynamicTransforms.Count) ? frame.DynamicTransforms[instance.Slot].Orientation : default);
                var center = Vector3.Transform(value: instance.Center, rotation: Quaternion.Inverse(value: ((orientation == default) ? Quaternion.Identity : orientation)));

                if (!bySlot.TryGetValue(key: instance.Slot, value: out var group)) {
                    group = new Group(selector: instance.Slot, dynamic: true) {
                        Low = (center - new Vector3(value: radius)),
                        High = (center + new Vector3(value: radius)),
                    };
                    bySlot.Add(key: instance.Slot, value: group);
                    m_groups.Add(item: group);
                } else {
                    group.Low = Vector3.Min(value1: group.Low, value2: (center - new Vector3(value: radius)));
                    group.High = Vector3.Max(value1: group.High, value2: (center + new Vector3(value: radius)));
                }
                group.Key = HashCode.Combine(value1: group.Key, value2: key);
                group.Materials.UnionWith(other: owned);
            } else {
                var group = new Group(selector: -(index + 1), dynamic: false) {
                    Low = (instance.Center - new Vector3(value: radius)),
                    High = (instance.Center + new Vector3(value: radius)),
                    Key = key,
                };

                group.Materials.UnionWith(other: owned);
                m_groups.Add(item: group);
            }
        }

        // The segments no instance owns, which every instance group's walk omits, bake as one group of their own, over a
        // cube the frame's far distance wide about the origin.
        if (WorldInstructions(program: frame.Program) is { } worldKey) {
            m_groups.Add(item: new Group(selector: WorldSelector, dynamic: false) {
                High = new Vector3(value: reach),
                Key = worldKey,
                Low = new Vector3(value: -reach),
            });
        }

        // A program rebuilt with a group's instructions unchanged keeps that group, its bake and any lattice in flight; only
        // a group whose geometry moved bakes again.
        var rebaked = 0;

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (previous.TryGetValue(key: group.Selector, value: out var old) && (old.Key == group.Key) && (old.Dynamic == group.Dynamic)) {
                m_groups[index] = old;
                continue;
            }

            // The coarse lattice is a cube about the bounding box's center, so its cells are cubes.
            var center = ((group.Low + group.High) * 0.5f);
            var half = (MathF.Max((group.High.X - group.Low.X), MathF.Max((group.High.Y - group.Low.Y), (group.High.Z - group.Low.Z))) * 0.5f);

            group.Low = (center - new Vector3(value: half));
            group.High = (center + new Vector3(value: half));
            group.Next = Lattice(group: group, longest: CoarseSamples, minimum: CoarseSamples, gradients: false);
            if ((old is not null) && (old.Mesh is not null) && (old.Dynamic == group.Dynamic)) {
                group.Mesh = old.Mesh;
                group.DrawnPosition = old.DrawnPosition;
                group.DrawnOrientation = old.DrawnOrientation;
            }
            rebaked++;
        }
        if (rebaked > 0) {
            m_dispatches = 0;
            m_samples = 0;
            m_taps = 0;
            m_reported = false;
            m_clock.Restart();
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] start groups={m_groups.Count} baking={rebaked} dynamic={bySlot.Count} instances={instances.Count}");
        }
        m_drawnMeshes = -1;
    }
    // A key of the shapes the program's segments outside every instance hold, or null when none holds a shape.
    private static int? WorldInstructions(SdfProgram program) {
        var instructions = program.Instructions;
        var owned = new bool[instructions.Count];

        foreach (var instance in program.Instances) {
            for (var index = instance.First; (index < instance.End); index++) { owned[index] = true; }
        }

        int? key = null;

        for (var index = 0; (index < instructions.Count); index++) {
            if (owned[index]) { continue; }
            if ((instructions[index].Op == SdfOp.ShapeBlend) && !instructions[index].Detail) { key ??= 0; }
            if (key is { } running) { key = HashCode.Combine(value1: running, value2: instructions[index]); }
        }

        return key;
    }
    // The lattice over a group's box: `longest` samples along its longest axis and the same spacing along the others,
    // never fewer than `minimum` samples along any. A final lattice's spacing is never finer than MinimumCell, so a small
    // part meshes at the detail a view resolves rather than at its own scale.
    private static SdfMeshBakeRequest Lattice(Group group, int longest, int minimum, bool gradients) {
        var extent = Vector3.Max(value1: (group.High - group.Low), value2: new Vector3(value: 1.0e-4f));
        var target = (MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / (longest - 1));

        if (gradients) { target = MathF.Max(target, MinimumCell); }

        int Samples(float length) => Math.Clamp(value: (((int)MathF.Ceiling(length / target)) + 1), min: minimum, max: FinalSamples);

        var dims = (X: Samples(length: extent.X), Y: Samples(length: extent.Y), Z: Samples(length: extent.Z));
        var cell = new Vector3(x: (extent.X / (dims.X - 1)), y: (extent.Y / (dims.Y - 1)), z: (extent.Z / (dims.Z - 1)));

        return new SdfMeshBakeRequest(Cell: cell, Dims: dims, Gradients: gradients, Origin: group.Low, Selector: group.Selector);
    }
    // Narrows a group's box to the cells the surface can cross: every cell whose corners change sign, and every sample whose
    // value is within half the lattice's finest spacing of the surface, widened by one cell along each axis. A group with no
    // such cell has no surface. Once the box stops shrinking, the group's next lattice is the final one.
    private void Refine(Group group, in SdfMeshBakeRequest request, float[] values) {
        var (sx, sy, sz) = request.Dims;
        var near = (0.5f * MathF.Min(request.Cell.X, MathF.Min(request.Cell.Y, request.Cell.Z)));
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        int Index(int x, int y, int z) => (x + (sx * (y + (sy * z))));

        for (var z = 0; (z < sz); z++) {
            for (var y = 0; (y < sy); y++) {
                for (var x = 0; (x < sx); x++) {
                    var value = values[Index(x: x, y: y, z: z)];
                    var candidate = (MathF.Abs(value) <= near);

                    if (!candidate) {
                        var inside = (value < 0f);

                        candidate = (((x + 1) < sx) && ((values[Index(x: (x + 1), y: y, z: z)] < 0f) != inside)) ||
                            (((y + 1) < sy) && ((values[Index(x: x, y: (y + 1), z: z)] < 0f) != inside)) ||
                            (((z + 1) < sz) && ((values[Index(x: x, y: y, z: (z + 1))] < 0f) != inside));
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
            var final = Lattice(group: group, longest: FinalSamples, minimum: FinalMinimum, gradients: true);

            group.Final = new FinalLattice(lattice: final with { Gradients = false });
            group.Next = SlabOf(group: group);
            return;
        }
        group.Next = Lattice(group: group, longest: CoarseSamples, minimum: RefineMinimum, gradients: false);
    }
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
    // Answers a slab of a group's final lattice. Once every plane is answered, the corners of the cells the surface
    // crosses, the only samples the contour reads a gradient at, are sampled again with gradients.
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

        final.FindCorners();
        if (final.Corners.Length == 0) {
            group.Outcome = "no crossed cell in the final lattice";
            group.Final = null;
            group.Next = null;
            group.Done = true;
            group.Mesh = null;
            Finish();
            return;
        }
        group.Next = GradientChunk(group: group);
    }
    // The next chunk of a group's crossed-cell corners, as many as a frame's share of field evaluations takes.
    private static SdfMeshBakeRequest GradientChunk(Group group) {
        var final = group.Final!;
        var count = Math.Clamp(value: (FrameTaps / 5), min: 1, max: (final.Corners.Length - final.GradientsAnswered));

        return new SdfMeshBakeRequest(Cell: final.Lattice.Cell, Dims: (count, 1, 1), Gradients: true, Origin: Vector3.Zero, Selector: group.Selector) {
            Points = final.Points.AsMemory(length: count, start: final.GradientsAnswered),
        };
    }
    // Answers a chunk of a group's corner gradients; once every corner is answered, contours the group on the thread pool.
    private void AnswerGradients(Group group, in SdfMeshBakeRequest request, float[] gradients) {
        var final = group.Final!;

        for (var index = 0; (index < request.Samples); index++) {
            var sample = final.Corners[(final.GradientsAnswered + index)];

            final.Gradients[(3 * sample)] = gradients[(3 * index)];
            final.Gradients[((3 * sample) + 1)] = gradients[((3 * index) + 1)];
            final.Gradients[((3 * sample) + 2)] = gradients[((3 * index) + 2)];
        }
        final.GradientsAnswered += request.Samples;
        if (final.GradientsAnswered < final.Corners.Length) {
            group.Next = GradientChunk(group: group);
            return;
        }

        var lattice = final.Lattice with { Gradients = true };

        group.Final = null;
        group.Contouring = true;
        group.Next = null;
        _ = Task.Run(action: () => Contour(group: group, request: lattice, values: final.Values, gradients: final.Gradients, materials: final.Materials));
    }
    // Contours a group's final lattice, then queues the points its material is sampled at: four a quad, the centers of
    // its quarters, each a little outside the surface along the normal, as a field hit lies (MaterialPoints).
    private void Contour(Group group, SdfMeshBakeRequest request, float[] values, float[] gradients, int[] materials) {
        SdfContour? contour = null;

        try {
            contour = new SdfSampledLattice(
                cell: request.Cell,
                gradients: gradients,
                materials: materials,
                origin: request.Origin,
                samplesX: request.Dims.X,
                samplesY: request.Dims.Y,
                samplesZ: request.Dims.Z,
                values: values
            ).Contour(owns: ((group.Materials.Count > 0) ? group.Materials.Contains : null));
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
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
            group.Points = MaterialPoints(contour: contour);
            group.PointMaterials = new int[group.Points.Length];
            group.PointsAnswered = 0;
            group.Next = PointChunk(group: group);
        }
    }
    // The next chunk of a group's material points, at most a frame's share of field evaluations.
    private static SdfMeshBakeRequest PointChunk(Group group) {
        var count = Math.Min(val1: (group.Points!.Length - group.PointsAnswered), val2: FrameTaps);

        return new SdfMeshBakeRequest(Cell: Vector3.One, Dims: (count, 1, 1), Gradients: false, Origin: Vector3.Zero, Selector: group.Selector) {
            Points = group.Points.AsMemory(length: count, start: group.PointsAnswered),
        };
    }
    // Answers a chunk of a group's material points; once every point is answered, meshes the group on the thread pool.
    private void AnswerPoints(Group group, in SdfMeshBakeRequest request, int[] materials) {
        materials.AsSpan(length: request.Samples, start: 0).CopyTo(destination: group.PointMaterials.AsSpan(start: group.PointsAnswered));
        group.PointsAnswered += request.Samples;
        if (group.PointsAnswered < group.Points!.Length) {
            group.Next = PointChunk(group: group);
            return;
        }

        group.Next = null;
        group.Contouring = true;
        _ = Task.Run(action: () => Build(group: group));
    }
    // Builds a group's mesh from its contour and its points' materials.
    private void Build(Group group) {
        SdfMesh? mesh = null;

        try {
            mesh = MeshOf(contour: group.Contour!, materials: group.PointMaterials, owns: group.Materials);
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} refused: {exception.Message}");
        }

        lock (m_gate) {
            group.Contouring = false;
            group.Done = true;
            group.Mesh = mesh;
            group.Outcome = ((mesh is null) ? "mesh refused" : null);
            group.Contour = null;
            group.Points = null;
            group.PointMaterials = [];
            Finish();
        }
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
        var triangles = m_groups.Sum(selector: static group => (long)(group.Mesh?.TriangleCount ?? 0));
        var vertices = m_groups.Sum(selector: static group => (long)(group.Mesh?.Positions.Length ?? 0));

        // The groups that finished with no mesh, each with why: a group the bake drops renders nothing.
        var empty = string.Join(separator: "; ", values: m_groups.Where(predicate: static group => (group.Mesh is null)).Select(selector: static group => $"{group.Selector}: {group.Outcome}"));

        return $"[sdf.mesh-bake] done groups={m_groups.Count} meshes={meshes} dispatches={m_dispatches} samples={m_samples} taps={m_taps} triangles={triangles} vertices={vertices} seconds={m_seconds:0.000}" + ((empty.Length > 0) ? $" empty=[{empty}]" : string.Empty);
    }

    // The offset, in metres, outside the surface a material point lies, where a field hit would be accepted.
    private const float MaterialPointLift = 0.0005f;
    // The bilinear coordinates of a quad's quarter centers, in the order MeshOf reads their materials: a quad's corners
    // (a, b, c, d) sit at (0, 0), (1, 0), (1, 1) and (0, 1).
    private static readonly Vector2[] Quarters = [new(x: 0.25f, y: 0.25f), new(x: 0.75f, y: 0.25f), new(x: 0.75f, y: 0.75f), new(x: 0.25f, y: 0.75f)];

    private static Vector3 VertexOf(float[] values, int vertex) => new(x: values[(3 * vertex)], y: values[((3 * vertex) + 1)], z: values[((3 * vertex) + 2)]);
    private static Vector3 Bilinear(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 at) =>
        Vector3.Lerp(value1: Vector3.Lerp(value1: a, value2: b, amount: at.X), value2: Vector3.Lerp(value1: d, value2: c, amount: at.X), amount: at.Y);
    // Four points a quad, at its quarters' centers, lifted along the interpolated normal, in the frame its group bakes in.
    private static Vector4[] MaterialPoints(SdfContour contour) {
        var points = new Vector4[(4 * contour.QuadCount)];

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var corners = contour.Quads.AsSpan(length: 4, start: (4 * quad));

            for (var quarter = 0; (quarter < 4); quarter++) {
                var at = Quarters[quarter];
                var position = Bilinear(a: VertexOf(values: contour.Positions, vertex: corners[0]), b: VertexOf(values: contour.Positions, vertex: corners[1]), c: VertexOf(values: contour.Positions, vertex: corners[2]), d: VertexOf(values: contour.Positions, vertex: corners[3]), at: at);
                var normal = Bilinear(a: VertexOf(values: contour.Normals, vertex: corners[0]), b: VertexOf(values: contour.Normals, vertex: corners[1]), c: VertexOf(values: contour.Normals, vertex: corners[2]), d: VertexOf(values: contour.Normals, vertex: corners[3]), at: at);
                var length = normal.Length();

                points[((4 * quad) + quarter)] = new Vector4(value: (position + ((length > 0f) ? (normal * (MaterialPointLift / length)) : Vector3.Zero)), w: 1f);
            }
        }

        return points;
    }

    // A contour as a mesh, in the frame its group baked in. A quad
    // whose quarters all report one material stays one quad of that material; any other splits into its four quarters,
    // each its own material, so a material boundary is resolved at half a cell. A quarter whose material the group owns
    // no shape of takes its quad's contour material.
    private static SdfMesh? MeshOf(SdfContour contour, int[] materials, HashSet<int> owns) {
        if (contour.QuadCount == 0) { return null; }

        var positions = new List<Vector3>(capacity: contour.VertexCount);
        var normals = new List<Vector3>(capacity: contour.VertexCount);
        var indices = new List<uint>(capacity: (6 * contour.QuadCount));
        var triangleMaterials = new List<uint>(capacity: (2 * contour.QuadCount));

        uint Add(Vector3 point, Vector3 normal) {
            positions.Add(item: point);
            normals.Add(item: normal);

            return (uint)(positions.Count - 1);
        }
        void Quad(uint a, uint b, uint c, uint d, int material) {
            indices.Add(item: a); indices.Add(item: b); indices.Add(item: c);
            indices.Add(item: a); indices.Add(item: c); indices.Add(item: d);
            triangleMaterials.Add(item: (uint)Math.Max(val1: 0, val2: material));
            triangleMaterials.Add(item: (uint)Math.Max(val1: 0, val2: material));
        }

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            _ = Add(normal: VertexOf(values: contour.Normals, vertex: vertex), point: VertexOf(values: contour.Positions, vertex: vertex));
        }

        Span<int> quarter = stackalloc int[4];

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var corners = contour.Quads.AsSpan(length: 4, start: (4 * quad));
            var own = contour.QuadMaterials[quad];

            for (var index = 0; (index < 4); index++) {
                var material = materials[((4 * quad) + index)];

                quarter[index] = (((owns.Count == 0) || owns.Contains(item: material)) ? material : own);
            }

            if ((quarter[0] == quarter[1]) && (quarter[0] == quarter[2]) && (quarter[0] == quarter[3])) {
                Quad(a: (uint)corners[0], b: (uint)corners[1], c: (uint)corners[2], d: (uint)corners[3], material: quarter[0]);
                continue;
            }

            var p0 = VertexOf(values: contour.Positions, vertex: corners[0]);
            var p1 = VertexOf(values: contour.Positions, vertex: corners[1]);
            var p2 = VertexOf(values: contour.Positions, vertex: corners[2]);
            var p3 = VertexOf(values: contour.Positions, vertex: corners[3]);
            var n0 = VertexOf(values: contour.Normals, vertex: corners[0]);
            var n1 = VertexOf(values: contour.Normals, vertex: corners[1]);
            var n2 = VertexOf(values: contour.Normals, vertex: corners[2]);
            var n3 = VertexOf(values: contour.Normals, vertex: corners[3]);

            uint At(Vector2 at) {
                var normal = Bilinear(a: n0, b: n1, c: n2, d: n3, at: at);
                var length = normal.Length();

                return Add(normal: ((length > 0f) ? (normal / length) : normal), point: Bilinear(a: p0, b: p1, c: p2, d: p3, at: at));
            }

            var ab = At(at: new Vector2(x: 0.5f, y: 0f));
            var bc = At(at: new Vector2(x: 1f, y: 0.5f));
            var cd = At(at: new Vector2(x: 0.5f, y: 1f));
            var da = At(at: new Vector2(x: 0f, y: 0.5f));
            var center = At(at: new Vector2(x: 0.5f, y: 0.5f));

            Quad(a: (uint)corners[0], b: ab, c: center, d: da, material: quarter[0]);
            Quad(a: ab, b: (uint)corners[1], c: bc, d: center, material: quarter[1]);
            Quad(a: center, b: bc, c: (uint)corners[2], d: cd, material: quarter[2]);
            Quad(a: da, b: center, c: cd, d: (uint)corners[3], material: quarter[3]);
        }

        return new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray(), triangleMaterials: triangleMaterials.ToArray());
    }


    // One bake group: the instances it selects, the frame its mesh is stored in, its box, and where its bake stands.
    private sealed class Group(int selector, bool dynamic) {
        public int Selector { get; } = selector;
        public bool Dynamic { get; } = dynamic;
        public int Slot => Selector;
        public int Key { get; set; }
        // The materials the group's shapes carry.
        public HashSet<int> Materials { get; } = [];
        public Vector3 Low { get; set; }
        public Vector3 High { get; set; }
        public SdfMeshBakeRequest? Next { get; set; }
        public int Refinements { get; set; }
        public bool InFlight { get; set; }
        public bool Contouring { get; set; }
        public bool Done { get; set; }
        public SdfMesh? Mesh { get; set; }
        // The final lattice's contour, the points its material is sampled at, and their answers so far.
        public SdfContour? Contour { get; set; }
        public Vector4[]? Points { get; set; }
        public int[] PointMaterials { get; set; } = [];
        public int PointsAnswered { get; set; }
        public Vector3 DrawnPosition { get; set; }
        public Quaternion DrawnOrientation { get; set; } = Quaternion.Identity;
        // The final lattice while its slabs and corner gradients are sampled.
        public FinalLattice? Final { get; set; }
        // Why a finished group has no mesh, or null.
        public string? Outcome { get; set; }
    }
    // A group's final lattice: its values and materials, sampled a slab at a time, then the corners of the cells whose
    // corners disagree in sign, the samples a contour reads a gradient at, and their gradients.
    private sealed class FinalLattice(SdfMeshBakeRequest lattice) {
        public SdfMeshBakeRequest Lattice { get; } = lattice;
        public float[] Values { get; } = new float[lattice.Samples];
        public int[] Materials { get; } = new int[lattice.Samples];
        public float[] Gradients { get; } = new float[(3 * lattice.Samples)];
        public int Planes { get; set; }
        public int[] Corners { get; private set; } = [];
        public Vector4[] Points { get; private set; } = [];
        public int GradientsAnswered { get; set; }

        // Finds every corner of a cell the surface crosses.
        public void FindCorners() {
            var (sx, sy, sz) = Lattice.Dims;
            var marked = new bool[Values.Length];
            var corners = new List<int>();

            int Index(int x, int y, int z) => (x + (sx * (y + (sy * z))));

            for (var z = 0; (z < (sz - 1)); z++) {
                for (var y = 0; (y < (sy - 1)); y++) {
                    for (var x = 0; (x < (sx - 1)); x++) {
                        var inside = 0;

                        for (var corner = 0; (corner < 8); corner++) {
                            if (Values[Index(x: (x + (corner & 1)), y: (y + ((corner >> 1) & 1)), z: (z + ((corner >> 2) & 1)))] < 0f) { inside++; }
                        }
                        if ((inside == 0) || (inside == 8)) { continue; }
                        // The cell's corners, and those of the cells past it along each axis: a vertex clamped to the
                        // cell's far face, or a crossing at an edge's far end, reads its normal in the next cell.
                        for (var dz = 0; (dz < 3) && ((z + dz) < sz); dz++) {
                            for (var dy = 0; (dy < 3) && ((y + dy) < sy); dy++) {
                                for (var dx = 0; (dx < 3) && ((x + dx) < sx); dx++) {
                                    var index = Index(x: (x + dx), y: (y + dy), z: (z + dz));

                                    if (!marked[index]) {
                                        marked[index] = true;
                                        corners.Add(item: index);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Corners = [.. corners];
            Points = new Vector4[Corners.Length];
            for (var corner = 0; (corner < Corners.Length); corner++) {
                var index = Corners[corner];
                var at = new Vector3(x: (index % sx), y: ((index / sx) % sy), z: (index / (sx * sy)));

                Points[corner] = new Vector4(value: (Lattice.Origin + (at * Lattice.Cell)), w: 1f);
            }
        }
    }
}
