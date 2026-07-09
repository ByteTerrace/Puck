using System.Globalization;
using Puck.Scene;

namespace Puck.Demo.Overworld;

/// <summary>
/// The scripted-driving + observability control seam the <c>OverworldControlCommandModule</c> verbs
/// (<c>reveal</c>, <c>boot</c>, <c>player.add</c>/<c>join</c>, <c>link</c>, <c>state</c>, <c>capture</c>,
/// <c>step</c>/<c>settle</c>) drive over stdin/console. It is implemented by <see cref="OverworldFrameSource"/> —
/// NOT the render node — because the node sits AT its analyzer coupling ceiling and, as the
/// <c>world.*</c> module already documents, "its analyzer ceiling has NO headroom for a second interface; the frame
/// source is every authoring surface's composition point." The frame source holds the <see cref="OverworldWorld"/>
/// and the <see cref="ScreenLayoutDirector"/>, so it owns the observability reads (<c>state</c>, <c>LayoutSettled</c>,
/// the frame count) and the world-mutating verbs (<c>boot</c>, <c>player.add</c>) directly; the three verbs that need
/// node-only state (<c>reveal</c>'s handshake, <c>link</c>'s brick/choir bookkeeping, <c>capture</c>'s producer
/// readback) are QUEUED here and drained by the node each frame (<c>OverworldRenderNode.DrainControlRequests</c>),
/// mirroring the established <see cref="OverworldFrameSource.ConsumePendingWorldLoad"/> pattern. Every member is
/// PRIMITIVE-typed (bool / int / string / a small value tuple) so the node names no new type to drive it. All of it
/// is host-side presentation / roster / capture bookkeeping — the deterministic simulation hash never learns it
/// exists.
/// </summary>
internal interface IOverworldControlHost {
    /// <summary>The number of frames produced so far (one per window-loop iteration). The <c>state</c> verb reports
    /// it and the <c>step</c> gate counts against it.</summary>
    int ProducedFramesCount { get; }

    /// <summary>Whether the screen layout / reveal transitions are fully settled (no active easing) — the
    /// <c>settle</c> gate holds the command stream until this is true.</summary>
    bool LayoutSettled { get; }

    /// <summary>Forces a fourth-wall reveal now, exactly as a machine's win condition would (the same
    /// <c>RequestReveal</c> path the debug reveal used). <paramref name="kind"/> picks the ladder rung: <c>World</c>
    /// (rung 2 — the room becomes visible; requires an immersed run to reveal INTO) or <c>Editor</c> (rung 3 — the
    /// in-session authoring unlock, not immersion-gated). Returns a console status line.</summary>
    /// <param name="kind">Which reveal rung to force.</param>
    string RequestRevealNow(RevealKind kind);

    /// <summary>Boots console <paramref name="index"/> now — inserts its selected cart and powers it on (the same
    /// <c>InsertSelectedAndBoot</c> the <c>PUCK_OVERWORLD_DEBUG_BOOT</c> schedule drove). Returns a status line.</summary>
    string BootConsole(int index);

    /// <summary>Adds one scripted player (padless; drives no input). Wraps the scripted-player roster path
    /// (<c>PUCK_OVERWORLD_DEBUG_PLAYERS</c>). Returns a status line.</summary>
    string AddScriptedPlayer();

    /// <summary>Marks consoles <paramref name="first"/> and <paramref name="second"/> a linked serial-cable pair (the
    /// same <c>GamingBrickChildNode.TryLink</c> the Link verb / <c>PUCK_LINK_CABLE_PROBE</c> pair used). Applied by the
    /// node next frame (its outcome logs to stderr). Returns a status line.</summary>
    string LinkConsoles(int first, int second);

    /// <summary>Queues a one-shot frame capture to <paramref name="path"/> (written by the next produced frame, on the
    /// same in-flight readback the Right-Shoulder capture verb uses). Returns a status line.</summary>
    string RequestCaptureTo(string path);

