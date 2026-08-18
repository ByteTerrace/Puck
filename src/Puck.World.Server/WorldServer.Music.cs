
namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The music.state echo: segment, any pending transition, and the most recent committed transition's tick/from/to
    // (none= before the first one). No music section authored reads as a fixed, honest "no music" line rather than an
    // empty/refused answer, matching audio.state's "device=unsupported" posture for an absent capability.
    private string DescribeMusicState() {
        if (
            (m_musicClock is not { } clock) ||
            (m_musicDirector is not { } director)
        ) {
            return "[music.state: none declared]";
        }

        return $"[music.state: segment={director.CurrentSegmentId} pending={(director.PendingSegmentId ?? "none")} elapsedTicks={clock.ElapsedTicks} transitions={director.TransitionCount} lastTick={(director.LastTransitionTick?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "none")} lastFrom={(director.LastTransitionFromSegmentId ?? "none")} lastTo={(director.LastTransitionToSegmentId ?? "none")}]";
    }
    // The judge.state echo: every declared window set, plus the last graded (body, judge) fact per body an
    // ActionEffect.Judge press has ever staged. A world with no bodies pressed and no judges declared reads "none
    // declared"; a declared-but-never-pressed world reads only the declared list, with no trailing grade fields.
    // Each graded pair contributes two SEPARATE name=value fields (never one compound token) — CanaryAssertions'
    // field extraction reads one whitespace-delimited name=value token at a time, the same convention music.state's
    // own segment/transitions/lastTick fields already follow.
    private string DescribeJudgeState() {
        if (m_judgeWindowSets.Count == 0) {
            return "[judge.state: none declared]";
        }

        var builder = new System.Text.StringBuilder(value: "[judge.state:");

        for (var index = 0; (index < m_judgeWindowSets.Count); index++) {
            var (id, windows) = m_judgeWindowSets[index];
            var windowText = string.Join(
                separator: ",",
                values: windows.Select(selector: window => $"{window.Grade}:{window.ToleranceTicks}")
            );

            _ = builder.Append(value: $"{((index == 0) ? " " : " | ")}{id} [{windowText}]");
        }

        if (m_judgeGrades.Count > 0) {
            foreach (var ((bodyIndex, judgeRef), (grade, tick)) in m_judgeGrades) {
                _ = builder.Append(value: $" body{bodyIndex}.{judgeRef}.grade={(grade ?? "miss")} body{bodyIndex}.{judgeRef}.tick={tick}");
            }
        }

        return builder.Append(value: ']').ToString();
    }
    // The (id, windows) row named judgeRef, or null when no declared judge row carries that name — the same lookup
    // ValidateEffect already proved succeeds for an admitted document, kept as a defensive miss rather than an
    // index-out-of-range here.
    private IReadOnlyList<Puck.Audio.Simulation.JudgeWindow>? FindJudgeWindows(string judgeRef) {
        foreach (var (id, windows) in m_judgeWindowSets) {
            if (string.Equals(
                a: id,
                b: judgeRef,
                comparisonType: StringComparison.Ordinal
            )) {
                return windows;
            }
        }

        return null;
    }
}
