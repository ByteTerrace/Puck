using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// One group of a program's baked-mesh path, as <see cref="Plan{T}"/> forms it: the instances it selects, the frame its mesh
/// is stored in, the geometry it is keyed by (<see cref="SdfMeshGeometryBuilder"/>) and the box its first lattice covers.
/// <see cref="SdfMeshPathBake"/> bakes the groups this plans, and the mesh audit (<see cref="SdfMeshAudit"/>) plans the same
/// groups to find their stored meshes and the field each was sampled from.
/// <para>A dynamic group is every instance riding one slot, in that slot's frame; a static group is one instance, or the
/// run of static instances that bake with the first (<see cref="SdfInstanceRange.BakesWithPrevious"/>), in world space; the
/// world group (<see cref="IsWorld"/>) is every segment no instance owns.</para>
/// </summary>
/// <param name="selector">The dynamic slot the group rides when non-negative; <c>-(index + 1)</c> of its first static
/// instance otherwise; <see cref="WorldSelector"/> for the world group.</param>
/// <param name="dynamic">Whether the group bakes in its slot's frame.</param>
public class SdfMeshBakeGroup(int selector, bool dynamic) {
    /// <summary>The selector of the group of segments no instance owns, which the bake kernel samples with every instance
    /// masked out and the world segments kept. KEEP IN SYNC with sdf-mesh-bake.comp.hlsl.</summary>
    public const int WorldSelector = int.MinValue;

    private readonly List<int> m_instances = [];

    /// <summary>Gets the group's selector: its slot, <c>-(index + 1)</c> of its first static instance, or
    /// <see cref="WorldSelector"/>.</summary>
    public int Selector { get; } = selector;
    /// <summary>Gets whether the group bakes in its slot's frame rather than in world space.</summary>
    public bool Dynamic { get; } = dynamic;

    /// <summary>Gets whether the group is the program's segments no instance owns.</summary>
    public bool IsWorld => (Selector == WorldSelector);
    /// <summary>Gets the slot a dynamic group rides.</summary>
    public int Slot => Selector;

    /// <summary>Gets the instances a static group's run holds, from <c>-(Selector + 1)</c> on.</summary>
    public int Instances { get; internal set; } = 1;
    /// <summary>Gets the program ordinal of the first instance the group holds, or -1 for the world group, which holds
    /// none: the instance a host names the group's draw by.</summary>
    public int FirstInstance => ((m_instances.Count > 0) ? m_instances[0] : -1);
    /// <summary>Gets whether the group's shapes all carry glass materials (<see cref="SdfProgram.IsGlassRange"/>): its
    /// mesh draws in the glass pass over the opaque image rather than in the mesh pass, casts no shadow and lights no
    /// probe. A glass instance never bakes with an opaque one, so a static group is glass or opaque whole; a dynamic
    /// group is glass only while every instance riding its slot is, and the world group never is.</summary>
    public bool Glass { get; private set; }
    /// <summary>Gets each static instance's bounding sphere, which a union of several sizes its lattice by, and the finest
    /// feature it declares (<see cref="SdfInstanceRange.Detail"/>), which refines its lattice.</summary>
    public List<(Vector3 Center, float Radius, float Detail)> Members { get; } = [];

    /// <summary>Gets the finest feature, in world units, any of the group's instances declares
    /// (<see cref="SdfInstanceRange.Detail"/>); zero for none.</summary>
    public float Detail { get; private set; }
    /// <summary>Gets the low corner of the box the group's detailed instances' bounds cover, in the group's frame: the
    /// region a lattice of one instance or slot refines toward <see cref="Detail"/>.</summary>
    public Vector3 DetailLow { get; private set; }
    /// <summary>Gets the high corner of the box the group's detailed instances' bounds cover, in the group's frame.</summary>
    public Vector3 DetailHigh { get; private set; }

    /// <summary>Gets the geometry the group bakes while its instances are appended: a world group's names its segments'
    /// materials as they are, an instance group's by their place in <see cref="MaterialOrder"/>.</summary>
    public SdfMeshGeometryBuilder Geometry { get; } = new(dynamic: dynamic);