    /// <summary>Forces console <paramref name="index"/>'s game to its WIN — writes the cabinet's authored victory bytes
    /// (a meta cabinet's <c>share</c>, or a solo cabinet's <c>target</c>) into the top-16 SRAM region the meta gate
    /// reads, exactly as the game's own on-win hook would. The room's real XOR poll then sees it; when a whole meta
    /// group is won the editor reveal fires. The one control verb that lets a script drive "complete X games → the
    /// editor" end to end without real gameplay input. Applied by the node next frame. Returns a status line.</summary>
    string WinConsole(int index);

    /// <summary>A one-line world-state summary for scripted assertions: the sim state hash, the layout mode, the
    /// booted-console mask, the active player count, and the current frame / tick.</summary>
    string DescribeState();

    /// <summary>Sets console <paramref name="index"/>'s SELECTED cart type (0..10) — the scripted equivalent of pressing
    /// Cycle at that cabinet until the wanted cart is chosen (the same <see cref="OverworldWorld.SetSelectedCartType"/>
    /// the Right-bumper cart-cycle drives). A booted cabinet live-swaps its running cart to the new selection. Wraps the
    /// world directly (the frame source owns it). Returns a status line.</summary>
    string SetCartType(int index, int type);

    /// <summary>Echoes cabinet <paramref name="index"/>'s current EXIT and VICTORY conditions (the live values, reflecting
    /// any prior <c>condition.set</c>/<c>condition.clear</c>) for scripted assertions — "the recursion" observability.
    /// Reads the node-published condition snapshot. Returns a status line.</summary>
    /// <param name="index">The cabinet index.</param>
    string ShowCondition(int index);

    /// <summary>Sets/replaces cabinet <paramref name="index"/>'s fourth-wall EXIT condition from a spec string
    /// (<c>0xADDR&lt;op&gt;value</c>, e.g. <c>0xC004&gt;=1</c>) — the live re-forge of the reveal gate. Parses + validates
    /// through <see cref="BrickExitCondition"/>; a bad spec returns a usage line and mutates nothing (never throws). The
    /// edit is QUEUED and applied by the node next frame (it owns the bricks + the console-source records). Returns a
    /// status line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="spec">The exit spec, e.g. <c>0xC004&gt;=1</c>.</param>
    string SetExitConditionSpec(int index, string spec);

    /// <summary>Sets/replaces cabinet <paramref name="index"/>'s 128-bit VICTORY condition — <c>solo target=&lt;guid&gt;</c>
    /// or <c>meta target=&lt;guid&gt; share=&lt;guid&gt; [group=&lt;g&gt;]</c>. Parses + validates through
    /// <see cref="BrickVictoryCondition"/>; a bad spec returns a usage line and mutates nothing (never throws). QUEUED and
    /// applied by the node next frame (which also re-seeds a meta share into the running machine and REBUILDS the
    /// room-level XOR watch). Returns a status line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="tokens">The victory tokens after the mode word (e.g. <c>[target=…, share=…, group=…]</c>).</param>
    /// <param name="mode">The victory shape word (<c>solo</c> or <c>meta</c>).</param>
    string SetVictoryConditionSpec(int index, string mode, string[] tokens);

    /// <summary>Clears cabinet <paramref name="index"/>'s <c>exit</c> or <c>victory</c> condition. QUEUED and applied by
    /// the node next frame (a victory clear also rebuilds the room-level watch). Returns a status line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="which">Either <c>exit</c> or <c>victory</c>.</param>
    string ClearCondition(int index, string which);

    /// <summary>Shows or hides the diegetic console terminal — the physical CRT in the revealed room that mirrors this
    /// console (a dev assist; the terminal is ON by default, a permanent fixture). Hiding darkens its CRT to a
    /// powered-off box (an instruction-neutral rebuild, like a cabinet unboot) and stops its feed uploading. Display-
    /// only this tier; input stays pad + this console + stdin. Returns a status line.</summary>
    /// <param name="visible">True to show (power on) the terminal, false to hide (power off) it.</param>
    string SetTerminalVisible(bool visible);
}

