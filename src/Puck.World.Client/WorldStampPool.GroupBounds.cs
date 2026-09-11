using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldStampPool {
    // All members must inherit the same rigid delta. Distances between their base positions are then invariant
    // under body motion, parent swings, and effector corrections. The first member's slot supplies the center.
    // Cuts/intersections cannot enlarge the solid; union members, smooth halos and outward field ops can.
    private static bool TryTightGroupRadius(Registration live, CreationDocument document, int fromIndex, int groupId, out float radius) {
        radius = 0f;
        var shapes = document.Shapes!;
        var anchor = shapes[fromIndex];
        if ((document.Chains is { Count: > 0 }) ||
            ((anchor.Blend ?? SdfBlendOp.Union) is not (SdfBlendOp.Union or SdfBlendOp.SmoothUnion))) {
            return false;
        }
        if (!live.PartParentsResolved) {
            ResolvePartParents(live: live, shapes: shapes);
        }
        var owner = GroupMotionOwner(live: live, document: document, index: fromIndex);
        var growth = 0f;
        var identity = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null);

        for (var index = fromIndex; index < Math.Min(shapes.Count, WorldPlacementPolicy.MaxAnimatedStampShapes); index++) {
            var shape = shapes[index];
            if ((shape.Group ?? 0) != groupId) {
                continue;
            }
            var blend = (shape.Blend ?? SdfBlendOp.Union);
            if ((shape.Type == SdfSolidPrimitive.Plane) || (shape.Domain is { Count: > 0 }) ||
                (blend is not (SdfBlendOp.Union or SdfBlendOp.SmoothUnion or SdfBlendOp.Subtraction or
                    SdfBlendOp.SmoothSubtraction or SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection)) ||
                (GroupMotionOwner(live: live, document: document, index: index) != owner)) {
                return false;
            }

            var separation = Vector3.Distance(anchor.Position.Value, shape.Position.Value);
            foreach (var frame in (document.Frames ?? [])) {
                var anchorPose = frame.Transforms.FirstOrDefault(pose => pose.Id == anchor.Id);
                var memberPose = frame.Transforms.FirstOrDefault(pose => pose.Id == shape.Id);
                // Changing geometry scale needs a larger envelope than the rest primitive below provides.
                if (((anchorPose is not null) && (anchorPose.Scale.Value != anchor.Scale.Value)) ||
                    ((memberPose is not null) && (memberPose.Scale.Value != shape.Scale.Value))) {
                    return false;
                }
                separation = MathF.Max(separation, Vector3.Distance(
                    anchorPose?.Position.Value ?? anchor.Position.Value,
                    memberPose?.Position.Value ?? shape.Position.Value));
            }

            // An outward field op acts on the whole running group, including when its shape is a cutter.
            growth += MathF.Max(0f, shape.Dilate ?? 0f) + MathF.Max(0f, shape.Onion ?? 0f);
            if (blend is SdfBlendOp.Union or SdfBlendOp.SmoothUnion) {
                var bound = CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: identity);
                radius = MathF.Max(radius, separation + bound.Radius);
                if (blend == SdfBlendOp.SmoothUnion) {
                    growth += MathF.Max(0f, shape.Smooth ?? 0f);
                }
            }
        }

        radius += growth;
        return float.IsFinite(radius) && (radius > 0f);
    }

    private static int GroupMotionOwner(Registration live, CreationDocument document, int index) {
        while (index >= 0) {
            var shape = document.Shapes![index];
            if ((shape.Swings is { Count: > 0 }) || (shape.Slides is { Count: > 0 })) {
                return index;
            }
            foreach (var effector in (document.Effectors ?? [])) {
                if ((shape.Name is { } name) && effector.Chain.Contains(name.Value)) {
                    return index;
                }
            }
            index = live.PartParent[index];
        }
        return -1;
    }
}
