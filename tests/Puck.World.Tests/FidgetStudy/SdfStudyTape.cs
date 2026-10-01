using Puck.SignedDistance;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>A double-precision three-vector for the study's evaluators.</summary>
/// <param name="X">The x component.</param>
/// <param name="Y">The y component.</param>
/// <param name="Z">The z component.</param>
public readonly record struct V3(double X, double Y, double Z) {
    public static V3 operator +(V3 a, V3 b) => new(X: (a.X + b.X), Y: (a.Y + b.Y), Z: (a.Z + b.Z));
    public static V3 operator -(V3 a, V3 b) => new(X: (a.X - b.X), Y: (a.Y - b.Y), Z: (a.Z - b.Z));
    public static V3 operator *(V3 a, double s) => new(X: (a.X * s), Y: (a.Y * s), Z: (a.Z * s));

    /// <summary>Returns the dot product.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The dot product.</returns>
    public static double Dot(V3 a, V3 b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
    /// <summary>Returns the cross product.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cross product.</returns>
    public static V3 Cross(V3 a, V3 b) => new(X: ((a.Y * b.Z) - (a.Z * b.Y)), Y: ((a.Z * b.X) - (a.X * b.Z)), Z: ((a.X * b.Y) - (a.Y * b.X)));

    /// <summary>Gets the Euclidean length.</summary>
    public double Length => Math.Sqrt(d: Dot(a: this, b: this));

    /// <summary>Returns component <paramref name="axis"/>.</summary>
    /// <param name="axis">The axis, 0 to 2.</param>
    /// <returns>The component.</returns>
    public double At(int axis) => (axis switch { 0 => X, 1 => Y, _ => Z });
    /// <summary>Returns a copy with component <paramref name="axis"/> replaced.</summary>
    /// <param name="axis">The axis, 0 to 2.</param>
    /// <param name="value">The new component.</param>
    /// <returns>The vector.</returns>
    public V3 With(int axis, double value) => (axis switch { 0 => (this with { X = value }), 1 => (this with { Y = value }), _ => (this with { Z = value }) });
}
/// <summary>A program as the GPU walks it, decoded from the packed words: instruction headers and payloads, the
/// per-shape and per-segment bounding spheres, the instance directory and the world-segment list, plus the frame's
/// dynamic transforms. Every read goes through <see cref="SdfProgram.Words"/>, so it sees the host-baked values the
/// kernels read.</summary>
public sealed class SdfStudyTape {
    /// <summary>One decoded instruction.</summary>
    /// <param name="Op">The op.</param>
    /// <param name="Shape">The shape lane with its flags masked off.</param>
    /// <param name="Blend">The blend lane.</param>
    /// <param name="Data0">The first payload vector.</param>
    /// <param name="Data1">The second payload vector.</param>
    /// <param name="RawShape">The shape lane as packed (flags included; bit pattern for seed lanes).</param>
    public readonly record struct Instruction(SdfOp Op, uint Shape, uint Blend, (double X, double Y, double Z, double W) Data0, (double X, double Y, double Z, double W) Data1, uint RawShape);
    /// <summary>One bounding sphere: its mode (none, static or dynamic), slot and sphere.</summary>
    /// <param name="Mode">The bound mode.</param>
    /// <param name="Slot">The dynamic slot.</param>
    /// <param name="Center">The center, before any dynamic offset.</param>
    /// <param name="Radius">The radius.</param>
    public readonly record struct Bound(uint Mode, int Slot, V3 Center, double Radius);
    /// <summary>One segment: its bound and its instruction range.</summary>
    /// <param name="Bound">The bound.</param>
    /// <param name="First">The first instruction.</param>
    /// <param name="End">One past the last instruction.</param>
    /// <param name="Owner">The owning instance, or -1 for a world segment.</param>
    /// <param name="Rigid">Whether the kernels walk the segment through its host-compiled rigid-leaf plan.</param>
    public readonly record struct Segment(Bound Bound, int First, int End, int Owner, bool Rigid);
    /// <summary>One instance directory entry.</summary>
    /// <param name="Bound">The instance bound.</param>
    /// <param name="FirstSegment">The first segment.</param>
    /// <param name="EndSegment">One past the last segment.</param>
    /// <param name="CameraHidden">Whether the cull keeps it out of camera masks.</param>
    public readonly record struct InstanceEntry(Bound Bound, int FirstSegment, int EndSegment, bool CameraHidden);
    /// <summary>One host-compiled rigid leaf: a shape instruction posed directly in its chain's base frame.</summary>
    /// <param name="Shape">The shape instruction.</param>
    /// <param name="Position">The local position.</param>
    /// <param name="Rotation">The local rotation, or <see langword="null"/> for identity.</param>
    /// <param name="Bound">The tight sphere in the chain's base frame, negative radius for none.</param>
    /// <param name="Fold">The fold prefix (position, rotation or null, first instruction, run length), or null.</param>
    public readonly record struct RigidLeaf(int Shape, V3 Position, (double X, double Y, double Z, double W)? Rotation, (V3 Center, double Radius) Bound, (V3 Position, (double X, double Y, double Z, double W)? Rotation, int First, int Count)? Fold);
    /// <summary>A segment's rigid plan: its leaves and the dynamic slot the chain rides, or -1.</summary>
    /// <param name="Leaves">The leaves.</param>
    /// <param name="DynamicSlot">The dynamic slot, or -1.</param>
    public readonly record struct RigidPlan(RigidLeaf[] Leaves, int DynamicSlot);

    /// <summary>Initializes a new instance of the <see cref="SdfStudyTape"/> class.</summary>
    /// <param name="program">The program.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    public SdfStudyTape(SdfProgram program, IReadOnlyList<DynamicTransform> transforms) {
        var words = program.Words;
        var count = ((int)words[0]);
        var materials = ((int)words[1]);
        var dataOffset = ((int)words[2]);
        var materialOffset = ((int)words[3]);
        var boundsOffset = (materialOffset + (SdfProgram.MaterialVectorsPerEntry * materials));
        var segmentOffset = (boundsOffset + (SdfProgram.BoundRecordVectors * count));
        var segmentCount = ((int)words[(segmentOffset * 4)]);
        var instanceOffset = ((segmentOffset + 1) + (2 * segmentCount));
        var instanceCount = ((int)words[(instanceOffset * 4)]);

        Instructions = new Instruction[count];
        ShapeBounds = new Bound[count];
        for (var index = 0; (index < count); index++) {
            var header = ((1 + index) * 4);
            var data = ((dataOffset + (2 * index)) * 4);
            var shape = words[(header + 1)];

            Instructions[index] = new Instruction(
                Blend: words[(header + 2)],
                Data0: (F(at: data, words: words), F(at: (data + 1), words: words), F(at: (data + 2), words: words), F(at: (data + 3), words: words)),
                Data1: (F(at: (data + 4), words: words), F(at: (data + 5), words: words), F(at: (data + 6), words: words), F(at: (data + 7), words: words)),
                Op: ((SdfOp)words[header]),
                RawShape: shape,
                Shape: ((((SdfOp)words[header]) == SdfOp.ShapeBlend) ? shape & SdfProgram.ShapeTypeMask : shape)
            );
            ShapeBounds[index] = ReadBound(vector: (boundsOffset + (2 * index)), words: words);
        }
        Segments = new Segment[segmentCount];
        for (var segment = 0; (segment < segmentCount); segment++) {
            var vector = ((segmentOffset + 1) + (2 * segment));
            var meta = ((vector + 1) * 4);

            Segments[segment] = new Segment(
                Bound: ReadBound(vector: vector, words: words) with { Mode = words[meta] & SdfProgram.SegmentBoundModeMask },
                End: ((int)words[(meta + 3)]),
                First: ((int)words[(meta + 2)]),
                Owner: -1,
                Rigid: ((words[meta] & SdfProgram.SegmentRigidPlanFlag) != 0u)
            );
        }
        Instances = new InstanceEntry[instanceCount];
        for (var instance = 0; (instance < instanceCount); instance++) {
            var vector = ((instanceOffset + 1) + (2 * instance));
            var meta = ((vector + 1) * 4);
            var first = ((int)words[(meta + 2)]);
            var end = ((int)(words[(meta + 3)] & SdfProgram.SegmentEndMask));

            Instances[instance] = new InstanceEntry(
                Bound: ReadBound(vector: vector, words: words),
                CameraHidden: ((words[(meta + 3)] & SdfProgram.CameraHiddenInstanceFlag) != 0u),
                EndSegment: end,
                FirstSegment: first
            );
            for (var segment = first; (segment < end); segment++) {
                Segments[segment] = (Segments[segment] with { Owner = instance });
            }
        }
        var rigidPlanOffset = ((int)words[((segmentOffset * 4) + 2)]);

        RigidPlans = new RigidPlan?[segmentCount];
        for (var segment = 0; (segment < segmentCount); segment++) {
            if (!Segments[segment].Rigid) {
                continue;
            }

            var entry = ((rigidPlanOffset + segment) * 4);
            var leafVector = ((int)words[entry]);
            var leafCount = ((int)words[(entry + 1)]);
            var leaves = new List<RigidLeaf>();

            for (var leaf = 0; (leaf < leafCount); leaf++) {
                var at = ((leafVector + (3 * leaf)) * 4);
                var packed = words[(at + 3)];
                (V3, (double, double, double, double)?, int, int)? fold = null;

                if ((packed & SdfProgram.RigidLeafFoldedFlag) != 0u) {
                    leaf++;

                    var extension = ((leafVector + (3 * leaf)) * 4);
                    var prefix = words[(extension + 3)];

                    fold = (
                        new V3(X: F(at: extension, words: words), Y: F(at: (extension + 1), words: words), Z: F(at: (extension + 2), words: words)),
                        (((prefix & SdfProgram.RigidLeafIdentityRotationFlag) != 0u) ? null : (F(at: (extension + 4), words: words), F(at: (extension + 5), words: words), F(at: (extension + 6), words: words), F(at: (extension + 7), words: words))),
                        ((int)(prefix & SdfProgram.RigidLeafShapeMask)),
                        Math.Min(val1: ((int)words[(extension + 8)]), val2: SdfProgram.RigidLeafMaxFoldRun)
                    );
                }
                leaves.Add(item: new RigidLeaf(
                    Bound: (new V3(X: F(at: (at + 8), words: words), Y: F(at: (at + 9), words: words), Z: F(at: (at + 10), words: words)), F(at: (at + 11), words: words)),
                    Fold: fold,
                    Position: new V3(X: F(at: at, words: words), Y: F(at: (at + 1), words: words), Z: F(at: (at + 2), words: words)),
                    Rotation: (((packed & SdfProgram.RigidLeafIdentityRotationFlag) != 0u) ? null : (F(at: (at + 4), words: words), F(at: (at + 5), words: words), F(at: (at + 6), words: words), F(at: (at + 7), words: words))),
                    Shape: ((int)(packed & SdfProgram.RigidLeafShapeMask))
                ));
            }
            RigidPlans[segment] = new RigidPlan(DynamicSlot: (((int)words[(entry + 2)]) - 1), Leaves: [.. leaves]);
        }
        StepScale = ((program.StepScale > 0f) ? program.StepScale : 1.0);
        Transforms = [.. transforms];
        InstructionCount = count;
    }

    /// <summary>Gets the decoded instructions.</summary>
    public Instruction[] Instructions { get; }
    /// <summary>Gets the per-instruction shape bounds (mode none for a non-shape or unbounded shape).</summary>
    public Bound[] ShapeBounds { get; }
    /// <summary>Gets the segments in walk order.</summary>
    public Segment[] Segments { get; }
    /// <summary>Gets the instance directory.</summary>
    public InstanceEntry[] Instances { get; }
    /// <summary>Gets each segment's rigid plan, or <see langword="null"/> where the generic walk runs.</summary>
    public RigidPlan?[] RigidPlans { get; }
    /// <summary>Gets the program's Lipschitz step scale.</summary>
    public double StepScale { get; }
    /// <summary>Gets the frame's dynamic transforms.</summary>
    public DynamicTransform[] Transforms { get; }
    /// <summary>Gets the instruction count.</summary>
    public int InstructionCount { get; }

    /// <summary>Returns a bound's world center, adding its dynamic slot's position.</summary>
    /// <param name="bound">The bound.</param>
    /// <returns>The world center.</returns>
    public V3 CenterOf(Bound bound) {
        if ((bound.Mode == SdfProgram.BoundModeDynamic) && (bound.Slot >= 0) && (bound.Slot < Transforms.Length)) {
            var position = Transforms[bound.Slot].Position;

            return (bound.Center + new V3(X: position.X, Y: position.Y, Z: position.Z));
        }

        return bound.Center;
    }

    private static Bound ReadBound(ReadOnlySpan<uint> words, int vector) {
        var at = (vector * 4);

        return new Bound(
            Center: new V3(X: F(at: at, words: words), Y: F(at: (at + 1), words: words), Z: F(at: (at + 2), words: words)),
            Mode: words[(at + 4)],
            Radius: F(at: (at + 3), words: words),
            Slot: ((int)words[(at + 5)])
        );
    }
    private static double F(ReadOnlySpan<uint> words, int at) => BitConverter.UInt32BitsToSingle(value: words[at]);
}