public sealed partial class OverworldFrameSource {
    // The node scalars the control host cannot see on its own — published each frame by the render node
    // (PublishControlSnapshot) so state / reveal-guarding read the true live values. m_controlWorldRevealed is the WORLD
    // rung (rung 2 — is the room visible); the EDITOR rung's latch (rung 3) lives directly on this frame source as the
    // in-session unlock, so it needs no republish.
    private int m_controlProducedFrames;
    private bool m_controlImmersed;
    private bool m_controlWorldRevealed;
    // The EDITOR reveal's OUTCOME (rung 3): an in-session authoring unlock, earned once per session by the editor
    // reveal (the meta-victory "complete X games" hook, or the `reveal editor` verb). It gates NO authoring path — a
    // later stage adds the diegetic player entry that reads it; today it is a state latch + a distinct narration only.
    //
    // PERSISTENCE SEAM: this is a clean, primitive, serializable-ready field on the composition point (the frame
    // source). It is SESSION-ONLY for now — reset to false at boot and re-earned each session (no save code wired) —
    // but it is where a cloud save WOULD serialize the unlock later.
    private bool m_editorRevealed;
    // How many SCRIPTED players `player.add` has requested beyond the permanent room player. Console mode's roster
    // reconcile (OverworldRenderNode.UpdateConsoleRoster) reads ScriptedPlayerCount and keeps 1 + this many slots
    // active regardless of pad count, so a padless scripted player joins and is not evicted; it drives no input.
    private int m_scriptedPlayers;
    // The node-only requests the verbs queued, drained by the node each frame (Consume*). The two reveals are separate
    // one-shot flags (they may coexist), link a pending pair, capture a pending path.
    private bool m_pendingReveal;
    private bool m_pendingEditorReveal;
    private (int First, int Second)? m_pendingLink;
    private string? m_pendingCapturePath;
    // The cabinets a `win` verb queued, as a bit per console index — accumulated (a batch may win several) and drained
    // in one pass by the node, which writes each cabinet's authored victory bytes into its SRAM region.
    private int m_pendingWinMask;
    // The per-cabinet CONDITION snapshot (condition.show reads it) and the pending condition EDITS the node applies (it
    // owns the bricks, the console-source records, and the MetaVictoryWatch — the frame source only PARSES the spec here,
    // into the Scene condition types the node already names). The snapshot is DUAL-WRITTEN: the node republishes it each
    // frame from the LIVE brick truth (PublishConditionSnapshot), AND a condition.set/clear updates the relevant cell
    // SYNCHRONOUSLY here — so a piped script's `condition.show` on the line right after a `condition.set` (both drained in
    // the SAME batch, before the node applies the queued edit next frame) already reflects the edit. Edits are a QUEUE
    // (not one slot): a batch may carry several edits to DIFFERENT cabinets, and the node drains one per frame.
    private string[] m_conditionExitSnapshot = [];
    private string[] m_conditionVictorySnapshot = [];
    private readonly Queue<PendingConditionEdit> m_pendingConditionEdits = new();

    /// <summary>The scripted-player count console-mode roster reconciliation preserves (see the field remark).</summary>
    public int ScriptedPlayerCount => m_scriptedPlayers;

    /// <summary>Whether the EDITOR reveal (rung 3) has fired this session — the in-session authoring unlock the render
    /// node sets when the editor reveal applies (<c>ApplyEditorRevealIfRequested</c>). Session-only for now (reset at
    /// boot, re-earned each session); the persistence seam for a later cloud save (see the backing field's remark). It
    /// gates no authoring path — a later stage's diegetic entry reads it.</summary>
    public bool EditorRevealed {
        get => m_editorRevealed;
        set => m_editorRevealed = value;
    }

