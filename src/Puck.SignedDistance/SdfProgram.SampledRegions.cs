using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One immutable voxel snapshot of a sampled field (<see cref="SdfSampledField"/>): distances X fastest, then Y,
/// then Z, every one finite. A snapshot never changes; a field moves by publishing a new one.</summary>
public sealed class SdfSampledSnapshot {
    private readonly float[] m_values;

    internal SdfSampledSnapshot(float[] values) => m_values = values;

    /// <summary>Gets the voxel distances.</summary>
    public ReadOnlySpan<float> Values => m_values;
    /// <summary>Gets the voxel count.</summary>
    public int Count => m_values.Length;
}

/// <summary>
/// A sampled field's live voxel values: a cell holding the field's current immutable snapshot
/// (<see cref="SdfSampledSnapshot"/>), whose lattice dimensions are fixed for its life. A program's sampled instruction
/// names its field by a handle (<see cref="SdfProgram.SampledFields"/>), never its values, so publishing new values
/// changes no program word: the frame carries the snapshots (<see cref="SdfSampledSet"/>), the mesh identity hashes them,
/// and the bake samples exactly the snapshot a group hashed. A field whose dimensions change is a new field, and so a new
/// program.
/// </summary>
public sealed class SdfSampledField {
    /// <summary>The scale a producer stores a distance at, 1/√3: the trilinear interpolant of distances so scaled is
    /// 1-Lipschitz (KEEP IN SYNC with <c>sdfSampledRegion</c>).</summary>
    public const float DistanceScale = 0.57735026f;

    private SdfSampledSnapshot m_current;

    /// <summary>Initializes a new instance of the <see cref="SdfSampledField"/> class with its first values.</summary>
    /// <param name="dimX">Voxels along X, in [1, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/>].</param>
    /// <param name="dimY">Voxels along Y, in [1, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/>].</param>
    /// <param name="dimZ">Voxels along Z, in [1, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/>].</param>
    /// <param name="values">The first voxel distances, X fastest.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> holds another count of voxels, or a value that is
    /// not finite.</exception>
    public SdfSampledField(int dimX, int dimY, int dimZ, ReadOnlySpan<float> values) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: dimX, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: dimY, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: dimZ, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: dimX, other: SdfProgramBuilder.MaxSampledRegionDim);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: dimY, other: SdfProgramBuilder.MaxSampledRegionDim);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: dimZ, other: SdfProgramBuilder.MaxSampledRegionDim);

        DimX = dimX;
        DimY = dimY;
        DimZ = dimZ;
        Count = checked((dimX * dimY) * dimZ);
        m_current = Snapshot(values: values);
    }

    /// <summary>Gets the voxels along X.</summary>
    public int DimX { get; }
    /// <summary>Gets the voxels along Y.</summary>
    public int DimY { get; }
    /// <summary>Gets the voxels along Z.</summary>
    public int DimZ { get; }
    /// <summary>Gets the voxel count every snapshot holds.</summary>
    public int Count { get; }
    /// <summary>Gets the dimensions as the sampled instruction packs them (<see cref="SdfProgramBuilder.SampledRegionDimMask"/>
    /// ten bits each).</summary>
    public uint PackedDims => ((((uint)DimX) | (((uint)DimY) << 10)) | (((uint)DimZ) << 20));

    /// <summary>Gets the field's current snapshot. Any thread may read it; the publisher replaces it whole.</summary>
    public SdfSampledSnapshot Current => Volatile.Read(location: ref m_current);

    /// <summary>Publishes new voxel values as the field's current snapshot. No program word changes.</summary>
    /// <param name="values">The voxel distances, X fastest.</param>
    /// <returns>The published snapshot.</returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> holds another count of voxels, or a value that is
    /// not finite.</exception>
    public SdfSampledSnapshot Publish(ReadOnlySpan<float> values) {
        var snapshot = Snapshot(values: values);

        Volatile.Write(location: ref m_current, value: snapshot);
        return snapshot;
    }

    private SdfSampledSnapshot Snapshot(ReadOnlySpan<float> values) {
        if (values.Length != Count) {
            throw new ArgumentException(message: $"A {DimX}x{DimY}x{DimZ} sampled field takes {Count} voxels, not {values.Length}.", paramName: nameof(values));
        }
        foreach (var value in values) {
            if (!float.IsFinite(f: value)) { throw new ArgumentException(message: "A sampled field's voxel distances must be finite.", paramName: nameof(values)); }
        }
        return new SdfSampledSnapshot(values: values.ToArray());
    }
}

