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
/// offset its program packed the entry at;</item>
/// <item>an instance's shapes' materials by their place among the group's materials (<see cref="Materials"/>), since a
/// program numbers its palette in the order its emitters register materials;</item>
/// <item>a static instance's bound, and a dynamic instance's bound radius, which clips what the bake samples. A dynamic
/// instance's bound center is offset along world axes by the slot's pose when the program was emitted;</item>
/// <item>a dynamic instance's transform into its own slot by that role rather than the slot's number, which each program
/// assigns;</item>
/// <item>a transform into any other slot by that slot's pose in the group's frame (its own slot's, or world space),
/// rounded to <see cref="PositionSteps"/> and <see cref="OrientationSteps"/>, and, under a slot of the group's own, by
/// that slot's offset from it, which names the member a vertex of the mesh rides (<see cref="SdfMesh.Members"/>). The bake samples such an instruction
/// through that slot's transform in the frame, so the shapes of a stamp-pool blend group, each riding its own slot inside
/// the group's instance, take their places and turns from the frame's transforms rather than from the
/// instructions;</item>
/// <item>the lanes (<see cref="DynamicTransform.Lanes"/>) a lane erosion or a morph reads from the slot its chain
/// rides.</item>
/// </list>
/// The rounded poses and lanes it hashes are kept (<see cref="Poses"/>), and the bake samples the group through them
/// rather than through the frame it dispatches in, so a mesh is a function of its geometry alone.
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

    private readonly XxHash128 m_hash = new();
    private readonly List<int> m_materials = [];
    private readonly SortedDictionary<int, DynamicTransform> m_poses = [];
    // The slot the chain being appended rides, whose lanes a lane erosion reads; -1 under none.
    private int m_chainSlot = -1;

    /// <summary>Gets the materials the appended instances' shapes carry, in the order the geometry numbers them.</summary>
    public IReadOnlyList<int> Materials => m_materials;
    /// <summary>Gets each slot the appended instructions read, with its pose in the group's frame and its lanes as the
    /// geometry hashed them, rounded: the group's own slot at the identity, any other slot where it stood in the frame the
    /// geometry was appended from. A slot the frame held no transform for is absent.</summary>
    public IReadOnlyDictionary<int, DynamicTransform> Poses => m_poses;
    /// <summary>Gets the turn from world axes into the group's frame, as the frame the geometry was appended from held it:
    /// the inverse of its own slot's orientation, the identity for a group in world space. A dynamic bound's center is
    /// an offset along world axes from its slot, which the bake turns by it into the group's frame.</summary>
    public Quaternion BoundTurn { get; private set; } = Quaternion.Identity;
    /// <summary>Gets the geometry of everything appended so far.</summary>
    public SdfMeshGeometry Geometry => new(Dynamic: dynamic, Hash: m_hash.GetCurrentHashAsUInt128());

    /// <summary>Appends one instance of a program: its instructions, each shape's material by its place among the
    /// group's materials, each other slot it reads by that slot's pose in the instance's frame, and its bound.</summary>
    /// <param name="program">The program.</param>
    /// <param name="instance">The instance's index in <see cref="SdfProgram.Instances"/>.</param>
    /// <param name="transforms">The frame's dynamic transforms, which the bake samples the instance through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="transforms"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="instance"/> names no instance.</exception>
    public void AppendInstance(SdfProgram program, int instance, IReadOnlyList<DynamicTransform> transforms) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        var range = program.Instances[instance];
        var bound = (range.IsDynamic ? new Vector4(x: 0f, y: 0f, z: 0f, w: range.Radius) : new Vector4(value: range.Center, w: range.Radius));
        var count = (range.End - range.First);
        var ownSlot = (range.IsDynamic ? range.Slot : -1);

        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(in bound)));
        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(in count)));
        m_chainSlot = -1;
        // The bake carries each sample into the group's frame through its own slot, which is that frame.
        if (ownSlot >= 0) {
            Keep(slot: ownSlot, pose: Pose.Identity, transforms: transforms);
            if (ownSlot < transforms.Count) { BoundTurn = Quaternion.Inverse(value: PoseOf(transform: transforms[ownSlot]).Orientation); }
        }

        for (var index = range.First; (index < range.End); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op == SdfOp.ShapeBlend) {
                var material = (int)instruction.Material;
                var place = m_materials.IndexOf(item: material);

                if (place < 0) {
                    place = m_materials.Count;
                    m_materials.Add(item: material);
                }
                instruction = (instruction with { Material = (uint)place });
            }
            Append(index: index, instruction: instruction, ownSlot: ownSlot, program: program, transforms: transforms);
        }
    }
    /// <summary>Appends the reach a group's box is clamped to: a group whose bound passes the frame's far distance, or the
    /// group of the segments no instance owns, bakes over a box that reach wide, so the same instructions under another
    /// reach mesh another surface.</summary>
    /// <param name="reach">The reach, in world units.</param>
    public void AppendReach(float reach) => m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(in reach)));
    /// <summary>Appends one instruction of a program as it stands, its material included, with the world-space pose of a
    /// slot it reads.</summary>
    /// <param name="program">The program.</param>
    /// <param name="index">The instruction's index in <see cref="SdfProgram.Instructions"/>.</param>
    /// <param name="transforms">The frame's dynamic transforms, which the bake samples the instruction through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="transforms"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> names no instruction.</exception>
    public void AppendInstruction(SdfProgram program, int index, IReadOnlyList<DynamicTransform> transforms) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        Append(index: index, instruction: program.Instructions[index], ownSlot: -1, program: program, transforms: transforms);
    }

    // Appends an instruction: a transform into the group's own slot by that role and into another slot with that slot's
    // pose in the group's frame, a lane erosion with the lanes it reads, and a side-table entry's values for its offset.
    private void Append(SdfProgram program, int index, SdfInstruction instruction, int ownSlot, IReadOnlyList<DynamicTransform> transforms) {
        switch (instruction.Op) {
            case SdfOp.ResetPoint:
                m_chainSlot = -1;
                break;
            case SdfOp.TransformDynamic: {
                var slot = (int)instruction.Data0.X;

                m_chainSlot = slot;
                if (slot == ownSlot) {
                    instruction = (instruction with { Data0 = (instruction.Data0 with { X = OwnSlot }) });
                    Keep(slot: slot, pose: Pose.Identity, transforms: transforms);
                } else {
                    var pose = RelativePose(ownSlot: ownSlot, slot: slot, transforms: transforms);
                    var offset = ((ownSlot >= 0) ? (slot - ownSlot) : 0);

                    instruction = (instruction with { Data0 = (instruction.Data0 with { X = OtherSlot }) });
                    m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Pose>(in pose)));
                    m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(in offset)));
                    Keep(slot: slot, pose: pose, transforms: transforms);
                }
                break;
            }
            case SdfOp.LaneErode:
                AppendLanes(transforms: transforms);
                break;
            case SdfOp.PopField when (instruction.Blend == (uint)SdfBlendOp.Morph):
                AppendLanes(transforms: transforms);
                break;
            default:
                break;
        }

        var table = (((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape is SdfShapeType.ConvexPolygon or SdfShapeType.Sweep or SdfShapeType.Path))
            ? program.SideTableValues(instructionIndex: index)
            : []);

        if (!table.IsEmpty) {
            instruction = (instruction with { Data0 = (instruction.Data0 with { X = 0f }) });
            m_hash.Append(source: MemoryMarshal.AsBytes(span: table));
        }

        var words = new InstructionWords(instruction: instruction);

        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<InstructionWords>(in words)));
    }
    // Appends the lanes of the slot the chain rides, which a lane erosion or a morph reads; zero under none.
    private void AppendLanes(IReadOnlyList<DynamicTransform> transforms) {
        var lanes = new Rounded4(steps: PositionSteps, value: (((m_chainSlot >= 0) && (m_chainSlot < transforms.Count)) ? transforms[m_chainSlot].Lanes : Vector4.Zero));

        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Rounded4>(in lanes)));
    }
    // Keeps a slot's rounded pose as the bake reads it: its position, its orientation, renormalized in double precision
    // so the same rounded components always turn alike, and its lanes rounded as AppendLanes rounds them. A missing slot
    // is left out.
    private void Keep(int slot, Pose pose, IReadOnlyList<DynamicTransform> transforms) {
        if ((pose == Pose.Missing) || (slot < 0) || (slot >= transforms.Count) || m_poses.ContainsKey(key: slot)) { return; }

        var turn = pose.Orientation.Value(steps: OrientationSteps);
        var length = Math.Sqrt(d: ((((double)turn.X * turn.X) + ((double)turn.Y * turn.Y)) + (((double)turn.Z * turn.Z) + ((double)turn.W * turn.W))));
        var orientation = ((length > 0d)
            ? new Quaternion(x: (float)(turn.X / length), y: (float)(turn.Y / length), z: (float)(turn.Z / length), w: (float)(turn.W / length))
            : Quaternion.Identity);
        var position = pose.Position.Value(steps: PositionSteps);

        var lanes = new Rounded4(steps: PositionSteps, value: transforms[slot].Lanes);

        m_poses.Add(key: slot, value: new DynamicTransform(Lanes: lanes.Value(steps: PositionSteps), Orientation: orientation, Position: new Vector3(x: position.X, y: position.Y, z: position.Z)));
    }
    // A slot's pose in the frame of the group's own slot (world space under none), rounded: where a sample the bake
    // carries into the group's frame lands in that slot's frame. A slot the frame holds no transform for is Missing.
    private static Pose RelativePose(int ownSlot, int slot, IReadOnlyList<DynamicTransform> transforms) {
        if ((slot < 0) || (slot >= transforms.Count) || (ownSlot >= transforms.Count)) { return Pose.Missing; }

        var (framePosition, frameOrientation) = ((ownSlot < 0) ? (Vector3.Zero, Quaternion.Identity) : PoseOf(transform: transforms[ownSlot]));
        var (position, orientation) = PoseOf(transform: transforms[slot]);
        var inverse = Quaternion.Inverse(value: frameOrientation);
        var relative = Quaternion.Normalize(value: (inverse * orientation));

        // q and -q turn alike.
        if (relative.W < 0f) { relative = Quaternion.Negate(value: relative); }

        return new Pose(
            Orientation: new Rounded4(steps: OrientationSteps, value: new Vector4(x: relative.X, y: relative.Y, z: relative.Z, w: relative.W)),
            Position: new Rounded4(steps: PositionSteps, value: new Vector4(value: Vector3.Transform(value: (position - framePosition), rotation: inverse), w: 0f))
        );
    }
    // A transform's position and orientation, an unset orientation read as the identity, as the bake reads it.
    private static (Vector3 Position, Quaternion Orientation) PoseOf(DynamicTransform transform) =>
        (transform.Position, ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation));

    // A vector's components rounded to steps a unit; a component no int holds rounds to int.MinValue.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rounded4(Vector4 value, float steps) {
        private readonly int m_x = Round(steps: steps, value: value.X);
        private readonly int m_y = Round(steps: steps, value: value.Y);
        private readonly int m_z = Round(steps: steps, value: value.Z);
        private readonly int m_w = Round(steps: steps, value: value.W);

        public static Rounded4 Missing => new(steps: 1f, value: new Vector4(value: float.NaN));

        // The rounded components back in units of the steps they were rounded to.
        public Vector4 Value(float steps) => new(x: (m_x / steps), y: (m_y / steps), z: (m_z / steps), w: (m_w / steps));

        private static int Round(float value, float steps) {
            var scaled = Math.Round(value: ((double)value * steps), mode: MidpointRounding.ToEven);

            return ((double.IsFinite(d: scaled) && (Math.Abs(value: scaled) < int.MaxValue)) ? (int)scaled : int.MinValue);
        }
    }
    // A slot's rounded pose in the group's frame.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Pose(Rounded4 Position, Rounded4 Orientation) {
        public static Pose Identity => new(Orientation: new Rounded4(steps: OrientationSteps, value: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f)), Position: default);
        public static Pose Missing => new(Orientation: Rounded4.Missing, Position: Rounded4.Missing);
    }
    // An instruction's fields as the bytes the geometry hashes.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct InstructionWords(in SdfInstruction instruction) {
        private readonly uint m_op = (uint)instruction.Op;
        private readonly uint m_shape = instruction.Shape;
        private readonly uint m_blend = instruction.Blend;
        private readonly uint m_material = instruction.Material;
        private readonly Vector4 m_data0 = instruction.Data0;
        private readonly Vector4 m_data1 = instruction.Data1;
        private readonly uint m_flags = (instruction.Detail ? 1u : 0u);
    }
}
