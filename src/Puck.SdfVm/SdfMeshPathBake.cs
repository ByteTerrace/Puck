using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>One dispatch of the mesh bake's field sampler: the lattice it samples and the instances it selects.</summary>
/// <param name="Origin">The world position of the lattice's first sample.</param>
/// <param name="Cell">The spacing between samples along each axis, in world units.</param>
/// <param name="Dims">The samples along each axis.</param>
/// <param name="Selector">A dynamic-transform slot whose instances the bake samples when non-negative, the one instance
/// <c>-(Selector + 1)</c> otherwise.</param>
/// <param name="Gradients">Whether the sampler measures the field's gradient at each sample.</param>
public readonly record struct SdfMeshBakeRequest(Vector3 Origin, Vector3 Cell, (int X, int Y, int Z) Dims, int Selector, bool Gradients) {
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => ((Dims.X * Dims.Y) * Dims.Z);
}
/// <summary>The lattices one bake dispatch samples, at most <see cref="SdfWorldPackage.MeshBakeBatch"/> holding at most
/// <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples together, and the group each answers.</summary>
/// <param name="Lattices">The lattices, in dispatch order.</param>
/// <param name="Tickets">The group each lattice answers.</param>
public sealed record SdfMeshBakeBatch(SdfMeshBakeRequest[] Lattices, object[] Tickets) {
    /// <summary>Gets the samples the dispatch writes.</summary>
    public int Samples => Lattices.Sum(selector: static lattice => lattice.Samples);
}
/// <summary>
/// The baked-mesh path's bake of one residency's program: a mesh per bake group, sampled from the program on the GPU and
/// contoured on the CPU, drawn in the frame's mesh draws while a view of the residency takes the mesh path.
/// <para>A group is the instances one rigid frame moves: every active instance riding one dynamic-transform slot, or one
/// static instance. Each group's field is sampled through the frame's own interpreter (<c>sdf-mesh-bake.comp.hlsl</c>)
/// with only its instances in the walk's mask, one lattice a frame across the groups: a coarse lattice over the group's
/// bounding sphere, refinements over the box of the cells the surface can cross until the box stops shrinking, then a
/// final lattice of at most 65 samples a side whose spacing follows the box's own proportions, with gradients. The final
/// samples contour on the thread pool (<see cref="SdfSampledLattice"/>). A dynamic group's vertices are stored in its
/// slot's frame at bake time, so its draw follows the slot's live transform; a static group's are in world space.</para>
/// <para>A new program restarts the bake; the meshes of the last finished bake keep drawing until each group's new mesh
/// lands. The bake reports its counted work and the wall time it took (<see cref="Describe"/>).</para>
/// </summary>
public sealed class SdfMeshPathBake {
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

                    m_draws.Add(item: new SdfMeshDraw(Identity: group, Material: 0, Mesh: mesh, ObjectToWorld: matrix) { IsDynamic = group.Dynamic });
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
                m_frameBatch = null;

                var lattices = new List<SdfMeshBakeRequest>();
                var tickets = new List<object>();
                var samples = 0;
                var start = m_cursor;