/// <summary>The snapshots of a program's sampled fields at one moment, by handle (<see cref="SdfProgram.SampledFields"/>):
/// what a frame's sampled instructions read. Immutable; a field publishing new values makes a new set
/// (<see cref="Capture"/>).</summary>
public sealed class SdfSampledSet {
    private readonly SdfSampledSnapshot[] m_snapshots;

    private SdfSampledSet(SdfSampledSnapshot[] snapshots) => m_snapshots = snapshots;

    /// <summary>Gets the set of a program with no sampled field.</summary>
    public static SdfSampledSet Empty { get; } = new(snapshots: []);

    /// <summary>Gets the number of handles.</summary>
    public int Count => m_snapshots.Length;
    /// <summary>Gets a handle's snapshot.</summary>
    /// <param name="handle">The handle.</param>
    public SdfSampledSnapshot this[int handle] => m_snapshots[handle];

    /// <summary>Captures each field's current snapshot. When every one is the snapshot <paramref name="previous"/> holds
    /// at its handle, answers <paramref name="previous"/> itself, so a frame whose fields stood still carries the set the
    /// frame before carried, and a consumer compares sets by reference.</summary>
    /// <param name="fields">The fields, by handle.</param>
    /// <param name="previous">The set captured before, or <see langword="null"/>.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    public static SdfSampledSet Capture(IReadOnlyList<SdfSampledField> fields, SdfSampledSet? previous) {
        ArgumentNullException.ThrowIfNull(argument: fields);

        if (fields.Count == 0) { return Empty; }
        if ((previous is not null) && (previous.Count == fields.Count)) {
            var same = true;

            for (var handle = 0; (same && (handle < fields.Count)); handle++) {
                same = ReferenceEquals(objA: previous.m_snapshots[handle], objB: fields[handle].Current);
            }
            if (same) { return previous; }
        }

        var snapshots = new SdfSampledSnapshot[fields.Count];

        for (var handle = 0; (handle < snapshots.Length); handle++) { snapshots[handle] = fields[handle].Current; }
        return new SdfSampledSet(snapshots: snapshots);
    }
}

/// <summary>A sampled instruction's field: the instruction a builder emitted and the field it samples.</summary>
/// <param name="InstructionIndex">The sampled instruction.</param>
/// <param name="Field">The field.</param>
public sealed record SdfSampledBinding(int InstructionIndex, SdfSampledField Field);

/// <summary>A height field a program's instance draws (<see cref="SdfInstanceRange.HeightField"/>): the instance, and the
/// handle of the field's heights (<see cref="SdfHeightField.Heights"/>) in <see cref="SdfProgram.SampledFields"/>, which a
/// draw of the instance's baked reference names to be displaced by as it is drawn.</summary>
/// <param name="Instance">The instance's place in <see cref="SdfProgram.Instances"/>.</param>
/// <param name="Handle">The heights' handle.</param>
/// <param name="Field">The height field.</param>
public sealed record SdfHeightFieldBinding(int Instance, int Handle, SdfHeightField Field);

public sealed partial class SdfProgram {
    // Invariant: no sampled value enters the packed words. A sampled instruction's Data1.z holds its field's handle, an
    // index into m_sampledFields; the values travel beside the program (SdfSampledSet), so publishing a field's values
    // moves no word, analysis or upload of the program.
    private readonly SdfSampledBinding[] m_sampledBindings;
    private SdfSampledField[] m_sampledFields = [];

    /// <summary>Gets the program's sampled fields, by handle: a sampled instruction's Data1.z holds its field's place here
    /// as uint bits.</summary>
    public IReadOnlyList<SdfSampledField> SampledFields => m_sampledFields;
    /// <summary>Gets the height fields the program's instances draw, in instance order: a field's reference is the
    /// instance's sampled instruction and its heights hold a handle of their own, which no instruction samples and the
    /// mesh identity never hashes.</summary>
    public IReadOnlyList<SdfHeightFieldBinding> HeightFields { get; private set; } = [];
    /// <summary>Gets the snapshots the program's fields held when it was built, by handle: the set a frame of the program
    /// carries unless its producer captured a newer one (<see cref="SdfSampledSet.Capture"/>).</summary>
    public SdfSampledSet Sampled { get; private set; } = SdfSampledSet.Empty;