    /// <summary>Gets the geometry the group was sealed with, which keys its mesh.</summary>
    public SdfMeshGeometry Sealed { get; private set; }
    /// <summary>Gets the form the group was sealed with (<see cref="SdfMeshGeometryBuilder.Form"/>): its geometry without
    /// the poses of the slots besides its own.</summary>
    public UInt128 Form { get; private set; }

    /// <summary>Gets the materials the group's shapes carry, in the order its geometry numbers them.</summary>
    public int[] MaterialOrder { get; private set; } = [];

    /// <summary>Gives the group the material ids a later program numbers its shapes with, place for place: the geometry
    /// names each material by its place (<see cref="SdfMeshGeometryBuilder.Materials"/>), so the group's geometry and form
    /// stay as they were.</summary>
    /// <param name="materials">The ids, as many as <see cref="MaterialOrder"/> holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="materials"/> holds another count of ids.</exception>
    protected void Renumber(int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        if (materials.Length != MaterialOrder.Length) {
            throw new ArgumentException(message: $"A group of {MaterialOrder.Length} materials is renumbered by as many ids, not {materials.Length}.", paramName: nameof(materials));
        }

        MaterialOrder = materials;
    }

    /// <summary>Gets the first slot of <see cref="PoseRows"/>.</summary>
    public int PoseFirst { get; private set; }

    /// <summary>Gets the poses the group bakes through: the turn from world axes into its frame, then from its first slot
    /// three rows a slot as the dynamic-transform table lays one out, what its geometry hashed, so its samples never read
    /// the frame's transforms and its mesh is a function of its geometry, whatever frame each lattice dispatches in. A slot
    /// between two the group reads holds the identity.</summary>
    public Vector4[] PoseRows { get; private set; } = [];

    /// <summary>Gets or sets the low corner of the group's box: its members' bounds when planned, then what the bake's
    /// lattices narrow it to.</summary>
    public Vector3 Low { get; set; }
    /// <summary>Gets or sets the high corner of the group's box.</summary>
    public Vector3 High { get; set; }
    /// <summary>Gets whether a member's bound passed the reach, so the group's box, and its geometry, hold the reach.</summary>
    public bool Clamped { get; internal set; }

    /// <summary>Plans the groups of a program: one per dynamic slot and per run of static instances baked together (one
    /// rigid copy), unions of several static shapes first, so their long lattices and contours lead each frame's round,
    /// and the group of the segments no instance owns last. Each is sealed: its geometry, materials and poses fixed.</summary>
    /// <typeparam name="T">The group type the caller keeps.</typeparam>
    /// <param name="program">The program.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    /// <param name="farDistance">The frame's far distance, which clamps a bound past it.</param>
    /// <param name="create">Makes a group of a selector and whether it is dynamic.</param>
    /// <returns>The groups, in the order the bake takes them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/>, <paramref name="transforms"/> or
    /// <paramref name="create"/> is <see langword="null"/>.</exception>
    public static List<T> Plan<T>(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float farDistance, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: create);

