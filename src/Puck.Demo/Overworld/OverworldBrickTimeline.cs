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
    // The scripted-input driver behind the `press` verb (one tape slot per cabinet — host-side driving bookkeeping the
    // sim hash never sees). Owned HERE, the input-stream domain, because both classes that would otherwise name it
    // (OverworldRenderNode, OverworldFrameSource) sit at their exact CA1506 class-coupling ceilings — the node reaches
    // it only through the primitive-typed Press* forwarders below.
    private readonly OverworldPressDriver m_pressDriver;

    /// <summary>Initializes the timeline for <paramref name="consoleCount"/> consoles (one cursor each).</summary>
    public OverworldBrickTimeline(int consoleCount) {
        m_cursors = new int[consoleCount];
        m_pressDriver = new OverworldPressDriver(consoleCount: consoleCount);
    }

    /// <summary>Publishes this frame's tick budget to the press driver (each emitted tape segment is one frame-budget
    /// slice — the same size the classic per-frame path stages).</summary>
    /// <param name="ticks">The frame's fixed-step tick budget.</param>
    public void PressSetDeltaTicks(ulong ticks) =>
        m_pressDriver.SetDeltaTicks(ticks: ticks);

    /// <summary>Whether cabinet <paramref name="console"/> currently has an active scripted-input tape.</summary>
    /// <param name="console">The cabinet index.</param>
    public bool PressIsScripted(int console) =>
        m_pressDriver.IsScripted(console: console);

    /// <summary>Cancels a cabinet's active tape (takeover / cart-change pre-emption). A no-op when none runs.</summary>
    /// <param name="console">The cabinet index.</param>
    public void PressCancel(int console) =>
        m_pressDriver.Cancel(console: console);

    /// <summary>The total frame length of a cabinet's active (or just-completed) tape; zero when none.</summary>
    /// <param name="console">The cabinet index.</param>
    public int PressLengthOf(int console) =>
        m_pressDriver.LengthOf(console: console);

    /// <summary>Reports (and clears) whether a cabinet's tape just reached its end. True at most once per tape.</summary>
    /// <param name="console">The cabinet index.</param>
    public bool PressTryTakeCompleted(int console) =>
        m_pressDriver.TryTakeCompleted(console: console);

    /// <summary>Compiles a press script and installs it on a cabinet. On failure <paramref name="error"/> carries the
    /// one-line grammar reason.</summary>
    /// <param name="console">The cabinet index.</param>
    /// <param name="script">The raw script text.</param>
    /// <param name="frameCount">The compiled tape length, for the start echo.</param>
    /// <param name="error">The parse failure, when any.</param>
    /// <returns>Whether a tape was installed.</returns>
    public bool PressTryInstall(int console, string script, out int frameCount, out string error) {
        frameCount = 0;

        if (!OverworldPressDriver.TryCompile(script: script, frames: out var frames, error: out error)) {
            return false;
        }

        m_pressDriver.Install(console: console, frames: frames);
        frameCount = frames.Length;

        return true;
    }

    /// <summary>The segment filler a scripted cabinet consumes (exactly one frame-budget segment per frame).</summary>
    /// <param name="console">The cabinet index.</param>
    public JoypadSegmentFiller PressFillerFor(int console) =>
        m_pressDriver.FillerFor(console: console);

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
        var count = Math.Min(val1: (m_segments.Count - cursor), val2: destination.Length);

        for (var index = 0; (index < count); index++) {
            destination[index] = m_segments[(cursor + index)];
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
            consumed = Math.Min(val1: consumed, val2: cursor);
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