    /// <inheritdoc/>
    public int ProducedFramesCount => m_controlProducedFrames;

    /// <inheritdoc/>
    public bool LayoutSettled => m_director.LayoutSettled;

    /// <summary>Publishes the render node's per-frame scalars to the control host (produced-frame count + the immersed
    /// / revealed layout flags) so the observability reads and the reveal guard see live values. Called by the node's
    /// DrainControlRequests each frame; primitive-typed on purpose (the node is at its analyzer coupling ceiling).</summary>
    /// <param name="producedFrames">Frames produced so far.</param>
    /// <param name="immersed">Whether this run is immersed (there is a wall to break).</param>
    /// <param name="worldRevealed">Whether the WORLD reveal (rung 2) has already fired (the room is visible).</param>
    public void PublishControlSnapshot(int producedFrames, bool immersed, bool worldRevealed) {
        m_controlProducedFrames = producedFrames;
        m_controlImmersed = immersed;
        m_controlWorldRevealed = worldRevealed;
    }

    /// <summary>Drains a pending <c>reveal world</c> request (one-shot): true exactly once after the verb ran.</summary>
    public bool ConsumePendingReveal() {
        if (!m_pendingReveal) {
            return false;
        }

        m_pendingReveal = false;

        return true;
    }

    /// <summary>Drains a pending <c>reveal editor</c> request (one-shot): true exactly once after the verb ran. Mirrors
    /// <see cref="ConsumePendingReveal"/> for the second ladder rung, so a script can FORCE the editor unlock.</summary>
    public bool ConsumePendingEditorReveal() {
        if (!m_pendingEditorReveal) {
            return false;
        }

        m_pendingEditorReveal = false;

        return true;
    }

    /// <summary>Drains a pending <c>link</c> pair (one-shot): the consoles to connect, or null when none is queued.</summary>
    public (int First, int Second)? ConsumePendingLink() {
        var pending = m_pendingLink;

        m_pendingLink = null;

        return pending;
    }

    /// <summary>Drains a pending <c>capture</c> path (one-shot): the PNG to write, or null when none is queued.</summary>
    public string? ConsumePendingCapture() {
        var path = m_pendingCapturePath;

        m_pendingCapturePath = null;

        return path;
    }

    /// <summary>Drains the pending <c>win</c> cabinet mask (one pass): a bit set per console index a <c>win</c> verb
    /// queued, or 0 when none. The node writes each cabinet's authored victory bytes into its SRAM region.</summary>
    public int ConsumePendingWins() {
        var mask = m_pendingWinMask;

        m_pendingWinMask = 0;

        return mask;
    }

    // A queued live condition edit (the condition.set/condition.clear verbs), applied by the node next frame. Carries the
    // target cabinet plus the ALREADY-PARSED replacement conditions (the Scene types the node already names) — the frame
    // source did the parse/validate so the node only mutates. ExitSet/VictorySet mark which channel(s) this edit touches;
    // a null Exit/Victory with its flag set means CLEAR that channel. Kept internal to the frame source (a record) and
    // drained through primitive out-params, so the NODE names no new type at its analyzer coupling ceiling.
    private sealed record PendingConditionEdit(int Index, bool ExitSet, BrickExitCondition? Exit, bool VictorySet, BrickVictoryCondition? Victory);