        return Ordered(
            create: create,
            groups: PlanGroups(create: create, farDistance: farDistance, program: program, slots: null, snap: null, transforms: transforms),
            program: program,
            reach: Reach(farDistance: farDistance),
            transforms: transforms
        );
    }
    /// <summary>Plans again, at a frame's transforms, the groups of a plan <see cref="Plan{T}"/> made of the same program
    /// at other transforms that a caller cannot keep as they stand: each dynamic group the predicate names, whose box,
    /// poses and geometry read the transforms, and each static or world group it names whose geometry reads a slot
    /// (<see cref="SdfMeshGeometryBuilder.ReadsSlots"/>). Which groups a program has, the instances each holds and their
    /// order never read a transform, so each group planned again is the one <see cref="Plan{T}"/> makes at the frame's
    /// transforms, at its place in the list; every other group stays as it was planned.</summary>
    /// <typeparam name="T">The group type the caller keeps.</typeparam>
    /// <param name="groups">The plan, changed in place.</param>
    /// <param name="program">The program the plan was made of.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    /// <param name="farDistance">The far distance the plan was made at.</param>
    /// <param name="stale">Whether the caller needs a group planned at the frame's transforms.</param>
    /// <param name="create">Makes a group of a selector and whether it is dynamic.</param>
    /// <returns>How many groups were planned again.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static int Refresh<T>(List<T> groups, SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float farDistance, Func<T, bool> stale, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: stale);
        ArgumentNullException.ThrowIfNull(argument: create);

        var reach = Reach(farDistance: farDistance);
        var refreshed = 0;
        HashSet<int>? slots = null;

        for (var index = 0; (index < groups.Count); index++) {
            var group = groups[index];

            if ((!group.Dynamic && !group.Geometry.ReadsSlots) || !stale(arg: group)) { continue; }
            if (group.Dynamic) {
                (slots ??= []).Add(item: group.Slot);

                continue;
            }

            groups[index] = (group.IsWorld
                ? WorldGroup(create: create, program: program, reach: reach, transforms: transforms)!
                : Replanned(create: create, group: group, program: program, reach: reach, transforms: transforms));
            refreshed++;
        }

        if (slots is null) { return refreshed; }

        var planned = PlanGroups(create: create, farDistance: farDistance, program: program, slots: slots, snap: null, transforms: transforms);
        var bySlot = new Dictionary<int, T>(capacity: planned.Count);

        foreach (var group in planned) { bySlot[group.Slot] = group; }
        for (var index = 0; (index < groups.Count); index++) {
            if (groups[index].Dynamic && bySlot.TryGetValue(key: groups[index].Slot, value: out var group)) {
                groups[index] = group;
                refreshed++;
            }
        }

        return refreshed;
    }
    // A static group planned again at a frame's transforms: the same instances, members and box, its geometry appended
    // anew.
    private static T Replanned<T>(T group, SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float reach, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        var replanned = create(arg1: group.Selector, arg2: false);

        replanned.Instances = group.Instances;
        replanned.Low = group.Low;
        replanned.High = group.High;
        replanned.Glass = group.Glass;
        replanned.Clamped = group.Clamped;
        replanned.Detail = group.Detail;
        replanned.DetailLow = group.DetailLow;
        replanned.DetailHigh = group.DetailHigh;
        replanned.Members.AddRange(collection: group.Members);

        foreach (var index in group.m_instances) {
            replanned.m_instances.Add(item: index);
            replanned.Geometry.AppendInstance(instance: index, program: program, transforms: transforms);
        }

        if (replanned.Clamped) { replanned.Geometry.AppendReach(reach: reach); }
        replanned.Seal();

        return replanned;
    }
    /// <summary>Plans the group of one dynamic slot of a program as <see cref="Plan{T}"/> plans it, with every other slot
    /// its instances read keyed at its pose rounded to the given steps (<see cref="SdfMeshGeometryBuilder.Snap"/>): the
    /// settled pose a skinned group bakes again at (<see cref="SdfMeshPathBake"/>).</summary>
    /// <typeparam name="T">The group type the caller keeps.</typeparam>
    /// <param name="program">The program.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    /// <param name="farDistance">The frame's far distance, which clamps a bound past it.</param>
    /// <param name="slot">The dynamic slot whose group is planned.</param>
    /// <param name="positionSteps">The steps a metre another slot's position rounds to.</param>
    /// <param name="orientationSteps">The steps a unit another slot's orientation components round to.</param>
    /// <param name="create">Makes a group of a selector and whether it is dynamic.</param>
    /// <returns>The group, or <see langword="null"/> when no active instance rides the slot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/>, <paramref name="transforms"/> or
    /// <paramref name="create"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative, or a step count is not
    /// positive.</exception>
    public static T? PlanSlot<T>(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float farDistance, int slot, float positionSteps, float orientationSteps, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: create);
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: positionSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: orientationSteps);

        var groups = PlanGroups(create: create, farDistance: farDistance, program: program, slots: new HashSet<int> { slot }, snap: (positionSteps, orientationSteps), transforms: transforms);

        return ((groups.Count > 0) ? groups[0] : null);
    }

    private static float Reach(float farDistance) => MathF.Max(x: 1f, y: farDistance);

    // Plans every group, or with `slots` the groups of those dynamic slots alone, other slots' poses snapped to `snap`'s
    // steps when it is given, each sealed and listed at its first instance.
    private static List<T> PlanGroups<T>(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float farDistance, IReadOnlySet<int>? slots, (float Position, float Orientation)? snap, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        var groups = new List<T>();
        var bySlot = new Dictionary<int, T>();
        var instances = program.Instances;
        var reach = Reach(farDistance: farDistance);
        // The static group the last instance joined, which a following instance that bakes with its previous one extends
        // while the run stays unbroken.
        T? staticRun = null;

        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];

            if (!instance.Active) { continue; }
            if ((slots is not null) && (!instance.IsDynamic || !slots.Contains(item: instance.Slot))) { continue; }

            var radius = (float.IsFinite(f: instance.Radius) ? MathF.Min(x: instance.Radius, y: reach) : reach);
            // A dynamic bound is a sphere about the slot's position offset along world axes; about the slot's origin in its
            // own frame, its center is that offset turned back by the slot's orientation in this frame.
            var orientation = ((instance.IsDynamic && (instance.Slot < transforms.Count)) ? transforms[instance.Slot].Orientation : default);
            // Rounded as the geometry rounds a pose, so one geometry bakes over one box whatever frame turned it back.
            var center = (instance.IsDynamic
                ? Rounded(value: Vector3.Transform(value: instance.Center, rotation: Quaternion.Inverse(value: ((orientation == default) ? Quaternion.Identity : orientation))))
                : instance.Center);
            var glass = program.IsGlassRange(end: instance.End, first: instance.First);
            T group;

            if (instance.IsDynamic) {
                if (!bySlot.TryGetValue(key: instance.Slot, value: out group!)) {
                    group = create(arg1: instance.Slot, arg2: true);
                    if (snap is { } steps) { group.Geometry.Snap(orientationSteps: steps.Orientation, positionSteps: steps.Position); }
                    group.Low = (center - new Vector3(value: radius));
                    group.High = (center + new Vector3(value: radius));
                    group.Glass = glass;
                    bySlot.Add(key: instance.Slot, value: group);
                    groups.Add(item: group);
                } else {
                    group.Low = Vector3.Min(value1: group.Low, value2: (center - new Vector3(value: radius)));
                    group.High = Vector3.Max(value1: group.High, value2: (center + new Vector3(value: radius)));
                    group.Glass &= glass;
                }
            } else if (instance.BakesWithPrevious && (staticRun is { } run) && !glass && !run.Glass && ((-(run.Selector + 1) + run.Instances) == index)) {
                group = run;
                group.Instances++;
                group.Low = Vector3.Min(value1: group.Low, value2: (instance.Center - new Vector3(value: radius)));
                group.High = Vector3.Max(value1: group.High, value2: (instance.Center + new Vector3(value: radius)));
            } else {
                group = create(arg1: -(index + 1), arg2: false);
                group.Low = (instance.Center - new Vector3(value: radius));
                group.High = (instance.Center + new Vector3(value: radius));
                group.Glass = glass;
                groups.Add(item: group);
            }
            if (!instance.IsDynamic) {
                group.Members.Add(item: (instance.Center, radius, instance.Detail));
            }
            if (instance.Detail > 0f) {
                var low = (center - new Vector3(value: radius));
                var high = (center + new Vector3(value: radius));
                var first = (group.Detail <= 0f);

                group.DetailLow = (first ? low : Vector3.Min(value1: group.DetailLow, value2: low));
                group.DetailHigh = (first ? high : Vector3.Max(value1: group.DetailHigh, value2: high));
                group.Detail = (first ? instance.Detail : MathF.Min(x: group.Detail, y: instance.Detail));
            }
            staticRun = (instance.IsDynamic ? null : group);
            // A bound past the reach bakes over the reach instead, which the geometry then holds.
            group.Clamped |= (radius != instance.Radius);
            group.m_instances.Add(item: index);
            group.Geometry.AppendInstance(instance: index, program: program, transforms: transforms);
        }

        foreach (var group in groups) {
            if (group.Clamped) { group.Geometry.AppendReach(reach: reach); }
            group.Seal();
        }

        return groups;
    }
    // Puts a whole plan's groups, listed at their first instances, in the order the bake takes them, and adds the world
    // group.
    private static List<T> Ordered<T>(List<T> groups, SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float reach, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        // A union of several static shapes bakes the largest lattice and contours longest, so its lattices go first in
        // each frame's round, and its contour overlaps the other groups' sampling rather than running after them.
        var unions = groups.Where(predicate: static group => (group.Members.Count > 1)).ToArray();

        if (unions.Length > 0) {
            _ = groups.RemoveAll(match: static group => (group.Members.Count > 1));
            groups.InsertRange(collection: unions, index: 0);
        }

        // The segments no instance owns, which every instance group's walk omits, bake as one group of their own, over a
        // cube the frame's far distance wide about the origin.
        if (WorldGroup(create: create, program: program, reach: reach, transforms: transforms) is { } world) {
            groups.Add(item: world);
        }

        return groups;
    }

    /// <summary>Returns which of a program's instructions a group's field holds, in program order: its instances' slices,
    /// or for the world group every instruction no instance owns.</summary>
    /// <param name="program">The program the group was planned from.</param>
    /// <returns>The instruction indices.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<int> InstructionIndices(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var indices = new List<int>();

        if (IsWorld) {
            var owned = Owned(program: program);

            for (var index = 0; (index < owned.Length); index++) {
                if (!owned[index]) { indices.Add(item: index); }
            }
        } else {
            foreach (var instance in m_instances) {
                var range = program.Instances[instance];

                for (var index = range.First; (index < range.End); index++) { indices.Add(item: index); }
            }
        }

        return indices;
    }

    // Seals the group: its geometry, its materials, and the pose rows its lattices read.
    private void Seal() {
        Geometry.Seal();
        Sealed = Geometry.Geometry;
        Form = Geometry.Form;
        MaterialOrder = [.. Geometry.Materials];

        var (first, last) = Geometry.PoseSlots;

        if (first < 0) { return; }

        var rows = new Vector4[(1 + (3 * ((last - first) + 1)))];
        var turn = Geometry.BoundTurn;

        rows[0] = new Vector4(w: turn.W, x: turn.X, y: turn.Y, z: turn.Z);
        for (var row = 2; (row < rows.Length); row += 3) {
            rows[row] = new Vector4(w: 1f, x: 0f, y: 0f, z: 0f);
        }
        Geometry.WritePoseRows(first: first, rows: rows.AsSpan(start: 1));
        PoseFirst = first;
        PoseRows = rows;
    }
    // A position rounded to the steps a metre the geometry rounds a slot's position to.
    private static Vector3 Rounded(Vector3 value) => new(x: Rounded(value: value.X), y: Rounded(value: value.Y), z: Rounded(value: value.Z));
    private static float Rounded(float value) =>
        ((float)(Math.Round(mode: MidpointRounding.ToEven, value: (((double)value) * SdfMeshGeometryBuilder.PositionSteps)) / SdfMeshGeometryBuilder.PositionSteps));
    // Which instructions an instance owns.
    private static bool[] Owned(SdfProgram program) {
        var owned = new bool[program.Instructions.Count];

        foreach (var instance in program.Instances) {
            for (var index = instance.First; (index < instance.End); index++) { owned[index] = true; }
        }

        return owned;
    }
    // The group of the shapes the program's segments outside every instance hold, keyed by every such instruction (the
    // transforms ahead of the first drawn shape place it too) with its absolute materials; null when no segment holds a
    // drawn shape. Its box is the reach about the origin, which its geometry holds.
    private static T? WorldGroup<T>(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, float reach, Func<int, bool, T> create) where T : SdfMeshBakeGroup {
        var instructions = program.Instructions;
        var owned = Owned(program: program);
        var drawn = false;

        for (var index = 0; (!drawn && (index < instructions.Count)); index++) {
            drawn = (!owned[index] && (instructions[index].Op == SdfOp.ShapeBlend) && !instructions[index].Detail);
        }

        if (!drawn) { return null; }

        var world = create(arg1: WorldSelector, arg2: false);

        for (var index = 0; (index < instructions.Count); index++) {
            if (!owned[index]) { world.Geometry.AppendInstruction(index: index, program: program, transforms: transforms); }
        }

        world.Geometry.AppendReach(reach: reach);
        world.Seal();
        world.High = new Vector3(value: reach);
        world.Low = new Vector3(value: -reach);

        return world;
    }
}
