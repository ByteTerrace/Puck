using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// Maps points, vectors and orientations along an adjacency projection's path (<see cref="WorldAdjacencyProjection.Path"/>)
/// between this world's frame and the neighbour's own frame, through <see cref="WorldFrameIsometry"/>: the one isometry a
/// crossing traveler's arrival, the neighbour's rendering, its contact field and the editor's edits across it all share.
/// Into the neighbour walks the path from its last stage to its first, each stage from its source frame to its neighbour
/// frame; into this world walks it from first to last the other way. Pure fixed point.
/// </summary>
public static class WorldAdjacencyPath {
    private interface IFrameStageMap {
        static abstract FixedVector3 Map(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination);
    }
    private readonly struct PointStageMap : IFrameStageMap {
        public static FixedVector3 Map(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination) => WorldFrameIsometry.MapPoint(
            destination: in destination,
            point: value,
            source: in source
        );
    }
    private readonly struct VectorStageMap : IFrameStageMap {
        public static FixedVector3 Map(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination) => WorldFrameIsometry.MapVector(
            destination: in destination,
            source: in source,
            value: value
        );
    }

    // The one path walk every point and vector mapping shares. TStage is a struct type argument so the per-stage
    // primitive stays a direct call, not a delegate.
    private static FixedVector3 MapAlong<TStage>(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path, bool intoNeighbour)
        where TStage : IFrameStageMap {
        if (intoNeighbour) {
            for (var stageIndex = (path.Count - 1); (stageIndex >= 0); stageIndex--) {
                var stage = path[stageIndex];

                value = TStage.Map(
                    destination: stage.Neighbour,
                    source: stage.Source,
                    value: value
                );
            }

            return value;
        }

        foreach (var stage in path) {
            value = TStage.Map(
                destination: stage.Source,
                source: stage.Neighbour,
                value: value
            );
        }

        return value;
    }

    /// <summary>Maps a point from this world's frame into the neighbour's own frame.</summary>
    /// <param name="value">The point, in this world's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The point, in the neighbour's frame.</returns>
    public static FixedVector3 MapPointIntoNeighbour(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) => MapAlong<PointStageMap>(
        intoNeighbour: true,
        path: path,
        value: value
    );
    /// <summary>Maps a point from the neighbour's own frame into this world's frame.</summary>
    /// <param name="value">The point, in the neighbour's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The point, in this world's frame.</returns>
    public static FixedVector3 MapPointIntoSource(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) => MapAlong<PointStageMap>(
        intoNeighbour: false,
        path: path,
        value: value
    );
    /// <summary>Maps a direction from this world's frame into the neighbour's own frame.</summary>
    /// <param name="value">The direction, in this world's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The direction, in the neighbour's frame.</returns>
    public static FixedVector3 MapVectorIntoNeighbour(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) => MapAlong<VectorStageMap>(
        intoNeighbour: true,
        path: path,
        value: value
    );
    /// <summary>Maps a direction from the neighbour's own frame into this world's frame.</summary>
    /// <param name="value">The direction, in the neighbour's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The direction, in this world's frame.</returns>
    public static FixedVector3 MapVectorIntoSource(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) => MapAlong<VectorStageMap>(
        intoNeighbour: false,
        path: path,
        value: value
    );
    /// <summary>Maps an orientation from this world's frame into the neighbour's own frame.</summary>
    /// <param name="value">The orientation, in this world's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The orientation, in the neighbour's frame.</returns>
    public static FixedQuaternion MapOrientationIntoNeighbour(FixedQuaternion value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        for (var stageIndex = (path.Count - 1); (stageIndex >= 0); stageIndex--) {
            var stage = path[stageIndex];

            value = (WorldFrameIsometry.Rotation(
                destination: stage.Neighbour,
                source: stage.Source
            ) * value).Normalize();
        }

        return value;
    }
    /// <summary>Maps an orientation from the neighbour's own frame into this world's frame.</summary>
    /// <param name="value">The orientation, in the neighbour's frame.</param>
    /// <param name="path">The projection's path.</param>
    /// <returns>The orientation, in this world's frame.</returns>
    public static FixedQuaternion MapOrientationIntoSource(FixedQuaternion value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        foreach (var stage in path) {
            value = (WorldFrameIsometry.Rotation(
                destination: stage.Source,
                source: stage.Neighbour
            ) * value).Normalize();
        }

        return value;
    }
}
