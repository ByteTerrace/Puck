using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>Maps shapes to the root or to the nearest independently moving part.</summary>
public sealed class CreationMotionPlan {
    /// <summary>Compiles actual motion owners, including timeline poses and part followers.</summary>
    public CreationMotionPlan(CreationDocument document, IReadOnlyDictionary<string, string>? partDynamics = null) {
        var shapes = (document.Shapes ?? []);
        var parents = new int[shapes.Count];

        CreationMotion.ResolveParents(parents: parents, shapes: shapes);
        ShapeSlots = new int[shapes.Count];
        var owners = new List<int>();
        var parts = ((partDynamics is null) ? null : CreationPartCompiler.Compile(document: document));
        var followed = new HashSet<int>();

        if (partDynamics is not null) {
            foreach (var name in partDynamics.Keys) {
                if (parts!.TryResolve(partId: name, transformSlot: out var index)) { followed.Add(item: index); }
            }
        }
        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var own = ((CreationMotion.Owner(document: document, index: index, parents: parents) == index) || followed.Contains(item: index) ||
                (document.Frames ?? []).Any(predicate: frame => (frame.Transforms ?? []).Any(predicate: pose => (pose.Id == shape.Id))));

            if (own) {
                owners.Add(item: index);
                ShapeSlots[index] = owners.Count;
            } else {
                ShapeSlots[index] = ((parents[index] < 0) ? 0 : ShapeSlots[parents[index]]);
            }
        }
        Owners = owners.ToArray();
        var rejected = CreationMotion.MovingBlends(document: document).Where(predicate: blend => !CreationMotion.Envelope(blend: blend, document: document).Admitted).Select(selector: blend => blend.Group).ToHashSet();
        // A follower has no finite authored envelope. Partition its entire group by owner as well.
        var followedSlots = followed.Select(selector: index => ShapeSlots[index]).ToHashSet();

        for (var index = 0; (index < shapes.Count); index++) {
            if (followedSlots.Contains(item: ShapeSlots[index]) && ((shapes[index].Group ?? 0) != 0)) { rejected.Add(item: shapes[index].Group!.Value); }
        }
        var partitions = new Dictionary<(int Group, int Slot), int>();
        var nextGroup = shapes.Select(selector: s => (s.Group ?? 0)).DefaultIfEmpty().Max();
        var renderShapes = shapes.ToArray();

        for (var index = 0; (index < shapes.Count); index++) {
            var group = (shapes[index].Group ?? 0);

            if (!rejected.Contains(item: group)) { continue; }
            var key = (group, ShapeSlots[index]);

            if (!partitions.TryGetValue(key: key, value: out var partition)) { partitions.Add(key: key, value: partition = ++nextGroup); }
            renderShapes[index] = shapes[index] with { Group = partition };
        }
        RenderDocument = document with { Shapes = renderShapes };
        m_partitions = partitions.ToDictionary(keySelector: static entry => entry.Value, elementSelector: static entry => entry.Key);
        m_groups = [.. shapes.Select(selector: static shape => (shape.Group ?? 0))];
        m_carves = [.. shapes.Select(selector: static shape => SdfBoundAlgebra.Carves(blend: (shape.Blend ?? SdfBlendOp.Union)))];
    }

    // Each partition's render group: the authored group it came from and the owner slot whose shapes it holds.
    private readonly Dictionary<int, (int Group, int Slot)> m_partitions;
    private readonly int[] m_groups;
    private readonly bool[] m_carves;

    /// <summary>Returns whether a render group borrows a shape it does not hold: a carving shape
    /// (<see cref="SdfBoundAlgebra.Carves"/>) of another owner in the authored group the render group was partitioned
    /// from. A partition emits such a carve where it stands in the group, riding the partition's own slot, so the cut it
    /// made at rest stays cut on that owner's surfaces; a carve that moves against them keeps its rest cut.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <param name="renderGroup">The render group (<see cref="RenderDocument"/>'s <see cref="ShapeDocument.Group"/>).</param>
    /// <param name="slot">The slot, relative to the root, the borrowed carve rides; zero when it is not borrowed.</param>
    /// <returns><see langword="true"/> when the render group borrows the shape.</returns>
    public bool Borrows(int shape, int renderGroup, out int slot) {
        slot = 0;
        if (!m_carves[shape] || !m_partitions.TryGetValue(key: renderGroup, value: out var partition)) { return false; }
        if ((m_groups[shape] != partition.Group) || (ShapeSlots[shape] == partition.Slot)) { return false; }
        slot = partition.Slot;

        return true;
    }
    /// <summary>Returns whether any partition an owner's slot holds borrows a shape (<see cref="Borrows"/>).</summary>
    /// <param name="shape">The shape's index.</param>
    /// <param name="slot">The owner's slot, relative to the root.</param>
    /// <returns><see langword="true"/> when one of the owner's partitions borrows the shape.</returns>
    public bool BorrowedBy(int shape, int slot) =>
        (m_carves[shape] && (ShapeSlots[shape] != slot) && m_partitions.Values.Contains(value: (m_groups[shape], slot)));

    /// <summary>Gets each shape's slot relative to the root (zero means root).</summary>
    public int[] ShapeSlots { get; }
    /// <summary>Gets the independently moving shapes in slot order.</summary>
    public int[] Owners { get; }
    /// <summary>Gets the presentation document with rejected seams partitioned by motion owner.</summary>
    public CreationDocument RenderDocument { get; }
    /// <summary>Gets the root plus independently moving part count.</summary>
    public int SlotCount => (1 + Owners.Length);
}
