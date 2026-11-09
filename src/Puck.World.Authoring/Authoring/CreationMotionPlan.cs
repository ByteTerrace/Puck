namespace Puck.World.Authoring;

/// <summary>Maps shapes to the root or to the nearest independently moving part.</summary>
public sealed class CreationMotionPlan {
    /// <summary>Compiles actual motion owners, including timeline poses and part followers.</summary>
    public CreationMotionPlan(CreationDocument document, IReadOnlyDictionary<string, string>? partDynamics = null) {
        var shapes = (document.Shapes ?? []);
        var parents = new int[shapes.Count];

        CreationMotion.ResolveParents(shapes, parents);
        ShapeSlots = new int[shapes.Count];
        var owners = new List<int>();
        var parts = ((partDynamics is null) ? null : CreationPartCompiler.Compile(document));
        var followed = new HashSet<int>();

        if (partDynamics is not null) {
            foreach (var name in partDynamics.Keys) {
                if (parts!.TryResolve(name, out var index)) { followed.Add(index); }
            }
        }
        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var own = ((CreationMotion.Owner(document, parents, index) == index) || followed.Contains(index) ||
                (document.Frames ?? []).Any(frame => (frame.Transforms ?? []).Any(pose => (pose.Id == shape.Id))));

            if (own) {
                owners.Add(index);
                ShapeSlots[index] = owners.Count;
            } else {
                var owner = CreationMotion.Owner(document, parents, index);

                ShapeSlots[index] = ((owner < 0) ? 0 : ShapeSlots[owner]);
            }
        }
        Owners = owners.ToArray();
        var rejected = CreationMotion.MovingBlends(document).Where(blend => !CreationMotion.Envelope(document, blend).Admitted).Select(blend => blend.Group).ToHashSet();
        // A follower has no finite authored envelope. Partition its entire group by owner as well.
        foreach (var index in followed) { if ((shapes[index].Group ?? 0) != 0) { rejected.Add(shapes[index].Group!.Value); } }
        var partitions = new Dictionary<(int Group, int Slot), int>();
        var nextGroup = shapes.Select(s => s.Group ?? 0).DefaultIfEmpty().Max();
        var renderShapes = shapes.ToArray();
        for (var index = 0; index < shapes.Count; index++) {
            var group = (shapes[index].Group ?? 0);
            if (!rejected.Contains(group)) { continue; }
            var key = (group, ShapeSlots[index]);
            if (!partitions.TryGetValue(key, out var partition)) { partitions.Add(key, partition = ++nextGroup); }
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
