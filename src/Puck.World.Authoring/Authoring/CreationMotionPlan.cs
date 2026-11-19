using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>Maps shapes to the root or to the nearest independently moving part.</summary>
public sealed class CreationMotionPlan {
    /// <summary>Initializes a new instance of the <see cref="CreationMotionPlan"/> class: a slot for the root and one for
    /// each shape with a writer of its own (<see cref="CreationRig.Owner"/>), and the render document with every seam the
    /// bake may not skin partitioned by owner.</summary>
    /// <param name="rig">The rig the creation is played by: a look's (<see cref="CreationRig.Follow"/>) when its part
    /// followers move shapes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The creation carries noise relief and a shape moves on its own.</exception>
    public CreationMotionPlan(CreationRig rig) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var document = rig.Document;
        var shapes = (document.Shapes ?? []);
        var parents = rig.Parents;

        ShapeSlots = new int[shapes.Count];
        var owners = new List<int>();
        var followed = new HashSet<int>();

        for (var index = 0; (index < shapes.Count); index++) {
            if ((rig.Writers(shape: index) & CreationRigWriters.Follower) != 0) { _ = followed.Add(item: index); }
        }
        for (var index = 0; (index < shapes.Count); index++) {
            var own = (rig.Owner(shape: index) == index);

            if (own) {
                owners.Add(item: index);
                ShapeSlots[index] = owners.Count;
            } else {
                ShapeSlots[index] = ((parents[index] < 0) ? 0 : ShapeSlots[parents[index]]);
            }
        }
        Owners = owners.ToArray();
        if ((document.Noise is not null) && (Owners.Length > 0)) {
            throw new ArgumentException(message: "Noise relief requires every shape to ride the creation root; independently moving parts cannot share its field.", paramName: nameof(document));
        }
        m_operands = CreationMotion.OperandShapes(keys: ShapeSlots, shapes: shapes);
        var rejected = CreationMotion.CreasedGroups(rig: rig);

        // A group an operand cuts bakes one piece a surface owner, each riding that owner's slot alone.
        rejected.UnionWith(other: CreationMotion.SharedOperandGroups(keys: ShapeSlots, operands: m_operands, shapes: shapes));
        // A follower has no finite authored envelope. Partition its entire group by owner as well; an operand follower
        // joins no seam.
        var followedSlots = followed.Where(predicate: index => !m_operands[index]).Select(selector: index => ShapeSlots[index]).ToHashSet();

        for (var index = 0; (index < shapes.Count); index++) {
            if (followedSlots.Contains(item: ShapeSlots[index]) && ((shapes[index].Group ?? 0) != 0)) { rejected.Add(item: shapes[index].Group!.Value); }
        }
        var partitions = new Dictionary<(int Group, int Slot), int>();
        var nextGroup = shapes.Select(selector: s => (s.Group ?? 0)).DefaultIfEmpty().Max();
        var renderShapes = shapes.ToArray();

