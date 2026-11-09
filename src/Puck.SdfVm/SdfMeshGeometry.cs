using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The geometry a baked mesh group was sampled from (<see cref="SdfMeshGeometryBuilder"/>): whether it bakes in a
/// dynamic slot's frame, and a 128-bit hash of what determines its surface there.</summary>
/// <param name="Dynamic">Whether the group bakes in its slot's frame rather than in world space.</param>
/// <param name="Hash">The hash of the group's geometry.</param>
public readonly record struct SdfMeshGeometry(bool Dynamic, UInt128 Hash);
/// <summary>
/// Hashes what determines a bake group's surface, independent of where its program and its frame place it, so one body
/// carried by two worlds' programs, or by one world's program before and after a rebuild, hashes alike
/// (<see cref="SdfMeshLibrary"/>):
/// <list type="bullet">
/// <item>each instruction's fields, a side-table entry's values (<see cref="SdfProgram.SideTableValues"/>) in place of the
/// offset its program packed the entry at, and a sampled instruction's snapshot (<see cref="SdfSampledSet"/>) in place of
/// its field's handle;</item>
/// <item>an instance's shapes' materials by their place among the group's materials (<see cref="Materials"/>), since a
/// program numbers its palette in the order its emitters register materials;</item>
/// <item>a static instance's bound, and a dynamic instance's bound radius, which clips what the bake samples. A dynamic
/// instance's bound center is offset along world axes by the slot's pose when the program was emitted;</item>
/// <item>a dynamic instance's transform into its own slot by that role rather than the slot's number, which each program
/// assigns;</item>
/// <item>a transform into any other slot by that slot's pose in the group's frame (its own slot's, in that slot's own
/// units (<see cref="DynamicTransform.Scale"/>), so a body drawn at another size keys its geometry alike; or world
/// space), rounded to <see cref="PositionSteps"/> and <see cref="OrientationSteps"/> (or the coarser steps <see cref="Snap"/>
/// sets), and, under a slot of the group's own, by
/// that slot's offset from it, which names the member a vertex of the mesh rides (<see cref="SdfMesh.Members"/>). The bake samples such an instruction
/// through that slot's transform in the frame, so the shapes of a stamp-pool blend group, each riding its own slot inside
/// the group's instance, take their places and turns from the frame's transforms rather than from the
/// instructions;</item>
/// <item>the lanes (<see cref="DynamicTransform.Lanes"/>) a lane erosion or a morph reads from the slot its chain
/// rides.</item>
/// </list>
/// The rounded poses and lanes it hashes are kept (<see cref="Poses"/>), as are the snapshots (<see cref="Sampled"/>), and
/// the bake samples the group through them rather than through the frame it dispatches in, so a mesh is a function of its
/// geometry alone.
/// </summary>
/// <param name="dynamic">Whether the group bakes in its slot's frame.</param>
public sealed class SdfMeshGeometryBuilder(bool dynamic) {
    /// <summary>The steps a metre a slot's position, and a lane's value, rounds to: a millimetre, finer than any baked
    /// cell, and coarse enough that one rest pose composed under two roots rounds alike.</summary>
    public const float PositionSteps = 1024f;
    /// <summary>The steps a unit a slot's orientation quaternion's components round to.</summary>
    public const float OrientationSteps = 16384f;

    // What a transform's slot lane hashes as: the group's own slot, or any other, whose pose is hashed beside it.
    private const float OwnSlot = -1f;
    private const float OtherSlot = -2f;
    // The bytes before each fact's payload: its kind, then the payload's length in bytes.
    private const int FactHeaderBytes = (sizeof(byte) + sizeof(int));
    // The most hashers sealed builders keep for later ones: every group of a plan takes two, and a program restart plans
    // thousands.
    private const int MaxSpareHashers = 8192;

    private static readonly System.Collections.Concurrent.ConcurrentQueue<XxHash128> SpareHashers = new();

    private static int SpareHasherCount;

    private XxHash128? m_hash = RentHasher();
    // Everything m_hash takes but the poses of other slots and the steps they round to.
    private XxHash128? m_form = RentHasher();