                for (var step = 0; (step < m_groups.Count) && (lattices.Count < SdfWorldPackage.MeshBakeBatch); step++) {
                    var index = ((start + step) % m_groups.Count);
                    var group = m_groups[index];

                    if (group.Done || group.InFlight || group.Contouring || (group.Next is not { } next)) { continue; }
                    if ((samples + next.Samples) > SdfWorldPackage.MeshBakeMaxSamples) { break; }
                    group.InFlight = true;
                    m_cursor = ((index + 1) % m_groups.Count);
                    m_dispatches++;
                    m_samples += next.Samples;
                    samples += next.Samples;
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
            if (request.Gradients) {
                var lattice = request;

                group.Contouring = true;
                group.Next = null;
                _ = Task.Run(action: () => Contour(group: group, request: lattice, values: values, gradients: gradients!, materials: materials));
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

            return $"[sdf.mesh-bake] groups={m_groups.Count} meshes={meshes} pending={m_groups.Count(predicate: static group => !group.Done)} dispatches={m_dispatches} samples={m_samples} triangles={triangles} vertices={vertices} bytes={bytes} seconds={(m_reported ? m_seconds : m_clock.Elapsed.TotalSeconds):0.000}";
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
                var transform = ((instance.Slot < frame.DynamicTransforms.Count) ? frame.DynamicTransforms[instance.Slot] : default);
                var center = (transform.Position + instance.Center);

                if (!bySlot.TryGetValue(key: instance.Slot, value: out var group)) {
                    group = new Group(selector: instance.Slot, dynamic: true) {
                        BakeOrientation = ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation),
                        BakePosition = transform.Position,
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
                    BakeOrientation = Quaternion.Identity,
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
                BakeOrientation = Quaternion.Identity,
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

        group.Next = ((converged || (group.Refinements >= MaxRefinements))
            ? Lattice(group: group, longest: FinalSamples, minimum: FinalMinimum, gradients: true)
            : Lattice(group: group, longest: CoarseSamples, minimum: RefineMinimum, gradients: false));
    }
    // Contours a group's final lattice into its mesh, in the group's slot frame when it is dynamic.
    private void Contour(Group group, SdfMeshBakeRequest request, float[] values, float[] gradients, int[] materials) {
        SdfMesh? mesh = null;

        try {
            var contour = new SdfSampledLattice(
                cell: request.Cell,
                gradients: gradients,
                materials: materials,
                origin: request.Origin,
                samplesX: request.Dims.X,
                samplesY: request.Dims.Y,
                samplesZ: request.Dims.Z,
                values: values
            ).Contour();

            mesh = MeshOf(contour: contour, dynamic: group.Dynamic, orientation: group.BakeOrientation, position: group.BakePosition);        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {group.Selector} refused: {exception.Message}");
        }

        lock (m_gate) {
            group.Contouring = false;
            group.Done = true;
            group.Mesh = mesh;
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

        return $"[sdf.mesh-bake] done groups={m_groups.Count} meshes={meshes} dispatches={m_dispatches} samples={m_samples} triangles={triangles} vertices={vertices} seconds={m_seconds:0.000}";
    }

    // A contour as a mesh: two triangles a quad, each quad's material on both, positions and normals carried into the slot's
    // frame at bake time for a dynamic group.
    private static SdfMesh? MeshOf(SdfContour contour, bool dynamic, Quaternion orientation, Vector3 position) {
        if (contour.QuadCount == 0) { return null; }

        var inverse = Quaternion.Inverse(value: orientation);
        var positions = new Vector3[contour.VertexCount];
        var normals = new Vector3[contour.VertexCount];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            var point = new Vector3(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)]);
            var normal = new Vector3(x: contour.Normals[(3 * vertex)], y: contour.Normals[((3 * vertex) + 1)], z: contour.Normals[((3 * vertex) + 2)]);

            positions[vertex] = (dynamic ? Vector3.Transform(value: (point - position), rotation: inverse) : point);
            normals[vertex] = (dynamic ? Vector3.Transform(value: normal, rotation: inverse) : normal);
        }

        var indices = new uint[(6 * contour.QuadCount)];
        var triangleMaterials = new uint[(2 * contour.QuadCount)];

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var a = (uint)contour.Quads[(4 * quad)];
            var b = (uint)contour.Quads[((4 * quad) + 1)];
            var c = (uint)contour.Quads[((4 * quad) + 2)];
            var d = (uint)contour.Quads[((4 * quad) + 3)];
            var material = (uint)Math.Max(val1: 0, val2: contour.QuadMaterials[quad]);

            indices[(6 * quad)] = a;
            indices[((6 * quad) + 1)] = b;
            indices[((6 * quad) + 2)] = c;
            indices[((6 * quad) + 3)] = a;
            indices[((6 * quad) + 4)] = c;
            indices[((6 * quad) + 5)] = d;
            triangleMaterials[(2 * quad)] = material;
            triangleMaterials[((2 * quad) + 1)] = material;
        }

        return new SdfMesh(indices: indices, normals: normals, positions: positions, triangleMaterials: triangleMaterials);
    }

    // One bake group: the instances it selects, the frame its mesh is stored in, its box, and where its bake stands.
    private sealed class Group(int selector, bool dynamic) {
        public int Selector { get; } = selector;
        public bool Dynamic { get; } = dynamic;
        public int Slot => Selector;
        public int Key { get; set; }
        // The materials the group's shapes carry.
        public HashSet<int> Materials { get; } = [];
        public Vector3 BakePosition { get; init; }
        public Quaternion BakeOrientation { get; init; }
        public Vector3 Low { get; set; }
        public Vector3 High { get; set; }
        public SdfMeshBakeRequest? Next { get; set; }
        public int Refinements { get; set; }
        public bool InFlight { get; set; }
        public bool Contouring { get; set; }
        public bool Done { get; set; }
        public SdfMesh? Mesh { get; set; }
        public Vector3 DrawnPosition { get; set; }
        public Quaternion DrawnOrientation { get; set; } = Quaternion.Identity;
    }
}