        // An operand keeps its authored group, which then holds no surface: every partition of the group takes the operand
        // where it stands (Borrows).
        for (var index = 0; (index < shapes.Count); index++) {
            var group = (shapes[index].Group ?? 0);

            if (!rejected.Contains(item: group) || m_operands[index]) { continue; }
            var key = (group, ShapeSlots[index]);

            if (!partitions.TryGetValue(key: key, value: out var partition)) { partitions.Add(key: key, value: partition = ++nextGroup); }
            renderShapes[index] = shapes[index] with { Group = partition };
        }
        RenderDocument = document with { Shapes = renderShapes };
        m_partitions = partitions.ToDictionary(keySelector: static entry => entry.Value, elementSelector: static entry => entry.Key);
        m_groups = [.. shapes.Select(selector: static shape => (shape.Group ?? 0))];
        m_carves = [.. shapes.Select(selector: static shape => SdfBoundAlgebra.Carves(blend: (shape.Blend ?? SdfBlendOp.Union)))];
        m_surfaces = [];
        m_cut = [];
        for (var index = 0; (index < renderShapes.Length); index++) {
            var group = (renderShapes[index].Group ?? 0);

            if (group == 0) { continue; }
            if (m_operands[index]) {
                _ = m_cut.Add(item: group);
            } else {
                m_surfaces[group] = ShapeSlots[index];
            }
        }
        foreach (var (partition, (group, _)) in m_partitions) {
            if (m_cut.Contains(item: group)) { _ = m_cut.Add(item: partition); }
        }
    }

    // Each partition's render group: the authored group it came from and the owner slot whose shapes it holds.
    private readonly Dictionary<int, (int Group, int Slot)> m_partitions;
    private readonly int[] m_groups;
    private readonly bool[] m_carves;
    private readonly bool[] m_operands;
    // Each render group holding a surface, with the slot of its last surface's owner, which in a group an operand cuts is
    // its one surface owner (CreationMotion.SharedOperandGroups partitions any other); and the render groups an operand
    // cuts.
    private readonly Dictionary<int, int> m_surfaces;
    private readonly HashSet<int> m_cut;

    /// <summary>Returns whether a render group emits a shape it does not hold, and the slot the shape rides there: an
    /// operand (<see cref="IsOperand"/>) of the authored group the render group belongs to, riding its own slot, which
    /// the group's bake reads where it stands; or a carving shape (<see cref="SdfBoundAlgebra.Carves"/>) of another owner
    /// in the authored group the render group was partitioned from, emitted riding the partition's own slot, so the cut it
    /// made at rest stays cut on that owner's surfaces.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <param name="renderGroup">The render group (<see cref="RenderDocument"/>'s <see cref="ShapeDocument.Group"/>).</param>
    /// <param name="slot">The slot, relative to the root, the borrowed shape rides; zero when it is not borrowed.</param>
    /// <returns><see langword="true"/> when the render group borrows the shape.</returns>
    public bool Borrows(int shape, int renderGroup, out int slot) {
        slot = 0;
        if (!m_carves[shape] || !m_partitions.TryGetValue(key: renderGroup, value: out var partition)) { return false; }
        if ((m_groups[shape] != partition.Group) || (ShapeSlots[shape] == partition.Slot)) { return false; }
        slot = (m_operands[shape] ? ShapeSlots[shape] : partition.Slot);

        return true;
    }
    /// <summary>Returns whether any partition an owner's slot holds borrows a shape (<see cref="Borrows"/>).</summary>
    /// <param name="shape">The shape's index.</param>
    /// <param name="slot">The owner's slot, relative to the root.</param>
    /// <returns><see langword="true"/> when one of the owner's partitions borrows the shape.</returns>
    public bool BorrowedBy(int shape, int slot) =>
        (m_carves[shape] && (ShapeSlots[shape] != slot) && m_partitions.Values.Contains(value: (m_groups[shape], slot)));
    /// <summary>Returns whether a shape rides an operand owner (<see cref="CreationMotion.OperandShapes(CreationRig)"/>):
    /// a cutter moving alone, which the group it cuts reads where it stands.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns><see langword="true"/> for an operand shape.</returns>
    public bool IsOperand(int shape) => m_operands[shape];
    /// <summary>Returns whether a render group holds a surface: a group holding only operands, whose partitions take
    /// them, emits nothing of its own.</summary>
    /// <param name="renderGroup">The render group.</param>
    /// <returns><see langword="true"/> when a shape of the group is not an operand.</returns>
    public bool HoldsSurface(int renderGroup) => m_surfaces.ContainsKey(key: renderGroup);
    /// <summary>Returns the slot, relative to the root, a render group an operand cuts bakes in: the one owner whose
    /// surfaces it holds, so every vertex of the cut rides that slot and no skin member does.</summary>
    /// <param name="renderGroup">The render group.</param>
    /// <param name="slot">The surfaces' owner's slot.</param>
    /// <returns><see langword="true"/> when an operand cuts the group.</returns>
    public bool TryCutSlot(int renderGroup, out int slot) {
        slot = 0;
        return (m_cut.Contains(item: renderGroup) && m_surfaces.TryGetValue(key: renderGroup, value: out slot));
    }

    /// <summary>Gets each shape's slot relative to the root (zero means root).</summary>
    public int[] ShapeSlots { get; }
    /// <summary>Gets the independently moving shapes in slot order.</summary>
    public int[] Owners { get; }
    /// <summary>Gets the presentation document with rejected seams partitioned by motion owner.</summary>
    public CreationDocument RenderDocument { get; }
    /// <summary>Gets the root plus independently moving part count.</summary>
    public int SlotCount => (1 + Owners.Length);
}