    /// <summary>Publishes the node's per-cabinet condition SNAPSHOT (one status string per cabinet, in console order)
    /// from the LIVE brick truth so <c>condition.show</c> reads the applied values. Called by the node each frame. A
    /// cabinet+channel with a still-QUEUED edit keeps its SYNCHRONOUS override (set by <c>condition.set/clear</c>) — the
    /// republish only overwrites cells with no pending edit — so a same-batch <c>show</c> right after a <c>set</c> keeps
    /// reflecting the edit until the node has actually applied it. String-typed on purpose (the node stays under its
    /// analyzer coupling ceiling).</summary>
    /// <param name="exit">Per-cabinet exit-condition descriptions (index = console index).</param>
    /// <param name="victory">Per-cabinet victory-condition descriptions (index = console index).</param>
    public void PublishConditionSnapshot(string[] exit, string[] victory) {
        // Size the display arrays to the cabinet count (once — it is fixed for a run; a condition.set that grew one
        // array before the first publish is preserved by ResizeSnapshot's copy). Then overwrite only cells with NO
        // queued edit — a queued cell keeps its synchronous override until the edit drains and the brick truth matches —
        // so the length branch can never revert an override in flight.
        ResizeSnapshot(length: exit.Length);

        for (var index = 0; (index < exit.Length); index++) {
            if (!HasQueuedEdit(index: index, exitChannel: true)) {
                m_conditionExitSnapshot[index] = exit[index];
            }

            if (!HasQueuedEdit(index: index, exitChannel: false)) {
                m_conditionVictorySnapshot[index] = victory[index];
            }
        }
    }

    // Grows/shrinks both snapshot arrays to the cabinet count, preserving any existing cells (a synchronous override a
    // condition.set wrote before the first publish survives). A no-op once sized.
    private void ResizeSnapshot(int length) {
        if (m_conditionExitSnapshot.Length != length) {
            var exit = new string[length];

            Array.Copy(sourceArray: m_conditionExitSnapshot, destinationArray: exit, length: Math.Min(m_conditionExitSnapshot.Length, length));
            m_conditionExitSnapshot = exit;
        }

        if (m_conditionVictorySnapshot.Length != length) {
            var victory = new string[length];

            Array.Copy(sourceArray: m_conditionVictorySnapshot, destinationArray: victory, length: Math.Min(m_conditionVictorySnapshot.Length, length));
            m_conditionVictorySnapshot = victory;
        }
    }

    // Whether a cabinet+channel has an edit still queued (so the republish must not revert its synchronous override).
    private bool HasQueuedEdit(int index, bool exitChannel) {
        foreach (var edit in m_pendingConditionEdits) {
            if ((edit.Index == index) && (exitChannel ? edit.ExitSet : edit.VictorySet)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drains ONE pending condition edit (FIFO) through PRIMITIVE + already-named out-params, so the node applies
    /// it without naming a new type (its analyzer coupling ceiling has no headroom). Returns whether an edit was dequeued;
    /// when true, <paramref name="exitSet"/>/<paramref name="victorySet"/> mark the touched channel and a null
    /// <paramref name="exit"/>/<paramref name="victory"/> with its flag set means CLEAR that channel. A batch may queue
    /// several edits (to different cabinets); the node drains one per frame, in order.</summary>
    /// <param name="index">The target cabinet index (already range-checked).</param>
    /// <param name="exitSet">Whether the edit sets/clears the exit condition.</param>
    /// <param name="exit">The replacement exit condition, or null to clear.</param>
    /// <param name="victorySet">Whether the edit sets/clears the victory condition.</param>
    /// <param name="victory">The replacement victory condition, or null to clear.</param>
    /// <returns>Whether an edit was dequeued.</returns>
    public bool TryConsumeConditionEdit(out int index, out bool exitSet, out BrickExitCondition? exit, out bool victorySet, out BrickVictoryCondition? victory) {
        if (!m_pendingConditionEdits.TryDequeue(result: out var edit)) {
            index = 0;
            exitSet = false;
            exit = null;
            victorySet = false;
            victory = null;

            return false;
        }

        index = edit.Index;
        exitSet = edit.ExitSet;
        exit = edit.Exit;
        victorySet = edit.VictorySet;
        victory = edit.Victory;

        return true;
    }

    /// <inheritdoc/>
    public string RequestRevealNow(RevealKind kind) {
        if (kind == RevealKind.Editor) {
            // The editor unlock is a session state, not a fourth-wall camera moment — it is NOT immersion-gated (a plain
            // bare-room run can still force it). Idempotent: once earned it stays earned this session.
            if (m_editorRevealed || m_pendingEditorReveal) {
                return "[reveal editor: already revealed]";
            }

            m_pendingEditorReveal = true;

            return "[reveal editor: requested — the workshop opens next frame]";
        }

        if (!m_controlImmersed) {
            return "[reveal world: unavailable — this run is not immersed (nothing to reveal into)]";
        }

        if (m_controlWorldRevealed || m_pendingReveal) {
            return "[reveal world: already revealed]";
        }

        m_pendingReveal = true;

        return "[reveal world: requested — the wall breaks next frame]";
    }

    /// <inheritdoc/>
    public string BootConsole(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[boot: no console {index} (there are {consoleCount})]");
        }

        if (m_world.IsBooted(consoleIndex: index)) {
            return Format($"[boot: console {index} already booted]");
        }

        // Cabinets start EMPTY, so a bare Boot would be refused — insert the cabinet's selected cart first, exactly as
        // the PUCK_OVERWORLD_DEBUG_BOOT schedule did per scheduled tick. Mutates m_world directly (the frame source
        // owns the world reference); the boot lands on the sim this same frame, before the frame's Advance.
        _ = m_world.InsertSelectedAndBoot(consoleIndex: index);

        return (m_world.IsBooted(consoleIndex: index)
            ? Format($"[boot: console {index} booted]")
            : Format($"[boot: console {index} refused the cart]"));
    }

    /// <inheritdoc/>
    public string WinConsole(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[win: no console {index} (there are {consoleCount})]");
        }

        // The node owns the bricks + the cartridge, so queue the cabinet (a bit in the mask) and let the node write its
        // authored victory bytes into the SRAM region next frame — the room's real meta XOR then sees it.
        m_pendingWinMask |= (1 << index);

        return Format($"[win: console {index} queued — writing its victory bytes next frame (complete the group to open the workshop)]");
    }

    /// <inheritdoc/>
    public string SetCartType(int index, int type) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[cart: no console {index} (there are {consoleCount})]");
        }

