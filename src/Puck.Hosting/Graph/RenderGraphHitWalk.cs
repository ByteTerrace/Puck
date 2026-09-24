using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Hosting;

/// <summary>What one render-graph instance shows in its own world, as a hit walk reads it.</summary>
public interface IRenderGraphHitScene {
    /// <summary>Returns the source placements standing in an instance's world; a ray cast into the instance can hit
    /// any of their <see cref="SourcePlacement.Surface"/> faces.</summary>
    /// <param name="instance">The instance's index in its <see cref="RenderGraphInstanceSet"/>.</param>
    /// <returns>The placements, in a stable order that breaks a tie between equally distant faces.</returns>
    IReadOnlyList<SourceMapping> Placements(int instance);
    /// <summary>Finds the camera an instance renders from, whose rays a hit on its image continues along.</summary>
    /// <param name="instance">The instance's index in its <see cref="RenderGraphInstanceSet"/>.</param>
    /// <param name="camera">The camera when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the instance renders from a camera.</returns>
    bool TryCamera(int instance, out CameraSnapshot camera);
}
/// <summary>How a hit walk ended.</summary>
public enum RenderGraphHitEnd : byte {
    /// <summary>The last ray met no source placement: it ends on the world of the last instance it was cast into.</summary>
    World = 0,
    /// <summary>The last hit landed on a producer's pixels, such as an emulator or a captured window.</summary>
    Producer = 1,
    /// <summary>The last hit met a face but not its source: a bezel, a letterbox bar, or a warp that declares no
    /// inverse.</summary>
    OffSource = 2,
    /// <summary>The last hit landed on another instance's image, and continuing would pass the depth limit.</summary>
    DepthLimit = 3,
    /// <summary>The last hit landed on an image the showing instance does not read, or on an instance the set does not
    /// hold.</summary>
    Unread = 4,
    /// <summary>The last hit landed on an instance's image, and that instance renders from no camera.</summary>
    NoCamera = 5,
}
/// <summary>One hit of a walk: the placement a ray met inside an instance's world, and where on it.</summary>
/// <param name="Instance">The index of the instance whose world the ray was cast into.</param>
/// <param name="Placement">The index of the placement met, in <see cref="IRenderGraphHitScene.Placements"/>' list for
/// that instance.</param>
/// <param name="Mapping">The placement's mapping.</param>
/// <param name="Hit">Where the ray met it.</param>
public readonly record struct RenderGraphHitStep(int Instance, int Placement, SourceMapping Mapping, SourceHit Hit);
/// <summary>A hit walk's result: every hit in order, outermost first, and how the walk ended.</summary>
/// <param name="Steps">The hits, one per instance the walk passed through.</param>
/// <param name="End">How the walk ended.</param>
/// <param name="Instance">The index of the instance whose world the walk ended in.</param>
public sealed record RenderGraphHitPath(IReadOnlyList<RenderGraphHitStep> Steps, RenderGraphHitEnd End, int Instance);
/// <summary>Follows a hit through nested render-graph instances: a hit on a rendered source continues as a ray through
/// the producing instance's camera from the hit's source coordinate, into that instance's world, recursively, up to a
/// depth limit such as <see cref="RenderGraphInstanceSet.NestingDepth"/>. Every step maps in fixed point through
/// <see cref="SourceMapping.MapRay"/>, so the same set, scene and ray walk the same path on every run.</summary>
public static class RenderGraphHitWalk {
    private static bool Reads(RenderGraphInstanceSet set, int consumer, int producer) {
        foreach (var edge in set.Reads[consumer]) {
            if (edge.Producer == producer) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Walks a ray cast into an instance's world.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="scene">What each instance shows.</param>
    /// <param name="instance">The index of the instance the ray is cast into.</param>
    /// <param name="ray">The ray, in that instance's world.</param>
    /// <param name="maxDepth">The most times the walk continues into another instance; non-negative.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> or <paramref name="scene"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="instance"/> is not an index into the set, or
    /// <paramref name="maxDepth"/> is negative.</exception>
    public static RenderGraphHitPath Walk(RenderGraphInstanceSet set, IRenderGraphHitScene scene, int instance, SourceRay ray, int maxDepth) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: scene);
        ArgumentOutOfRangeException.ThrowIfNegative(value: instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: set.Instances.Count,
            value: instance
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: maxDepth);

        var steps = new List<RenderGraphHitStep>();
        var current = instance;
        var currentRay = ray;

        while (true) {
            var placements = scene.Placements(instance: current);
            var nearest = -1;
            var nearestHit = default(SourceHit);

            for (var index = 0; (index < placements.Count); index++) {
                if (placements[index].Placement is not SourcePlacement.Surface) {
                    continue;
                }

                var hit = placements[index].MapRay(ray: currentRay);

                if (
                    (hit.Outcome is not (SourceHitOutcome.NoIntersection or SourceHitOutcome.OutsidePlacement)) &&
                    ((nearest < 0) || (hit.Distance < nearestHit.Distance))
                ) {
                    nearest = index;
                    nearestHit = hit;
                }
            }

            if (nearest < 0) {
                return new RenderGraphHitPath(
                    End: RenderGraphHitEnd.World,
                    Instance: current,
                    Steps: steps
                );
            }

            var mapping = placements[nearest];

            steps.Add(item: new RenderGraphHitStep(
                Hit: nearestHit,
                Instance: current,
                Mapping: mapping,
                Placement: nearest
            ));

            var end = Continue(
                camera: out var camera,
                current: current,
                depth: (steps.Count - 1),
                hit: nearestHit,
                mapping: mapping,
                maxDepth: maxDepth,
                producer: out var producer,
                scene: scene,
                set: set
            );

            if (end is { } stop) {
                return new RenderGraphHitPath(
                    End: stop,
                    Instance: current,
                    Steps: steps
                );
            }

            current = producer;
            currentRay = SourceRay.Through(
                camera: camera,
                image: new FixedVector2(
                    X: (nearestHit.Coordinate.X / FixedQ4816.FromInteger(value: mapping.SourceWidth)),
                    Y: (nearestHit.Coordinate.Y / FixedQ4816.FromInteger(value: mapping.SourceHeight))
                )
            );
        }
    }
    /// <summary>Walks a point on the display: the topmost pane under it (the last in <paramref name="panes"/> whose face
    /// holds it), then, when that pane shows an instance, a ray through that instance's camera into its world.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="scene">What each instance shows.</param>
    /// <param name="panes">The panes the display shows, in drawing order; only <see cref="SourcePlacement.Pane"/>
    /// placements are read.</param>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <param name="maxDepth">The most times the walk continues into an instance's world, counting the continuation
    /// from the pane; non-negative.</param>
    /// <returns>The path, whose first step is the pane with an <see cref="RenderGraphHitStep.Instance"/> of -1, or no
    /// steps with <see cref="RenderGraphHitEnd.World"/> and an instance of -1 when no pane holds the point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="scene"/> or
    /// <paramref name="panes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or
    /// <paramref name="displayHeight"/> is not positive, or <paramref name="maxDepth"/> is negative.</exception>
    public static RenderGraphHitPath WalkDisplay(RenderGraphInstanceSet set, IRenderGraphHitScene scene, IReadOnlyList<SourceMapping> panes, FixedVector2 point, int displayWidth, int displayHeight, int maxDepth) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: scene);
        ArgumentNullException.ThrowIfNull(argument: panes);
        ArgumentOutOfRangeException.ThrowIfNegative(value: maxDepth);

        for (var index = (panes.Count - 1); (index >= 0); index--) {
            var mapping = panes[index];

            if (mapping.Placement is not SourcePlacement.Pane) {
                continue;
            }

            var hit = mapping.MapDisplayPoint(
                displayHeight: displayHeight,
                displayWidth: displayWidth,
                point: point
            );

            if (hit.Outcome == SourceHitOutcome.OutsidePlacement) {
                continue;
            }

            var step = new RenderGraphHitStep(
                Hit: hit,
                Instance: -1,
                Mapping: mapping,
                Placement: index
            );
            var end = Continue(
                camera: out var camera,
                current: -1,
                depth: 0,
                hit: hit,
                mapping: mapping,
                maxDepth: maxDepth,
                producer: out var producer,
                scene: scene,
                set: set
            );

            if (end is { } stop) {
                return new RenderGraphHitPath(
                    End: stop,
                    Instance: -1,
                    Steps: [step]
                );
            }

            var inner = Walk(
                instance: producer,
                maxDepth: (maxDepth - 1),
                ray: SourceRay.Through(
                    camera: camera,
                    image: new FixedVector2(
                        X: (hit.Coordinate.X / FixedQ4816.FromInteger(value: mapping.SourceWidth)),
                        Y: (hit.Coordinate.Y / FixedQ4816.FromInteger(value: mapping.SourceHeight))
                    )
                ),
                scene: scene,
                set: set
            );

            return inner with { Steps = [step, .. inner.Steps] };
        }

        return new RenderGraphHitPath(
            End: RenderGraphHitEnd.World,
            Instance: -1,
            Steps: []
        );
    }

    // Decides whether a hit continues into another instance. `current` is -1 for the display, which reads every
    // instance it shows directly.
    private static RenderGraphHitEnd? Continue(RenderGraphInstanceSet set, IRenderGraphHitScene scene, int current, int depth, int maxDepth, SourceMapping mapping, SourceHit hit, out int producer, out CameraSnapshot camera) {
        producer = -1;
        camera = default;

        if (!hit.IsOnSource) {
            return RenderGraphHitEnd.OffSource;
        }
        if (mapping.Source.Kind == SourceHandleKind.Producer) {
            return RenderGraphHitEnd.Producer;
        }

        producer = set.IndexOf(name: mapping.Source.Name);

        if (
            (producer < 0) ||
            ((current >= 0) && !Reads(
                consumer: current,
                producer: producer,
                set: set
            ))
        ) {
            return RenderGraphHitEnd.Unread;
        }
        if (depth >= maxDepth) {
            return RenderGraphHitEnd.DepthLimit;
        }
        if (!scene.TryCamera(
            camera: out camera,
            instance: producer
        )) {
            return RenderGraphHitEnd.NoCamera;
        }

        return null;
    }
}
