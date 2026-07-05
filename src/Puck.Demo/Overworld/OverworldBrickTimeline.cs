using Puck.HumbleGamingBrick;

namespace Puck.Demo.Overworld;

/// <summary>
/// The overworld's shared brick-input timeline: one <see cref="JoypadSegment"/> per engine frame from the FIRST boot (the
/// epoch) onward, plus a per-console read cursor. Every powered brick consumes the SAME stream — a machine whose stand
/// booted at the epoch reads one segment per frame (live), while a machine booted later starts at segment zero and
/// drains several segments per frame (the brick node's fast-forward cap) until its cursor reaches the head. Because an
/// emulated machine is a pure function of its consumed input stream, two same-costume machines converge to
/// BIT-IDENTICAL state once both are caught up, no matter how far apart their stands booted — the lockstep the
/// carry-forward showcase promises. (The DMG costume still diverges from Color IN-GAME where the cartridge's mono code
/// path times scenes differently — that is hardware truth, not a timeline defect.)
/// </summary>
internal sealed class OverworldBrickTimeline {
    // Trim the consumed prefix once EVERY console is past this many segments, so an all-booted steady state stays
    // flat; a console that never boots pins the epoch (the stream then grows ~16 bytes per frame — immaterial).
    private const int TrimThreshold = 4096;

    private readonly List<JoypadSegment> m_segments = [];
    private readonly int[] m_cursors;

    /// <summary>Initializes the timeline for <paramref name="consoleCount"/> consoles (one cursor each).</summary>
    public OverworldBrickTimeline(int consoleCount) {
        m_cursors = new int[consoleCount];
    }

    /// <summary>Appends one engine frame's segment (called once per advanced frame, from the first boot onward).</summary>
    /// <param name="ticks">The frame's fixed-step tick budget.</param>
    /// <param name="buttons">The joypad image every brick holds for those ticks.</param>
    public void Append(ulong ticks, JoypadButtons buttons) {
        m_segments.Add(item: new JoypadSegment(Buttons: buttons, Ticks: ticks));
        TrimConsumedPrefix();
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length pending segments for a console and advances its
    /// cursor. A caught-up console gets exactly the frame's newly appended segment; a late-booted one gets a full
    /// buffer per frame until it converges.</summary>
    /// <param name="consoleIndex">The console whose cursor to read.</param>
    /// <param name="destination">The buffer to fill (its length is the fast-forward cap).</param>
    /// <returns>The number of segments written.</returns>
    public int Fill(int consoleIndex, Span<JoypadSegment> destination) {
        var cursor = m_cursors[consoleIndex];
        var count = Math.Min(m_segments.Count - cursor, destination.Length);

        for (var index = 0; (index < count); index++) {
            destination[index] = m_segments[cursor + index];
        }

        m_cursors[consoleIndex] = (cursor + count);

        return count;
    }

    /// <summary>Whether a console's cursor is at the stream head — it has consumed every appended segment. Two
    /// identical-config consoles both at the head hold BYTE-IDENTICAL machines (pure function of the same consumed
    /// stream), which is the choir park precondition.</summary>
    /// <param name="consoleIndex">The console whose cursor to test.</param>
    public bool IsAtHead(int consoleIndex) =>
        (m_cursors[consoleIndex] == m_segments.Count);

    /// <summary>Advances a console's cursor to the stream head without consuming — the bookkeeping for a PARKED choir
    /// follower: its machine mirrors the leader instead of stepping, but its cursor must track the head so the trim
    /// threshold and any future unpark (restore from the leader + resume here) stay correct.</summary>
    /// <param name="consoleIndex">The console whose cursor to advance.</param>
    public void SkipToHead(int consoleIndex) {
        m_cursors[consoleIndex] = m_segments.Count;
    }

    private void TrimConsumedPrefix() {
        var consumed = int.MaxValue;

        foreach (var cursor in m_cursors) {
            consumed = Math.Min(consumed, cursor);
        }

        if (consumed < TrimThreshold) {
            return;
        }

        m_segments.RemoveRange(index: 0, count: consumed);

        for (var index = 0; (index < m_cursors.Length); index++) {
            m_cursors[index] -= consumed;
        }
    }
}