        if ((type < 0) || (type >= OverworldWorld.CartTypeCount)) {
            return Format($"[cart: type must be 0..{OverworldWorld.CartTypeCount - 1}]");
        }

        // Mutates m_world directly (the frame source owns the world reference); a booted cabinet live-swaps its running
        // cart to the new selection, exactly as the in-game Cycle press does when it lands on a running cabinet.
        var swapped = m_world.IsBooted(consoleIndex: index);

        _ = m_world.SetSelectedCartType(consoleIndex: index, cartType: type);

        return (swapped
            ? Format($"[cart: console {index} live-swapped to cart {type}]")
            : Format($"[cart: console {index} will insert cart {type} on boot]"));
    }

    /// <inheritdoc/>
    public string SetTerminalVisible(bool visible) {
        // Flip the presentation-only latch; the next CaptureFrame's rebuild trigger swaps the CRT slab for a dark box
        // (or back) and TickFeeds starts/stops the console feed's uploads. The Anchored ledger claim stays registered
        // either way (a permanent fixture) — this gates only the CRT's emission, never the claim.
        if (m_terminalVisible == visible) {
            return Format($"[terminal: already {(visible ? "on" : "off")}]");
        }

        m_terminalVisible = visible;

        return Format($"[terminal: {(visible ? "on" : "off")}]");
    }

    /// <inheritdoc/>
    public string ShowCondition(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[condition: no console {index} (there are {consoleCount})]");
        }

        // The node publishes the live per-cabinet descriptions each frame; a not-yet-published snapshot (frame 0 before
        // the first drain) reads as "(none)" — the boot values land the moment the node's first DrainControlRequests runs.
        var exit = (((index < m_conditionExitSnapshot.Length) ? m_conditionExitSnapshot[index] : null) ?? "(none)");
        var victory = (((index < m_conditionVictorySnapshot.Length) ? m_conditionVictorySnapshot[index] : null) ?? "(none)");

        return Format($"[condition {index} exit={exit} victory={victory}]");
    }

    /// <inheritdoc/>
    public string SetExitConditionSpec(int index, string spec) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[condition.set: no console {index} (there are {consoleCount})]");
        }

        if (!ConditionSpecParser.TryParseExit(spec: spec, condition: out var condition)) {
            return "[condition.set: usage — condition.set <cabinet> exit <0xADDR><op><value> (addr 0xC000..0xDFFF; op == != >= <= > <; value 0..255)]";
        }

        m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: condition, ExitSet: true, Index: index, Victory: null, VictorySet: false));
        // Synchronous snapshot override so a same-batch condition.show already reflects the edit (it applies next frame).
        UpdateConditionSnapshot(index: index, exitChannel: true, description: $"{condition.Address}{condition.Op}{condition.Value}");

        return Format($"[condition.set: console {index} exit -> {condition.Address}{condition.Op}{condition.Value} (applied next frame)]");
    }

    /// <inheritdoc/>
    public string SetVictoryConditionSpec(int index, string mode, string[] tokens) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[condition.set: no console {index} (there are {consoleCount})]");
        }

        if (!ConditionSpecParser.TryParseVictory(mode: mode, tokens: tokens, condition: out var condition)) {
            return "[condition.set: usage — condition.set <cabinet> victory solo target=<guid> | victory meta target=<guid> share=<guid> [group=<g>]]";
        }

        m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: false, Index: index, Victory: condition, VictorySet: true));
        // Synchronous snapshot override — same format DescribeVictory publishes, so a same-batch condition.show matches.
        UpdateConditionSnapshot(index: index, exitChannel: false, description: $"{condition.Mode}(target={condition.Target}{((condition.Share is { } sh) ? $",share={sh}" : "")}{((condition.Group is { } gr) ? $",group={gr}" : "")})");

        return Format($"[condition.set: console {index} victory -> {condition.Mode} target={condition.Target}{((condition.Share is { } s) ? $" share={s}" : "")}{((condition.Group is { } g) ? $" group={g}" : "")} (applied next frame)]");
    }

    /// <inheritdoc/>
    public string ClearCondition(int index, string which) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format($"[condition.clear: no console {index} (there are {consoleCount})]");
        }

        if (string.Equals(a: which, b: "exit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: true, Index: index, Victory: null, VictorySet: false));
            UpdateConditionSnapshot(index: index, exitChannel: true, description: "(none)");

            return Format($"[condition.clear: console {index} exit cleared (applied next frame)]");
        }

        if (string.Equals(a: which, b: "victory", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: false, Index: index, Victory: null, VictorySet: true));
            UpdateConditionSnapshot(index: index, exitChannel: false, description: "(none)");

            return Format($"[condition.clear: console {index} victory cleared (applied next frame)]");
        }

        return "[condition.clear: usage — condition.clear <cabinet> exit|victory]";
    }

    // Writes a synchronous snapshot override for a cabinet+channel, so a same-batch condition.show reflects a queued
    // edit before the node applies it. Grows the arrays defensively (they normally already match the cabinet count via
    // the node's first publish; a condition.set before that first publish still lands correctly).
    private void UpdateConditionSnapshot(int index, bool exitChannel, string description) {
        ref var array = ref (exitChannel ? ref m_conditionExitSnapshot : ref m_conditionVictorySnapshot);

        if (index >= array.Length) {
            var grown = new string[index + 1];

            Array.Copy(sourceArray: array, destinationArray: grown, length: array.Length);
            array = grown;
        }

        array[index] = description;
    }

    /// <inheritdoc/>
    public string AddScriptedPlayer() {
        // Console mode (the default demo, with cabinets): the pad-driven roster reconcile adds and PRESERVES
        // 1 + ScriptedPlayerCount slots regardless of pad count — bump the count and let the next frame's reconcile
        // seat the player. Bare-room mode (no cabinets) has no such reconcile, so add to the world directly.
        if (m_room.Consoles.Count > 0) {
            if ((1 + m_scriptedPlayers) >= OverworldWorld.MaxPlayers) {
                return Format($"[player.add: the room is full ({OverworldWorld.MaxPlayers} players)]");
            }

            m_scriptedPlayers++;

            return Format($"[player.add: scripted player queued — {1 + m_scriptedPlayers} players next frame]");
        }

        var slot = m_world.AddPlayer(playerId: ScriptedPlayerId(index: (1 + m_scriptedPlayers)));

        if (slot < 0) {
            return Format($"[player.add: the room is full ({OverworldWorld.MaxPlayers} players)]");
        }

        m_scriptedPlayers++;

        return Format($"[player.add: player joined at slot {slot} ({m_world.ActivePlayerCount} active)]");
    }

    /// <inheritdoc/>
    public string LinkConsoles(int first, int second) {
        var consoleCount = m_room.Consoles.Count;

        if ((first < 0) || (first >= consoleCount) || (second < 0) || (second >= consoleCount)) {
            return Format($"[link: indices must be 0..{((consoleCount > 0) ? (consoleCount - 1) : 0)}]");
        }

        if (first == second) {
            return "[link: a console cannot link to itself]";
        }

        // The brick/choir bookkeeping + the ready-check live on the node, so queue the pair; the node applies it next
        // frame and logs the connect/refuse outcome to stderr (exactly as the debug Link verb does).
        m_pendingLink = (First: first, Second: second);

        return Format($"[link: queued consoles {first}+{second} — connecting next frame]");
    }

    /// <inheritdoc/>
    public string RequestCaptureTo(string path) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            return "[capture: give a path — capture <png>]";
        }

        try {
            // The engine's capture writer does not create the parent directory (see SdfEngineNode.RequestCapture), so
            // the caller does — mirror the Right-Shoulder capture verb's mkdir. The node arms the producer next frame.
            var directory = Path.GetDirectoryName(path: path);

            if (!string.IsNullOrEmpty(value: directory)) {
                _ = Directory.CreateDirectory(path: directory);
            }
        }
        catch (Exception exception) {
            return $"[capture: cannot write to '{path}' — {exception.Message}]";
        }

        m_pendingCapturePath = path;

        return Format($"[capture: queued -> {path} (written next frame)]");
    }

    /// <inheritdoc/>
    public string DescribeState() {
        // The world-state hash the "South logs the world state hash" debug bind computes — the one-line assertion
        // anchor. The rest is host-side presentation/roster state so a script can assert layout, boots, and players.
        // Both ladder rungs are exposed: `mode` reflects the WORLD reveal (rung 2), `editor` the EDITOR unlock (rung 3),
        // so an agent can assert each latch independently.
        var mode = (m_controlWorldRevealed ? "revealed" : (m_controlImmersed ? "immersed" : "standard"));
        var settled = (m_director.LayoutSettled ? "settled" : "easing");
        var editor = (m_editorRevealed ? "revealed" : "locked");

        return Format($"[state hash=0x{m_world.StateHash():X16} mode={mode}/{settled} editor={editor} booted=0x{m_world.BootedMask:X} ({m_world.BootedCount}) players={m_world.ActivePlayerCount} frame={m_controlProducedFrames} tick={m_world.CurrentTick}]");
    }

    // A deterministic padless-player guid for a scripted `player.add` (bare-room path), distinct from the pad-driven
    // roster's own DeterministicGuid so a scripted player never collides with a real pad slot's identity.
    private static Guid ScriptedPlayerId(int index) {
        var bytes = new byte[16];

        _ = BitConverter.TryWriteBytes(destination: bytes, value: (0x5C81_0000u | (uint)index));

        return new Guid(b: bytes);
    }

    private static string Format(FormattableString message) =>
        message.ToString(formatProvider: CultureInfo.InvariantCulture);
}
