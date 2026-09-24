using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldStampPool {
    private static int SelectPose(Registration live) => SelectPoseFrame(
        reads: live.Reads,
        references: live.PoseReferences,
        timelineFrames: live.PoseFrames
    );
    // A look's poses against its creation's timeline: each pose's state reference and its 1-based frame (0 = the
    // pose names no frame, which the validator refuses before a document reaches here).
    private static void ResolvePoses(IReadOnlyDictionary<string, string> poses, IReadOnlyList<FrameDocument?> frames, out string[] references, out int[] timelineFrames) {
        references = new string[poses.Count];
        timelineFrames = new int[poses.Count];

        var pose = 0;

        foreach (var (frameName, reference) in poses) {
            references[pose] = reference;

            for (var frame = 0; (frame < frames.Count); frame++) {
                if (string.Equals(a: frames[frame]?.Name, b: frameName, comparisonType: StringComparison.Ordinal)) {
                    timelineFrames[pose] = (frame + 1);

                    break;
                }
            }

            pose++;
        }
    }

    /// <summary>Selects the timeline frame a look's <c>poses</c> hold this frame: the first pose, in declaration
    /// order, whose state cell's stored truth reads nonzero through the state mirror, as a 1-based frame index; 0 when
    /// none holds.</summary>
    /// <param name="reads">The wearing body's reads of the state mirror, bound to the body <c>$body</c> names.</param>
    /// <param name="poses">The look's frame-name to state-reference map.</param>
    /// <param name="frames">The creation's timeline.</param>
    /// <returns>The holding pose's 1-based frame, or 0 when none holds.</returns>
    public static int SelectPoseFrame(WorldStateLease reads, IReadOnlyDictionary<string, string> poses, IReadOnlyList<FrameDocument?> frames) {
        ResolvePoses(
            frames: frames,
            poses: poses,
            references: out var references,
            timelineFrames: out var timelineFrames
        );

        return SelectPoseFrame(
            reads: reads,
            references: references,
            timelineFrames: timelineFrames
        );
    }

    private static int SelectPoseFrame(WorldStateLease reads, string[] references, int[] timelineFrames) {
        for (var pose = 0; (pose < references.Length); pose++) {
            if (
                (timelineFrames[pose] > 0) &&
                reads.TryNumber(
                reference: references[pose],
                truth: true,
                value: out var value
            ) &&
                (value != 0f)
            ) {
                return timelineFrames[pose];
            }
        }

        return 0;
    }
}
