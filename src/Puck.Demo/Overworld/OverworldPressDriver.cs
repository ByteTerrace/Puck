using Puck.HumbleGamingBrick;

namespace Puck.Demo.Overworld;

/// <summary>
/// The scripted-input driver behind the <c>press &lt;i&gt; &lt;seq&gt;</c> console verb: it COMPILES a legible button
/// script into one joypad image per produced frame, and — installed as a cabinet's <see cref="JoypadSegmentFiller"/> —
/// feeds that cabinet EXACTLY ONE frame-budget-sized segment each frame, advancing a per-cabinet cursor, until the
/// script runs out. A linked PRIMARY's segment ticks then set the whole pair's step budget (see
/// <c>GamingBrickChildNode.ExecuteLinkedStep</c>), so a scripted pair advances in lockstep exactly like a
/// pad-driven one. All state is host-side presentation/driving bookkeeping — the deterministic simulation hash never
/// learns a script exists; the machine stays a pure function of (config, the consumed segment stream + cable bits).
///
/// <para>Owned as ONE field by <c>OverworldRenderNode</c> so that node — at its analyzer class-coupling ceiling —
/// names no new segment/parse type: this class holds the <see cref="JoypadSegment"/> construction, the grammar, and
/// the cursor arrays.</para>
///
/// <para>The grammar (case-insensitive, whitespace- and comma-separated steps): a step is
/// <c>KEYS[*FRAMES][xREPEATS]</c> — <c>KEYS</c> is a <c>+</c>-joined set of button names
/// (<c>a b start select up down left right</c>) or <c>none</c>/<c>-</c>/<c>.</c> for no button held; <c>*FRAMES</c>
/// holds those keys for that many produced frames (default 1); <c>xREPEATS</c> emits the (KEYS held FRAMES) block that
/// many times back-to-back (default 1). Examples: <c>up a*4</c> (tap Up, then hold A four frames);
/// <c>a - a - a</c> (mash A three times, releasing between); <c>start*2 a+b*8</c>.</para>
/// </summary>
internal sealed class OverworldPressDriver {
    // A script longer than this many produced frames is almost certainly a mistake (at ~60 fps it is minutes of
    // scripted input); refuse it rather than expand a runaway array. Generous enough for a whole trade-session tape.
    private const int MaxScriptFrames = 200_000;

    // Per-cabinet compiled scripts (null = no active script) and the next frame each has yet to emit. Indexed by
    // console index; sized to the room's cabinet count at construction.
    private readonly JoypadButtons[]?[] m_frames;
    private readonly int[] m_cursor;
    // This frame's tick budget, published by the render node before it steps the machines, so every emitted segment is
    // one whole frame-budget slice (the same size the classic per-frame path stages).
    private ulong m_deltaTicks;

    /// <summary>Initializes the driver for a room with <paramref name="consoleCount"/> cabinets.</summary>
    /// <param name="consoleCount">The number of cabinets (one script slot each).</param>
    public OverworldPressDriver(int consoleCount) {
        m_frames = new JoypadButtons[]?[consoleCount];
        m_cursor = new int[consoleCount];
    }

    /// <summary>Publishes the current frame's tick budget (called by the render node before stepping the machines), so a
    /// segment this driver emits is one frame-budget slice — matching the pair-budget contract a linked primary sets.</summary>
    /// <param name="ticks">This frame's fixed-step tick budget.</param>
    public void SetDeltaTicks(ulong ticks) =>
        m_deltaTicks = ticks;

    /// <summary>Whether cabinet <paramref name="console"/> currently has an active scripted-input tape.</summary>
    /// <param name="console">The cabinet index.</param>
    public bool IsScripted(int console) =>
        ((console >= 0) && (console < m_frames.Length) && (m_frames[console] is not null));

    /// <summary>Installs a compiled tape on a cabinet, resetting its cursor to the start. Replaces any tape already
    /// running on that cabinet.</summary>
    /// <param name="console">The cabinet index.</param>
    /// <param name="frames">The compiled per-frame joypad images (from <see cref="TryCompile"/>).</param>
    public void Install(int console, JoypadButtons[] frames) {
        m_frames[console] = frames;
        m_cursor[console] = 0;
    }

    /// <summary>Cancels a cabinet's active tape (a takeover, an eject, or a reboot pre-empts the script). A no-op when
    /// none is running.</summary>
    /// <param name="console">The cabinet index.</param>
    public void Cancel(int console) {
        if ((console >= 0) && (console < m_frames.Length)) {
            m_frames[console] = null;
        }
    }

    /// <summary>Gets the total frame length of a cabinet's active (or just-completed) tape — the number the completion
    /// echo reports. Zero when none.</summary>
    /// <param name="console">The cabinet index.</param>
    public int LengthOf(int console) =>
        (((console >= 0) && (console < m_frames.Length) && (m_frames[console] is { } frames)) ? frames.Length : 0);

    /// <summary>Reports (and CLEARS) whether a cabinet's tape has just reached its end — the render node calls this
    /// after the step pass to seat the cabinet at the timeline head, restore its prior source, and echo completion.
    /// True at most once per tape.</summary>
    /// <param name="console">The cabinet index.</param>
    /// <returns>Whether a tape completed this check.</returns>
    public bool TryTakeCompleted(int console) {
        if ((console < 0) || (console >= m_frames.Length) || (m_frames[console] is not { } frames) || (m_cursor[console] < frames.Length)) {
            return false;
        }

        m_frames[console] = null;

        return true;
    }