    // Validates the bindings and writes each sampled instruction's handle: one binding per sampled instruction, each
    // naming a field of the instruction's own dimensions, handles in first-binding order; then gives each height field's
    // heights a handle after them.
    private void BindSampledFields() {
        var bound = new HashSet<int>();
        var handles = new Dictionary<SdfSampledField, int>(comparer: ReferenceEqualityComparer.Instance);
        var fields = new List<SdfSampledField>();

        foreach (var (index, field) in m_sampledBindings) {
            ArgumentNullException.ThrowIfNull(argument: field);
            if ((index < 0) || (index >= m_instructions.Length) || !bound.Add(item: index)) {
                throw new ArgumentException(message: "A sampled binding must name one unique instruction.");
            }
            var instruction = m_instructions[index];

            if ((instruction.Op != SdfOp.ShapeBlend) || ((SdfShapeType)instruction.Shape != SdfShapeType.SampledRegion) ||
                (BitConverter.SingleToUInt32Bits(value: instruction.Data1.Y) != field.PackedDims)) {
                throw new ArgumentException(message: "A sampled binding must name a sampled instruction of its field's dimensions.");
            }
            if (!handles.TryGetValue(key: field, value: out var handle)) {
                handle = fields.Count;
                handles.Add(key: field, value: handle);
                fields.Add(item: field);
            }
            m_instructions[index] = instruction with {
                Data1 = instruction.Data1 with { Z = BitConverter.UInt32BitsToSingle(value: (uint)handle) },
            };
        }
        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            if ((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape == SdfShapeType.SampledRegion) && !bound.Contains(item: index)) {
                throw new ArgumentException(message: "A sampled instruction requires its field.");
            }
        }
        var heightFields = new List<SdfHeightFieldBinding>();

        for (var index = 0; (index < m_instances.Length); index++) {
            if (m_instances[index].HeightField is not { } heightField) { continue; }
            if (m_instances[index].IsDynamic) { throw new ArgumentException(message: "A height field is drawn by a static instance."); }
            if (handles.ContainsKey(key: heightField.Heights)) { throw new ArgumentException(message: "A height field's heights are drawn by one instance, and sampled by no instruction."); }
            handles.Add(key: heightField.Heights, value: fields.Count);
            heightFields.Add(item: new SdfHeightFieldBinding(Field: heightField, Handle: fields.Count, Instance: index));
            fields.Add(item: heightField.Heights);
        }
        m_sampledFields = [.. fields];
        HeightFields = [.. heightFields];
        Sampled = SdfSampledSet.Capture(fields: m_sampledFields, previous: null);
    }
}

public sealed partial class SdfProgramBuilder {
    private readonly List<SdfSampledBinding> m_sampledBindings = [];

    /// <summary>Emits a sampled field (<see cref="SdfShapeType.SampledRegion"/>): a cubic-voxel lattice of
    /// <paramref name="field"/>'s dimensions over a box from <paramref name="boxMin"/>, sampled by manual trilinear
    /// interpolation. The program names the field by a handle; its values are the field's (<see cref="SdfSampledField"/>),
    /// published beside the program, so a value change rebuilds no program and the mesh identity still covers it. The
    /// stored distances are pre-scaled 1/λ (λ = √3), which makes the interpolant 1-Lipschitz with no step clamp, and
    /// <paramref name="boundaryFloor"/> (margin/λ) is the outside-box lower-bound offset. The lane packing
    /// (Data0 = boxMin.xyz + cellSize; Data1 = smooth + packedDims + handle + boundaryFloor) is KEEP IN SYNC with
    /// sdfSampledRegion in Assets/Shaders/Sdf/field/sdf-shapes.hlsli.</summary>
    /// <param name="boxMin">The minimum corner.</param>
    /// <param name="cellSize">The voxel edge; finite and greater than zero.</param>
    /// <param name="field">The field the instruction samples.</param>
    /// <param name="boundaryFloor">The outside-box distance margin; finite and non-negative.</param>
    /// <param name="material">The surface material.</param>
    /// <param name="blend">The field composition.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boxMin"/> is not finite, <paramref name="cellSize"/>
    /// is not positive and finite, <paramref name="boundaryFloor"/> is negative or not finite, or the box's extent is not
    /// finite.</exception>
    public SdfProgramBuilder SampledValues(Vector3 boxMin, float cellSize, SdfSampledField field, float boundaryFloor, int material, SdfBlendOp blend = SdfBlendOp.Union) {
        ArgumentNullException.ThrowIfNull(argument: field);

        SampledRegion(blend: blend, boundaryFloor: boundaryFloor, boxMin: boxMin, cellSize: cellSize, dimX: field.DimX, dimY: field.DimY, dimZ: field.DimZ, material: material);
        m_sampledBindings.Add(item: new SdfSampledBinding(Field: field, InstructionIndex: (m_instructions.Count - 1)));
        return this;
    }
}