    // The geometry and form Seal fixed.
    private UInt128 m_sealedHash;
    private UInt128 m_sealedForm;

    private readonly List<int> m_materials = [];

    // Made by the first kept pose: most static groups keep none.
    private SortedDictionary<int, DynamicTransform>? m_poses;
    // Made by the first sampled instruction: each handle the group reads, with the snapshot its geometry hashed.
    private SortedDictionary<int, SdfSampledSnapshot>? m_sampled;
    private static readonly IReadOnlyDictionary<int, SdfSampledSnapshot> NoSampled = new Dictionary<int, SdfSampledSnapshot>();

    private static readonly IReadOnlyDictionary<int, DynamicTransform> NoPoses = new Dictionary<int, DynamicTransform>();
    // The slot the chain being appended rides, whose lanes a lane erosion reads; -1 under none.
    private int m_chainSlot = -1;
    // The steps another slot's pose rounds to: PositionSteps and OrientationSteps unless Snap coarsened them.
    private float m_positionSteps = PositionSteps;
    private float m_orientationSteps = OrientationSteps;

    /// <summary>Rounds every other slot's pose this builder appends to coarser steps than <see cref="PositionSteps"/> and
    /// <see cref="OrientationSteps"/>, so poses that settle near one another key one geometry and share its mesh
    /// (<see cref="SdfMeshPathBake"/>'s settle rebake). The steps join the hash, so a snapped geometry never keys alike
    /// with one rounded at the defaults. Call before the first append.</summary>
    /// <param name="positionSteps">The steps a metre a position rounds to; positive.</param>
    /// <param name="orientationSteps">The steps a unit an orientation quaternion's components round to; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">A step count is not positive.</exception>
    public void Snap(float positionSteps, float orientationSteps) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: positionSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: orientationSteps);

        m_positionSteps = positionSteps;
        m_orientationSteps = orientationSteps;

        var steps = new Vector2(x: positionSteps, y: orientationSteps);

        HashGeometry(fact: Fact.Snap, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector2>(reference: in steps)));
    }
    /// <summary>Fixes the geometry and form appended so far, which <see cref="Geometry"/> and <see cref="Form"/> then
    /// answer, and hands the hashers they were taken with to the builders made after it. Nothing is appended after it;
    /// a second call does nothing.</summary>
    public void Seal() {
        if ((m_hash is not { } hash) || (m_form is not { } form)) {
            return;
        }

        m_sealedHash = hash.GetCurrentHashAsUInt128();
        m_sealedForm = form.GetCurrentHashAsUInt128();
        m_hash = null;
        m_form = null;
        ReturnHasher(hasher: hash);
        ReturnHasher(hasher: form);
    }

    private static XxHash128 RentHasher() {
        if (SpareHashers.TryDequeue(result: out var hasher)) {
            _ = Interlocked.Decrement(location: ref SpareHasherCount);

            return hasher;
        }

        return new XxHash128();
    }
    private static void ReturnHasher(XxHash128 hasher) {
        if (Interlocked.Increment(location: ref SpareHasherCount) > MaxSpareHashers) {
            _ = Interlocked.Decrement(location: ref SpareHasherCount);

            return;
        }

        hasher.Reset();
        SpareHashers.Enqueue(item: hasher);
    }
    // The geometry's hasher, which only an unsealed builder holds.
    private XxHash128 Hashing() => (m_hash ?? throw new InvalidOperationException(message: "A sealed geometry takes no more appends."));
    // Appends one fact to a hasher as its kind, its payload's length and the payload: every append into the geometry
    // goes through here, so the stream is prefix-free and no two different sequences of facts hash alike (a reach whose
    // bits spell another fact's payload included).
    private static void AppendFact(XxHash128 hasher, Fact fact, ReadOnlySpan<byte> source) {
        Span<byte> header = stackalloc byte[FactHeaderBytes];

        header[0] = ((byte)fact);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination: header[1..], value: source.Length);
        hasher.Append(source: header);
        hasher.Append(source: source);
    }
    // Appends a fact to the geometry alone: another slot's pose and the steps it rounds to, which the form leaves out.
    private void HashGeometry(Fact fact, ReadOnlySpan<byte> source) => AppendFact(fact: fact, hasher: Hashing(), source: source);

    /// <summary>Gets the materials the appended instances' shapes carry, in the order the geometry numbers them.</summary>
    public IReadOnlyList<int> Materials => m_materials;
    /// <summary>Gets each slot the appended instructions read, with its pose in the group's frame and its lanes as the
    /// geometry hashed them, rounded: the group's own slot at the identity, any other slot where it stood in the frame the
    /// geometry was appended from. A slot the frame held no transform for is absent.</summary>
    public IReadOnlyDictionary<int, DynamicTransform> Poses => (((IReadOnlyDictionary<int, DynamicTransform>?)m_poses) ?? NoPoses);

    /// <summary>Writes the poses (<see cref="Poses"/>) into pose rows from a first slot, three rows a slot as the
    /// dynamic-transform table lays one out: position, orientation, lanes. A slot between two kept ones is left as the
    /// rows hold it.</summary>
    /// <param name="rows">The rows, from the first slot's first row.</param>
    /// <param name="first">The slot the rows start at, at most the lowest kept slot.</param>
    public void WritePoseRows(Span<Vector4> rows, int first) {
        if (m_poses is null) { return; }

        foreach (var (slot, pose) in m_poses) {
            var row = (3 * (slot - first));

            rows[row] = new Vector4(value: pose.Position, w: 0f);
            rows[(row + 1)] = new Vector4(x: pose.Orientation.X, y: pose.Orientation.Y, z: pose.Orientation.Z, w: pose.Orientation.W);
            rows[(row + 2)] = pose.Lanes;
        }
    }

    /// <summary>Gets each sampled field the appended instructions read, by its handle in the program
    /// (<see cref="SdfProgram.SampledFields"/>), with the snapshot the geometry hashed, which the bake samples the group
    /// through.</summary>
    public IReadOnlyDictionary<int, SdfSampledSnapshot> Sampled => (((IReadOnlyDictionary<int, SdfSampledSnapshot>?)m_sampled) ?? NoSampled);
    /// <summary>Gets whether an appended instruction samples a field, so the geometry depends on the frame's sampled
    /// set.</summary>
    public bool ReadsSampled => (m_sampled is not null);

    /// <summary>Returns whether every snapshot the geometry hashed is the one a set holds at its handle.</summary>
    /// <param name="sampled">The set.</param>
    /// <returns><see langword="true"/> when the geometry reads no field or reads the set's snapshots.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sampled"/> is <see langword="null"/>.</exception>
    public bool SampledMatches(SdfSampledSet sampled) {
        ArgumentNullException.ThrowIfNull(argument: sampled);

        if (m_sampled is null) { return true; }
        foreach (var (handle, snapshot) in m_sampled) {
            if ((handle >= sampled.Count) || !ReferenceEquals(objA: sampled[handle], objB: snapshot)) { return false; }
        }
        return true;
    }

    /// <summary>Gets the lowest and highest slot <see cref="Poses"/> holds, or <c>(-1, -1)</c> while it holds none.</summary>
    public (int First, int Last) PoseSlots => ((m_poses is null) ? (-1, -1) : (m_firstPose, m_lastPose));

    // The lowest and highest slot m_poses holds.
    private int m_firstPose = int.MaxValue;
    private int m_lastPose = int.MinValue;

    /// <summary>Gets the turn from world axes into the group's frame, as the frame the geometry was appended from held it:
    /// the inverse of its own slot's orientation, the identity for a group in world space. A dynamic bound's center is
    /// an offset along world axes from its slot, which the bake turns by it into the group's frame.</summary>
    public Quaternion BoundTurn { get; private set; } = Quaternion.Identity;

    /// <summary>Gets whether an appended instruction reads a dynamic slot, so the geometry depends on the frame's
    /// transforms.</summary>
    public bool ReadsSlots { get; private set; }
    /// <summary>Gets whether an appended shape samples a glyph. Such groups use a tighter crease and material-edge
    /// error budget at every distance to protect counters and thin strokes while simplifying face interiors.</summary>
    public bool ContainsGlyph { get; private set; }
    /// <summary>Gets the geometry of everything appended so far.</summary>
    public SdfMeshGeometry Geometry => new(Dynamic: dynamic, Hash: ((m_hash is { } hash) ? hash.GetCurrentHashAsUInt128() : m_sealedHash));
    /// <summary>Gets the form of everything appended so far: the geometry without the poses of the slots besides the
    /// group's own, or the steps they round to. Two groups of one form are one creation's shapes standing in different
    /// relative poses, whose skinned mesh carries each member through its own slot (<see cref="SdfMesh.Members"/>).</summary>
    public UInt128 Form => ((m_form is { } form) ? form.GetCurrentHashAsUInt128() : m_sealedForm);

    /// <summary>Appends one instance of a program: its instructions, each shape's material by its place among the
    /// group's materials, each other slot it reads by that slot's pose in the instance's frame, and its bound.</summary>
    /// <param name="program">The program.</param>
    /// <param name="instance">The instance's index in <see cref="SdfProgram.Instances"/>.</param>
    /// <param name="transforms">The frame's dynamic transforms, which the bake samples the instance through.</param>
    /// <param name="sampled">The frame's sampled set, whose snapshots the bake samples the instance's sampled fields
    /// through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/>, <paramref name="transforms"/> or
    /// <paramref name="sampled"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="instance"/> names no instance.</exception>
    public void AppendInstance(SdfProgram program, int instance, IReadOnlyList<DynamicTransform> transforms, SdfSampledSet sampled) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: sampled);

        var range = program.Instances[instance];
        var bound = (range.IsDynamic ? new Vector4(x: 0f, y: 0f, z: 0f, w: range.Radius) : new Vector4(value: range.Center, w: range.Radius));
        var count = (range.End - range.First);
        var ownSlot = (range.IsDynamic ? range.Slot : -1);

        Hash(fact: Fact.Bound, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(reference: in bound)));
        Hash(fact: Fact.Count, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in count)));
        // A declared detail refines the lattice, so the mesh depends on it.
        if (range.Detail > 0f) {
            var detail = range.Detail;

            Hash(fact: Fact.Detail, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(reference: in detail)));
        }
        // So does the box a static union refines for the instance.
        if (!range.IsDynamic && (range.Box is { } box)) {
            var low = box.Low;
            var high = box.High;

            Hash(fact: Fact.BoxLow, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(reference: in low)));
            Hash(fact: Fact.BoxHigh, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(reference: in high)));
        }
        // So do the parts a dynamic instance declares, each by its slot's offset from the instance's own so one creation's
        // shapes key alike on every body that wears it.
        foreach (var part in range.Parts) {
            var words = new PartWords(part: part, ownSlot: ownSlot);

            Hash(fact: Fact.Part, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<PartWords>(reference: in words)));
        }
        m_chainSlot = -1;
        // The bake carries each sample into the group's frame through its own slot, which is that frame.
        if (ownSlot >= 0) {
            Keep(orientationSteps: OrientationSteps, pose: Pose.Identity, positionSteps: PositionSteps, slot: ownSlot, transforms: transforms);
            if (ownSlot < transforms.Count) { BoundTurn = Quaternion.Inverse(value: PoseOf(transform: transforms[ownSlot]).Orientation); }
        }

        for (var index = range.First; (index < range.End); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op == SdfOp.ShapeBlend) {
                var material = ((int)instruction.Material);
                var place = m_materials.IndexOf(item: material);

                if (place < 0) {
                    place = m_materials.Count;
                    m_materials.Add(item: material);
                }
                instruction = (instruction with { Material = ((uint)place) });
            }
            Append(index: index, instruction: instruction, ownSlot: ownSlot, program: program, sampled: sampled, transforms: transforms);
        }
    }
    /// <summary>Appends the reach a group's box is clamped to: a group whose bound passes the frame's far distance, or the
    /// group of the segments no instance owns, bakes over a box that reach wide, so the same instructions under another
    /// reach mesh another surface.</summary>
    /// <param name="reach">The reach, in world units.</param>
    public void AppendReach(float reach) => Hash(fact: Fact.Reach, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(reference: in reach)));
    /// <summary>Appends that the group bakes as glass: its shapes all carry glass materials, so its mesh lands whole and
    /// uncollapsed (<see cref="SdfMeshBakeGroup.Glass"/>), and the same shapes opaque key another geometry. Which
    /// materials are glass is the one material fact beside their places that shapes a mesh.</summary>
    public void AppendGlass() => Hash(fact: Fact.Glass, source: []);
    /// <summary>Appends the tier the group bakes at (<see cref="SdfMeshTier"/>), which floors every spacing its refinement
    /// aims for, so each tier of a geometry is its own mesh and its own cache entry. Every sealed group appends one
    /// (<see cref="SdfMeshBakeGroup.Seal"/>).</summary>
    /// <param name="tier">The tier, from zero to <see cref="SdfMeshTier.Last"/>.</param>
    public void AppendTier(int tier) => Hash(fact: Fact.Tier, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in tier)));
    /// <summary>Appends one instruction of a program as it stands, its material included, with the world-space pose of a
    /// slot it reads.</summary>
    /// <param name="program">The program.</param>
    /// <param name="index">The instruction's index in <see cref="SdfProgram.Instructions"/>.</param>
    /// <param name="transforms">The frame's dynamic transforms, which the bake samples the instruction through.</param>
    /// <param name="sampled">The frame's sampled set, whose snapshot the bake samples a sampled instruction through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/>, <paramref name="transforms"/> or
    /// <paramref name="sampled"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> names no instruction.</exception>
    public void AppendInstruction(SdfProgram program, int index, IReadOnlyList<DynamicTransform> transforms, SdfSampledSet sampled) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: sampled);

        Append(index: index, instruction: program.Instructions[index], ownSlot: -1, program: program, sampled: sampled, transforms: transforms);
    }

    // Appends an instruction: a transform into the group's own slot by that role and into another slot with that slot's
    // pose in the group's frame, a lane erosion with the lanes it reads, a side-table entry's values for its offset, and a
    // sampled field's snapshot for its handle.
    private void Append(SdfProgram program, int index, SdfInstruction instruction, int ownSlot, IReadOnlyList<DynamicTransform> transforms, SdfSampledSet sampled) {
        ContainsGlyph |= ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Shape == ((uint)SdfShapeType.Glyph)));
        switch (instruction.Op) {
            case SdfOp.ResetPoint:
                m_chainSlot = -1;
                break;
            case SdfOp.TransformDynamic: {
                    var slot = ((int)instruction.Data0.X);

                    m_chainSlot = slot;
                    ReadsSlots = true;
                    if (slot == ownSlot) {
                        instruction = (instruction with { Data0 = (instruction.Data0 with { X = OwnSlot }) });
                        Keep(orientationSteps: OrientationSteps, pose: Pose.Identity, positionSteps: PositionSteps, slot: slot, transforms: transforms);
                    } else {
                        var pose = RelativePose(orientationSteps: m_orientationSteps, ownSlot: ownSlot, positionSteps: m_positionSteps, slot: slot, transforms: transforms);
                        var offset = ((ownSlot >= 0) ? (slot - ownSlot) : 0);

                        instruction = (instruction with { Data0 = (instruction.Data0 with { X = OtherSlot }) });
                        HashGeometry(fact: Fact.OtherPose, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Pose>(reference: in pose)));
                        Hash(fact: Fact.MemberOffset, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in offset)));
                        Keep(orientationSteps: m_orientationSteps, pose: pose, positionSteps: m_positionSteps, slot: slot, transforms: transforms);
                    }
                    break;
                }
            case SdfOp.LaneErode:
                AppendLanes(transforms: transforms);
                break;
            case SdfOp.PopField when (instruction.Blend == ((uint)SdfBlendOp.Morph)):
                AppendLanes(transforms: transforms);
                break;
            default:
                break;
        }

        if ((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape == SdfShapeType.SampledRegion)) {
            var handle = (int)BitConverter.SingleToUInt32Bits(value: instruction.Data1.Z);
            var snapshot = sampled[handle];
            var count = snapshot.Count;

            (m_sampled ??= [])[handle] = snapshot;
            instruction = instruction with { Data1 = instruction.Data1 with { Z = 0f } };
            Hash(fact: Fact.SampledCount, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in count)));
            Hash(fact: Fact.SampledValues, source: MemoryMarshal.AsBytes(span: snapshot.Values));
        }

        var table = (((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) is SdfShapeType.ConvexPolygon or SdfShapeType.Sweep or SdfShapeType.Path))
            ? program.SideTableValues(instructionIndex: index)
            : []);

        if (!table.IsEmpty) {
            instruction = instruction with { Data0 = instruction.Data0 with { X = 0f } };
            Hash(fact: Fact.SideTable, source: MemoryMarshal.AsBytes(span: table));
        }

        var words = new InstructionWords(instruction: instruction);

        Hash(fact: Fact.Instruction, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<InstructionWords>(reference: in words)));
    }
    // Appends a fact to the geometry and its form.
    private void Hash(Fact fact, ReadOnlySpan<byte> source) {
        AppendFact(fact: fact, hasher: Hashing(), source: source);
        AppendFact(fact: fact, hasher: m_form!, source: source);
    }
    // Appends the lanes of the slot the chain rides, which a lane erosion or a morph reads; zero under none.
    private void AppendLanes(IReadOnlyList<DynamicTransform> transforms) {
        var lanes = new Rounded4(steps: PositionSteps, value: (((m_chainSlot >= 0) && (m_chainSlot < transforms.Count)) ? transforms[m_chainSlot].Lanes : Vector4.Zero));

        Hash(fact: Fact.Lanes, source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Rounded4>(reference: in lanes)));
    }
    // Keeps a slot's rounded pose as the bake reads it: its position, its orientation, renormalized in double precision
    // so the same rounded components always turn alike, and its lanes rounded as AppendLanes rounds them; the steps are
    // the ones the pose was rounded to. A missing slot is left out.
    private void Keep(int slot, Pose pose, float positionSteps, float orientationSteps, IReadOnlyList<DynamicTransform> transforms) {
        if ((pose == Pose.Missing) || (slot < 0) || (slot >= transforms.Count) || (m_poses?.ContainsKey(key: slot) ?? false)) { return; }

        var turn = pose.Orientation.Value(steps: orientationSteps);
        var length = Math.Sqrt(d: (((((double)turn.X) * turn.X) + (((double)turn.Y) * turn.Y)) + ((((double)turn.Z) * turn.Z) + (((double)turn.W) * turn.W))));
        var orientation = ((length > 0d)
            ? new Quaternion(w: ((float)(turn.W / length)), x: ((float)(turn.X / length)), y: ((float)(turn.Y / length)), z: ((float)(turn.Z / length)))
            : Quaternion.Identity);
        var position = pose.Position.Value(steps: positionSteps);

        var lanes = new Rounded4(steps: PositionSteps, value: transforms[slot].Lanes);

        (m_poses ??= []).Add(key: slot, value: new DynamicTransform(Lanes: lanes.Value(steps: PositionSteps), Orientation: orientation, Position: new Vector3(x: position.X, y: position.Y, z: position.Z)));
        m_firstPose = Math.Min(val1: m_firstPose, val2: slot);
        m_lastPose = Math.Max(val1: m_lastPose, val2: slot);
    }
    // A slot's pose in the frame of the group's own slot (world space under none), in that slot's own units
    // (DynamicTransform.Scale), rounded: where a sample the bake carries into the group's frame lands in that slot's
    // frame. A slot the frame holds no transform for is Missing.
    private static Pose RelativePose(int ownSlot, int slot, float positionSteps, float orientationSteps, IReadOnlyList<DynamicTransform> transforms) {
        if ((slot < 0) || (slot >= transforms.Count) || (ownSlot >= transforms.Count)) { return Pose.Missing; }

        var (framePosition, frameOrientation) = ((ownSlot < 0) ? (Vector3.Zero, Quaternion.Identity) : PoseOf(transform: transforms[ownSlot]));
        var frameScale = ((ownSlot < 0) ? 1f : UnitsOf(transform: transforms[ownSlot]));
        var (position, orientation) = PoseOf(transform: transforms[slot]);
        var inverse = Quaternion.Inverse(value: frameOrientation);
        var relative = Quaternion.Normalize(value: (inverse * orientation));

        // q and -q turn alike.
        if (relative.W < 0f) { relative = Quaternion.Negate(value: relative); }

        return new Pose(
            Orientation: new Rounded4(steps: orientationSteps, value: new Vector4(w: relative.W, x: relative.X, y: relative.Y, z: relative.Z)),
            Position: new Rounded4(steps: positionSteps, value: new Vector4(value: (Vector3.Transform(rotation: inverse, value: (position - framePosition)) / frameScale), w: 0f))
        );
    }
    // A transform's position and orientation, an unset orientation read as the identity, as the bake reads it.
    internal static (Vector3 Position, Quaternion Orientation) PoseOf(DynamicTransform transform) =>
        (transform.Position, ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation));
    /// <summary>Returns the world units one unit of a slot's own geometry spans (<see cref="DynamicTransform.Scale"/>),
    /// one for a scale that is not finite and positive: what the bake divides another slot's offset from a group's slot
    /// by, and what the group's draws scale its mesh by.</summary>
    /// <param name="transform">The slot's transform.</param>
    /// <returns>The scale.</returns>
    public static float UnitsOf(DynamicTransform transform) => ((float.IsFinite(f: transform.Scale) && (transform.Scale > 0f)) ? transform.Scale : 1f);

    // What each fact appended to the geometry hash is; its tag leads its payload (AppendFact).
    private enum Fact : byte {
        Snap = 1,
        Bound,
        Count,
        Detail,
        BoxLow,
        BoxHigh,
        Reach,
        Glass,
        OtherPose,
        MemberOffset,
        Lanes,
        SampledCount,
        SampledValues,
        SideTable,
        Instruction,
        Part,
        Tier,
    }
    // A declared part's fields as the bytes the geometry hashes: its slot as an offset from the instance's own.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PartWords(SdfInstancePart part, int ownSlot) {
        private readonly int m_slot = (part.Slot - ownSlot);
        private readonly Vector3 m_low = part.Low;
        private readonly Vector3 m_high = part.High;
        private readonly float m_detail = part.Detail;
    }
    // A vector's components rounded to steps a unit; a component no int holds rounds to int.MinValue.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rounded4(Vector4 value, float steps) {
        private readonly int m_x = Round(steps: steps, value: value.X);
        private readonly int m_y = Round(steps: steps, value: value.Y);
        private readonly int m_z = Round(steps: steps, value: value.Z);
        private readonly int m_w = Round(steps: steps, value: value.W);

        public static Rounded4 Missing => new(steps: 1f, value: new Vector4(value: float.NaN));

        // The rounded components back in units of the steps they were rounded to.
        public Vector4 Value(float steps) => new(w: (m_w / steps), x: (m_x / steps), y: (m_y / steps), z: (m_z / steps));

        private static int Round(float value, float steps) {
            var scaled = Math.Round(mode: MidpointRounding.ToEven, value: (((double)value) * steps));

            return ((double.IsFinite(d: scaled) && (Math.Abs(value: scaled) < int.MaxValue)) ? (int)scaled : int.MinValue);
        }
    }
    // A slot's rounded pose in the group's frame.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Pose(Rounded4 Position, Rounded4 Orientation) {
        public static Pose Identity => new(Orientation: new Rounded4(steps: OrientationSteps, value: new Vector4(w: 1f, x: 0f, y: 0f, z: 0f)), Position: default);
        public static Pose Missing => new(Orientation: Rounded4.Missing, Position: Rounded4.Missing);
    }
    // An instruction's fields as the bytes the geometry hashes.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct InstructionWords(in SdfInstruction instruction) {
        private readonly uint m_op = ((uint)instruction.Op);
        private readonly uint m_shape = instruction.Shape;
        private readonly uint m_blend = instruction.Blend;
        private readonly uint m_material = instruction.Material;
        private readonly Vector4 m_data0 = instruction.Data0;
        private readonly Vector4 m_data1 = instruction.Data1;
        private readonly uint m_flags = (instruction.Detail ? 1u : 0u);
    }
}