    /// <summary>Builds the <see cref="JoypadSegmentFiller"/> a cabinet consumes while scripted: it emits EXACTLY ONE
    /// frame-budget-sized segment per call (holding the cursor's current joypad image), advances the cursor, and
    /// returns 0 once the tape is exhausted (so the cabinet stops stepping and the node finalizes it). Returning at
    /// most one segment is what keeps a linked pair's per-side segment count at the required one.</summary>
    /// <param name="console">The cabinet index.</param>
    /// <returns>A filler bound to this cabinet's cursor.</returns>
    public JoypadSegmentFiller FillerFor(int console) =>
        destination => Fill(console: console, destination: destination);

    private int Fill(int console, Span<JoypadSegment> destination) {
        if ((destination.Length == 0) || (m_frames[console] is not { } frames)) {
            return 0;
        }

        var cursor = m_cursor[console];

        if (cursor >= frames.Length) {
            return 0;
        }

        destination[0] = new JoypadSegment(Ticks: m_deltaTicks, Buttons: frames[cursor]);
        m_cursor[console] = (cursor + 1);

        return 1;
    }

    /// <summary>Compiles a press script into one joypad image per produced frame (see the class remarks for the
    /// grammar). Returns whether it parsed; on failure <paramref name="error"/> carries a one-line reason and
    /// <paramref name="frames"/> is empty.</summary>
    /// <param name="script">The raw script text.</param>
    /// <param name="frames">The compiled per-frame joypad images.</param>
    /// <param name="error">A one-line failure reason, when it did not parse.</param>
    /// <returns>Whether the script compiled.</returns>
    public static bool TryCompile(string script, out JoypadButtons[] frames, out string error) {
        frames = [];
        error = "";

        if (string.IsNullOrWhiteSpace(value: script)) {
            error = "empty script";

            return false;
        }

        var steps = script.Split(separator: [' ', '\t', ',', ';'], options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compiled = new List<JoypadButtons>(capacity: steps.Length);

        foreach (var step in steps) {
            if (!TryCompileStep(step: step, into: compiled, error: out error)) {
                return false;
            }

            if (compiled.Count > MaxScriptFrames) {
                error = $"script exceeds {MaxScriptFrames} frames — shorten it or lower the repeat/hold counts";

                return false;
            }
        }

        if (compiled.Count == 0) {
            error = "no steps";

            return false;
        }

        frames = [.. compiled];

        return true;
    }

    // Compiles one KEYS[*FRAMES][xREPEATS] step, appending REPEATS blocks of FRAMES copies of the combined joypad image.
    private static bool TryCompileStep(string step, List<JoypadButtons> into, out string error) {
        error = "";

        var keysAndFrames = step;
        var repeats = 1;
        var frames = 1;

        // Trailing xREPEATS (no button name contains 'x', so a single 'x' is unambiguously the repeat delimiter).
        var xIndex = keysAndFrames.IndexOf(value: 'x', comparisonType: StringComparison.OrdinalIgnoreCase);

        if (xIndex >= 0) {
            if (!TryParsePositive(text: keysAndFrames[(xIndex + 1)..], value: out repeats)) {
                error = $"'{step}': expected a positive repeat count after 'x'";

                return false;
            }

            keysAndFrames = keysAndFrames[..xIndex];
        }

        // Optional *FRAMES hold count.
        var starIndex = keysAndFrames.IndexOf(value: '*');

        if (starIndex >= 0) {
            if (!TryParsePositive(text: keysAndFrames[(starIndex + 1)..], value: out frames)) {
                error = $"'{step}': expected a positive hold count after '*'";

                return false;
            }

            keysAndFrames = keysAndFrames[..starIndex];
        }

        if (!TryParseKeys(keys: keysAndFrames, buttons: out var buttons)) {
            error = $"'{step}': unknown button — use a/b/start/select/up/down/left/right joined by '+', or none/-";

            return false;
        }

        var total = (repeats * frames);

        for (var index = 0; (index < total); index++) {
            into.Add(item: buttons);
        }

        return true;
    }
    private static bool TryParsePositive(string text, out int value) =>
        (int.TryParse(s: text, result: out value) && (value >= 1));

    // Parses a '+'-joined button set (or none/-/.) into a JoypadButtons image. An empty/whitespace set is "no buttons".
    private static bool TryParseKeys(string keys, out JoypadButtons buttons) {
        buttons = JoypadButtons.None;

        if (string.IsNullOrWhiteSpace(value: keys)
            || string.Equals(a: keys, b: "none", comparisonType: StringComparison.OrdinalIgnoreCase)
            || (keys == "-") || (keys == ".")) {
            return true;
        }

        foreach (var name in keys.Split(separator: '+', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!TryParseButton(name: name, button: out var button)) {
                return false;
            }

            buttons |= button;
        }

        return true;
    }
    private static bool TryParseButton(string name, out JoypadButtons button) {
        switch (name.ToLowerInvariant()) {
            case "a": button = JoypadButtons.A; return true;
            case "b": button = JoypadButtons.B; return true;
            case "start": button = JoypadButtons.Start; return true;
            case "select": button = JoypadButtons.Select; return true;
            case "up": button = JoypadButtons.Up; return true;
            case "down": button = JoypadButtons.Down; return true;
            case "left": button = JoypadButtons.Left; return true;
            case "right": button = JoypadButtons.Right; return true;
            default: button = JoypadButtons.None; return false;
        }
    }
}
