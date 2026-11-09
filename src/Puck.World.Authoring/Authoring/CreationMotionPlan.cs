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
