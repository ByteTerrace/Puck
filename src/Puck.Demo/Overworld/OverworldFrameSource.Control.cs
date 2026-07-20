using System.Globalization;
using Puck.Maths;
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
    /// <c>InsertSelectedAndBoot</c> operation used by cabinet interaction). Returns a status line.</summary>
    string BootConsole(int index);

    /// <summary>Ejects console <paramref name="index"/> now — removes its cart and powers it off, the exact reverse of
    /// <see cref="BootConsole"/> (wraps <see cref="OverworldWorld.Eject"/>; the node eases the pane closed and tears the
    /// brick down next frame off the cleared booted mask, mirroring how a boot reconciles). Idempotent on an already-off
    /// cabinet. Returns a status line.</summary>
    /// <param name="index">The cabinet index to eject.</param>
    string EjectConsole(int index);

    /// <summary>Adds one scripted player (padless; drives no input). Returns a status line.</summary>
    string AddScriptedPlayer();

    /// <summary>Teleports player <paramref name="slot"/> to room-local XZ (<paramref name="x"/>, <paramref name="z"/>)
    /// — Y holds at the room's floor height. Wraps <see cref="OverworldWorld.MovePlayer"/> directly (a synchronous
    /// tick-boundary sim op, applied this same frame, exactly like <c>cart</c>/<c>win</c>). A destination blocked by
    /// the walk grid or a console/shelf keep-out REFUSES the move (the body is left where it was) rather than
    /// clamping into an arbitrary nearby cell. Returns a status line.</summary>
    /// <param name="slot">The player slot to move.</param>
    /// <param name="x">The room-local destination X.</param>
    /// <param name="z">The room-local destination Z.</param>
    string MovePlayer(int slot, float x, float z);

    /// <summary>Marks consoles <paramref name="first"/> and <paramref name="second"/> a linked serial-cable pair (the
    /// same <c>GamingBrickChildNode.TryLink</c> operation used by the Link verb). Applied by the
    /// node next frame (its outcome logs to stderr). Returns a status line.</summary>
    string LinkConsoles(int first, int second);

    /// <summary>Toggles a live STREAM of console <paramref name="index"/>'s completed serial transfers to stdout — each
    /// byte finally shifted through the link (on EITHER role, per <c>SerialComponent.TransferCompleted</c>) echoes a
    /// <c>[serial.watch i] 0x..</c> line the moment it lands. Re-issue on the same cabinet to STOP watching (the mirror of
    /// the <c>link</c> toggle). Pure host observation — the observer is never serialized and the simulation hash never
    /// learns it is read; the watch survives a cart swap / re-boot (it re-installs on the fresh machine) and simply
    /// pauses while the cabinet is ejected. Applied by the node next frame. Returns a status line.</summary>
    /// <param name="index">The cabinet index.</param>
    string WatchSerial(int index);

    /// <summary>Queues a one-shot frame capture to <paramref name="path"/> (written by the next produced frame, on the
    /// same in-flight readback the Right-Shoulder capture verb uses). Returns a status line.</summary>
    string RequestCaptureTo(string path);

    /// <summary>Drives a scripted joypad tape onto console <paramref name="index"/>: compiles <paramref name="script"/>
    /// and, applied by the node next frame, feeds the cabinet
    /// one frame-budget-sized segment per produced frame until the tape runs out — the deterministic equivalent of a
    /// player holding the pad. A linked cabinet drives its pair (its segment ticks set the shared budget); the node
    /// refuses an OWNED or unbooted cabinet and seats the cabinet back at the shared timeline head on completion. The
    /// compile is validated HERE (a bad script returns a usage line and queues nothing, never throws); the install +
    /// per-frame progress echo happen node-side on stdout. Returns a status line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="script">The button script, e.g. <c>up a*4</c> or <c>a - a - a</c>.</param>
    string PressConsole(int index, string script);

    /// <summary>Forces console <paramref name="index"/>'s game to its WIN — writes the cabinet's authored victory bytes
    /// (a meta cabinet's <c>share</c>, or a solo cabinet's <c>target</c>) into the top-16 SRAM region the meta gate
    /// reads, exactly as the game's own on-win hook would. The room's real XOR poll then sees it; when a whole meta
    /// group is won the editor reveal fires. The one control verb that lets a script drive "complete X games → the
    /// editor" end to end without real gameplay input. Applied by the node next frame. Returns a status line.</summary>
    string WinConsole(int index);

    /// <summary>Queues one machine-neutral time-travel operation for cabinet <paramref name="index"/> — the
    /// <c>rewind</c>/<c>rewind.status</c>/<c>runahead</c>/<c>fastforward</c> cabinet verbs. The bricks live on the node,
    /// so the operation is QUEUED here (the <c>press</c> pattern) and applied by the node next frame, on the render
    /// thread between step fan-outs, echoing the outcome (the landed frame counter, the ring status) to stdout.
    /// Primitive-typed on purpose. Returns an immediate status line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="op">The operation: <c>rewind</c> / <c>status</c> / <c>runahead</c> / <c>fastforward</c>.</param>
    /// <param name="argument">The operation's argument (may be empty).</param>
    string TimeTravel(int index, string op, string argument);

    /// <summary>Queues one SM83 debug operation for cabinet <paramref name="index"/> — the <c>hgb.*</c> verb family
    /// (peek/poke/regs/status/pause/resume/step/frame/until/snap/restore/watch/watch.clear/watch.list/dis). The bricks
    /// live on the node, so the operation is QUEUED here (the <c>press</c> pattern) and applied by the node next frame,
    /// on the render thread between step fan-outs, so single-stepping and inspection never race the fleet threads; the
    /// full (possibly multi-line) output echoes to stdout. Primitive-typed on purpose. Returns an immediate status
    /// line.</summary>
    /// <param name="index">The cabinet index.</param>
    /// <param name="op">The debug operation token (e.g. <c>peek</c>, <c>regs</c>, <c>watch</c>).</param>
    /// <param name="args">The operation's arguments (already stripped of the leading index).</param>
    string Debug(int index, string op, string[] args);

    /// <summary>A one-line world-state summary for scripted assertions: the sim state hash, the layout mode, the
    /// booted-console mask, the active player count, and the current frame / tick.</summary>
    string DescribeState();

    /// <summary>The current tick number and state hash, read together — the <c>hash.mark</c> verb's cheap
    /// divergence-bisection primitive. The same <see cref="OverworldWorld.StateHash"/> fold <see cref="DescribeState"/>
    /// already reads.</summary>
    (ulong Tick, ulong Hash) CurrentTickState();

    /// <summary>Registers (pass <see langword="null"/> to unregister) a callback fired once per simulated tick — the
    /// tick-transcript recorder's hook onto <see cref="OverworldWorld.OnTickAdvanced"/>. Observation-only.</summary>
    void SetTickObserver(Action<ulong, ulong, ulong>? observer);

    /// <summary>Sets console <paramref name="index"/>'s SELECTED cart type (0..12) — the scripted equivalent of pressing
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

    /// <summary>Shows or hides the diegetic UI action bar — the camera-rig-mounted mirror of the overlay binding bar
    /// (diegetic-UI Tier 2). DEFAULT ON, so the physical HUD appears the moment a revealed room does; the overlay bar
    /// stays regardless (they coexist by design this tier). Toggling rebuilds the program (the bar geometry appears or
    /// vanishes), an instruction change like a cabinet boot. Display-only this tier — the bar mirrors the overlay, it
    /// does not take input. Returns a status line.</summary>
    /// <param name="visible">True to show the diegetic bar, false to hide it (the overlay bar is unaffected).</param>
    string SetDiegeticUiVisible(bool visible);

    /// <summary>Sets the world render-scale quality TIER live by name, or (with a null/empty name) echoes the current
    /// tier and the full valid set. The tier scales the settled REVEALED room — the demo's most expensive view — trading
    /// softness for frame cost (~ scale²); the same knob the run-doc <c>revealedRenderScale</c> field feeds at boot. A
    /// valid name applies immediately (presentation-only — the director's settled slot-0 render scale, never simulation
    /// state); an unknown name is refused with the valid set. Returns a console status line.</summary>
    /// <param name="name">A tier name (<c>native</c>/<c>three-quarter</c>/<c>half</c>/<c>quarter</c>/<c>eighth</c>), or
    /// null/empty to echo the current tier + valid set.</param>
    string SetRenderScaleTier(string? name);

    /// <summary>Drives the REVEALED-ROOM fixed-camera perf-bench channel (see <see cref="RoomBenchScene"/>):
    /// <c>room.bench [n]</c> starts a run that pins the camera to a fixed deterministic pose over the ACTUAL live
    /// room content (no program swap, no simulation touch) and samples n produced frames (default ~300 with no
    /// argument); <c>room.bench abort</c> cancels a run in flight and releases the pin. A finished run's ONE summary
    /// line (median/min/p95 per render pass + frame + the live render-scale tier + the beam-pass DVFS canary) prints
    /// to stdout on its own — this call only arms/cancels the run and returns an immediate status line.</summary>
    /// <param name="args">Zero or one token: a positive sample-frame count, or <c>abort</c>.</param>
    string RoomBench(string[] args);
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
    // The cabinets a `serial.watch` verb queued to TOGGLE their completed-transfer stdout stream, accumulated (a batch
    // may toggle several) and drained in one pass by the node, which owns the bricks + their SerialComponent hooks.
    private readonly Queue<int> m_pendingSerialWatch = new();
    private string? m_pendingCapturePath;
    // The scripted-input tapes the `press` verb queued (a batch may script several cabinets), each the RAW script text —
    // the node owns the bricks + timeline and the joypad grammar, so it compiles + installs. Kept as a string here so
    // this composition point names no joypad/parse type; drained one per frame through primitive out-params.
    private readonly Queue<(int Index, string Script)> m_pendingPress = new();
    // The cabinets a `win` verb queued, as a bit per console index — accumulated (a batch may win several) and drained
    // in one pass by the node, which writes each cabinet's authored victory bytes into its SRAM region.
    private int m_pendingWinMask;
    // The machine-neutral time-travel operations the rewind/runahead/fastforward verbs queued (a batch may drive
    // several cabinets), drained ALL per frame by the node, which owns the bricks. Primitive tuples on purpose.
    private readonly Queue<(int Index, string Op, string Argument)> m_pendingTimeTravel = new();
    // The SM83 debug operations the hgb.* verbs queued (a batch may drive several cabinets), drained ALL per frame by
    // the node, which owns the bricks. Primitive tuples on purpose (the node names no new type at its coupling ceiling).
    private readonly Queue<(int Index, string Op, string[] Args)> m_pendingDebug = new();
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
    // The current world render-scale quality tier's canonical name (default native — the bit-exact full-resolution
    // path), tracked so the `render-scale` verb can echo it. The resolved float scale lives on the director
    // (RevealedRoomRenderScale); this is only the user-facing name. Presentation-only — the simulation never learns it.
    private string m_renderScaleTierName = WorldRenderScaleTiers.Name(tier: WorldRenderScaleTier.Native);

    /// <summary>The current world render-scale tier's canonical name (native/three-quarter/half/quarter/eighth) — the
    /// engine-bench <c>render.scale</c> feature switch's Get reads it (Set routes through <see cref="SetRenderScaleTier"/>,
    /// the same seam the <c>render-scale</c> verb and the run-doc <c>revealedRenderScale</c> field drive).</summary>
    public string RenderScaleTierName => m_renderScaleTierName;

    /// <summary>The scripted-player count console-mode roster reconciliation preserves (see the field remark).</summary>
    public int ScriptedPlayerCount => m_scriptedPlayers;

    /// <summary>Whether the EDITOR reveal (rung 3) has fired this session — the in-session authoring unlock the render
    /// node sets when the editor reveal applies (<c>ApplyEditorRevealIfRequested</c>). Session-only for now (reset at
    /// boot, re-earned each session); the persistence seam for a later cloud save (see the backing field's remark). It
    /// gates no authoring path — a later stage's diegetic entry reads it.</summary>
    public bool EditorRevealed {
        get => m_editorRevealed;
        set {
            // Arm the one-shot editor-reveal beat (Q35) on the false→true edge: the choreographed swell plays exactly
            // once as the workshop opens, never on a redundant re-set. See EditorRevealBeat in OverworldFrameSource.
            if (value && !m_editorRevealed) {
                m_editorRevealBeatTime = 0f;
            }

            m_editorRevealed = value;
        }
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

    /// <summary>Drains ONE queued <c>serial.watch</c> cabinet index (FIFO): the console whose completed-transfer stream
    /// the node should TOGGLE, or -1 (via the return) when none is queued. A batch may toggle several cabinets; the node
    /// drains one per frame, in order.</summary>
    /// <param name="index">The cabinet to toggle, when the return is true.</param>
    /// <returns>Whether a cabinet was dequeued.</returns>
    public bool TryConsumeSerialWatch(out int index) {
        if (!m_pendingSerialWatch.TryDequeue(result: out index)) {
            index = -1;

            return false;
        }

        return true;
    }

    /// <summary>Drains a pending <c>capture</c> path (one-shot): the PNG to write, or null when none is queued.</summary>
    public string? ConsumePendingCapture() {
        var path = m_pendingCapturePath;

        m_pendingCapturePath = null;

        return path;
    }

    /// <summary>Drains ONE queued <c>press</c> tape (FIFO) through primitive out-params, so the node compiles + installs
    /// it without this file naming a joypad type. Returns whether a tape was dequeued; when true,
    /// <paramref name="script"/> is the raw button script for cabinet <paramref name="index"/>. A batch may queue
    /// several tapes (to different cabinets); the node drains one per frame, in order.</summary>
    /// <param name="index">The target cabinet index.</param>
    /// <param name="script">The raw button script.</param>
    /// <returns>Whether a tape was dequeued.</returns>
    public bool TryConsumePress(out int index, out string script) {
        if (!m_pendingPress.TryDequeue(result: out var pending)) {
            index = -1;
            script = "";

            return false;
        }

        index = pending.Index;
        script = pending.Script;

        return true;
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

            Array.Copy(sourceArray: m_conditionExitSnapshot, destinationArray: exit, length: Math.Min(val1: m_conditionExitSnapshot.Length, val2: length));
            m_conditionExitSnapshot = exit;
        }

        if (m_conditionVictorySnapshot.Length != length) {
            var victory = new string[length];

            Array.Copy(sourceArray: m_conditionVictorySnapshot, destinationArray: victory, length: Math.Min(val1: m_conditionVictorySnapshot.Length, val2: length));
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
        // The same event also arms the view-stack transition from a console-framed view to a room-framed one,
        // independently of
        // ScreenLayoutDirector's own camera/rect easing below (see BeginRevealTransition's remarks).
        BeginRevealTransition();

        return "[reveal world: requested — the wall breaks next frame]";
    }

    /// <inheritdoc/>
    public string BootConsole(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[boot: no console {index} (there are {consoleCount})]");
        }

        if (m_world.IsBooted(consoleIndex: index)) {
            return Format(message: $"[boot: console {index} already booted]");
        }

        // Cabinets start empty, so a bare Boot would be refused; insert the cabinet's selected cart first. This mutates
        // m_world directly (the frame source
        // owns the world reference); the boot lands on the sim this same frame, before the frame's Advance.
        _ = m_world.InsertSelectedAndBoot(consoleIndex: index);

        return (m_world.IsBooted(consoleIndex: index)
            ? Format(message: $"[boot: console {index} booted]")
            : Format(message: $"[boot: console {index} refused the cart]"));
    }

    /// <inheritdoc/>
    public string EjectConsole(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[eject: no console {index} (there are {consoleCount})]");
        }

        if (!m_world.IsBooted(consoleIndex: index)) {
            return Format(message: $"[eject: console {index} already empty]");
        }

        // The exact reverse of BootConsole: pull the cart and clear the booted bit on m_world directly (the frame source
        // owns the world reference); the node reconciles the pane close + brick teardown next frame off the cleared mask.
        _ = m_world.Eject(consoleIndex: index);

        return Format(message: $"[eject: console {index} ejected]");
    }

    /// <inheritdoc/>
    public string WinConsole(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[win: no console {index} (there are {consoleCount})]");
        }

        // The node owns the bricks + the cartridge, so queue the cabinet (a bit in the mask) and let the node write its
        // authored victory bytes into the SRAM region next frame — the room's real meta XOR then sees it.
        m_pendingWinMask |= (1 << index);

        return Format(message: $"[win: console {index} queued — writing its victory bytes next frame (complete the group to open the workshop)]");
    }

    /// <inheritdoc/>
    public string SetCartType(int index, int type) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[cart: no console {index} (there are {consoleCount})]");
        }

        if ((type < 0) || (type >= OverworldWorld.CartTypeCount)) {
            return Format(message: $"[cart: type must be 0..{(OverworldWorld.CartTypeCount - 1)}]");
        }

        // Mutates m_world directly (the frame source owns the world reference); a booted cabinet live-swaps its running
        // cart to the new selection, exactly as the in-game Cycle press does when it lands on a running cabinet.
        var swapped = m_world.IsBooted(consoleIndex: index);

        _ = m_world.SetSelectedCartType(consoleIndex: index, cartType: type);

        return (swapped
            ? Format(message: $"[cart: console {index} live-swapped to cart {type}]")
            : Format(message: $"[cart: console {index} will insert cart {type} on boot]"));
    }

    /// <inheritdoc/>
    public string SetTerminalVisible(bool visible) {
        // Flip the presentation-only latch; the next CaptureFrame's rebuild trigger swaps the CRT slab for a dark box
        // (or back) and TickFeeds starts/stops the console feed's uploads. The Anchored ledger claim stays registered
        // either way (a permanent fixture) — this gates only the CRT's emission, never the claim.
        if (m_terminalVisible == visible) {
            return Format(message: $"[terminal: already {(visible ? "on" : "off")}]");
        }

        m_terminalVisible = visible;

        return Format(message: $"[terminal: {(visible ? "on" : "off")}]");
    }

    /// <inheritdoc/>
    public string SetDiegeticUiVisible(bool visible) {
        // Flip the presentation-only latch; the next CaptureFrame's rebuild trigger emits (or drops) the bar geometry.
        // The overlay bar is a separate surface and never touched by this — the two coexist by design this tier.
        if (m_diegeticUiVisible == visible) {
            return Format(message: $"[ui.diegetic: already {(visible ? "on" : "off")}]");
        }

        m_diegeticUiVisible = visible;

        return Format(message: $"[ui.diegetic: {(visible ? "on" : "off")}]");
    }

    /// <inheritdoc/>
    public string SetRenderScaleTier(string? name) {
        // No argument: echo the current tier and the full valid set (the knob is a fixed menu, not a free value).
        if (string.IsNullOrWhiteSpace(value: name)) {
            return Format(message: $"[render-scale: {m_renderScaleTierName} | tiers: {WorldRenderScaleTiers.ValidNames}]");
        }

        if (!WorldRenderScaleTiers.TryParse(name: name, tier: out var tier)) {
            return Format(message: $"[render-scale: unknown tier '{name}' — valid: {WorldRenderScaleTiers.ValidNames}]");
        }

        // Presentation-only: push the tier's resolved float onto the director (its settled slot-0 render scale). Native
        // restores the bit-exact full-resolution path (scale 1). Takes effect next Compose, on the settled room view.
        m_renderScaleTierName = WorldRenderScaleTiers.Name(tier: tier);
        m_director.RevealedRoomRenderScale = WorldRenderScaleTiers.Scale(tier: tier);

        var costPercent = (int)Math.Round(a: ((WorldRenderScaleTiers.Scale(tier: tier) * WorldRenderScaleTiers.Scale(tier: tier)) * 100f));

        return Format(message: $"[render-scale: {m_renderScaleTierName} — the revealed room renders at ~{costPercent}% of native cost]");
    }

    /// <inheritdoc/>
    public string ShowCondition(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[condition: no console {index} (there are {consoleCount})]");
        }

        // The node publishes the live per-cabinet descriptions each frame; a not-yet-published snapshot (frame 0 before
        // the first drain) reads as "(none)" — the boot values land the moment the node's first DrainControlRequests runs.
        var exit = (((index < m_conditionExitSnapshot.Length) ? m_conditionExitSnapshot[index] : null) ?? "(none)");
        var victory = (((index < m_conditionVictorySnapshot.Length) ? m_conditionVictorySnapshot[index] : null) ?? "(none)");

        return Format(message: $"[condition {index} exit={exit} victory={victory}]");
    }

    /// <inheritdoc/>
    public string SetExitConditionSpec(int index, string spec) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[condition.set: no console {index} (there are {consoleCount})]");
        }

        if (!ConditionSpecParser.TryParseExit(spec: spec, condition: out var condition)) {
            return "[condition.set: usage — condition.set <cabinet> exit <0xADDR><op><value> (addr 0xC000..0xDFFF; op == != >= <= > <; value 0..255)]";
        }

        m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: condition, ExitSet: true, Index: index, Victory: null, VictorySet: false));
        // Synchronous snapshot override so a same-batch condition.show already reflects the edit (it applies next frame).
        UpdateConditionSnapshot(index: index, exitChannel: true, description: $"{condition.Address}{condition.Op}{condition.Value}");
        // PERSISTENCE: mirror the re-forge onto the cabinet's world placement (a no-op when the cabinet was never
        // re-homed onto one — see WorldScene.SetCabinetExitCondition), so a subsequent world.save carries it.
        _ = m_worldScene.SetCabinetExitCondition(cabinetIndex: index, condition: condition);

        return Format(message: $"[condition.set: console {index} exit -> {condition.Address}{condition.Op}{condition.Value} (applied next frame)]");
    }

    /// <inheritdoc/>
    public string SetVictoryConditionSpec(int index, string mode, string[] tokens) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[condition.set: no console {index} (there are {consoleCount})]");
        }

        if (!ConditionSpecParser.TryParseVictory(mode: mode, tokens: tokens, condition: out var condition)) {
            return "[condition.set: usage — condition.set <cabinet> victory solo target=<guid> | victory meta target=<guid> share=<guid> [group=<g>]]";
        }

        m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: false, Index: index, Victory: condition, VictorySet: true));
        // Synchronous snapshot override — same format DescribeVictory publishes, so a same-batch condition.show matches.
        UpdateConditionSnapshot(index: index, exitChannel: false, description: $"{condition.Mode}(target={condition.Target}{((condition.Share is { } sh) ? $",share={sh}" : "")}{((condition.Group is { } gr) ? $",group={gr}" : "")})");
        // PERSISTENCE: mirror the re-forge onto the cabinet's world placement (see SetExitConditionSpec's remark).
        _ = m_worldScene.SetCabinetVictoryCondition(cabinetIndex: index, condition: condition);

        return Format(message: $"[condition.set: console {index} victory -> {condition.Mode} target={condition.Target}{((condition.Share is { } s) ? $" share={s}" : "")}{((condition.Group is { } g) ? $" group={g}" : "")} (applied next frame)]");
    }

    /// <inheritdoc/>
    public string ClearCondition(int index, string which) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[condition.clear: no console {index} (there are {consoleCount})]");
        }

        if (string.Equals(a: which, b: "exit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: true, Index: index, Victory: null, VictorySet: false));
            UpdateConditionSnapshot(index: index, exitChannel: true, description: "(none)");
            _ = m_worldScene.SetCabinetExitCondition(cabinetIndex: index, condition: null);

            return Format(message: $"[condition.clear: console {index} exit cleared (applied next frame)]");
        }

        if (string.Equals(a: which, b: "victory", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: false, Index: index, Victory: null, VictorySet: true));
            UpdateConditionSnapshot(index: index, exitChannel: false, description: "(none)");
            _ = m_worldScene.SetCabinetVictoryCondition(cabinetIndex: index, condition: null);

            return Format(message: $"[condition.clear: console {index} victory cleared (applied next frame)]");
        }

        return "[condition.clear: usage — condition.clear <cabinet> exit|victory]";
    }

    // Writes a synchronous snapshot override for a cabinet+channel, so a same-batch condition.show reflects a queued
    // edit before the node applies it. Grows the arrays defensively (they normally already match the cabinet count via
    // the node's first publish; a condition.set before that first publish still lands correctly).
    private void UpdateConditionSnapshot(int index, bool exitChannel, string description) {
        ref var array = ref (exitChannel ? ref m_conditionExitSnapshot : ref m_conditionVictorySnapshot);

        if (index >= array.Length) {
            var grown = new string[(index + 1)];

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
                return Format(message: $"[player.add: the room is full ({OverworldWorld.MaxPlayers} players)]");
            }

            m_scriptedPlayers++;

            return Format(message: $"[player.add: scripted player queued — {(1 + m_scriptedPlayers)} players next frame]");
        }

        var slot = m_world.AddPlayer(playerId: ScriptedPlayerId(index: (1 + m_scriptedPlayers)));

        if (slot < 0) {
            return Format(message: $"[player.add: the room is full ({OverworldWorld.MaxPlayers} players)]");
        }

        m_scriptedPlayers++;

        return Format(message: $"[player.add: player joined at slot {slot} ({m_world.ActivePlayerCount} active)]");
    }

    /// <inheritdoc/>
    public string MovePlayer(int slot, float x, float z) {
        var result = m_world.MovePlayer(slot: slot, x: FixedQ4816.FromDouble(value: x), z: FixedQ4816.FromDouble(value: z));

        return result switch {
            OverworldWorld.PlayerMoveResult.SlotOutOfRange => Format(message: $"[player.move: slot must be 0..{(OverworldWorld.MaxPlayers - 1)}]"),
            OverworldWorld.PlayerMoveResult.SlotEmpty => Format(message: $"[player.move: slot {slot} is empty — player.add first]"),
            OverworldWorld.PlayerMoveResult.Blocked => Format(message: $"[player.move: slot {slot} refused — ({x:0.00}, {z:0.00}) is blocked]"),
            _ => Format(message: $"[player.move: slot {slot} -> ({x:0.00}, {z:0.00})]"),
        };
    }

    /// <inheritdoc/>
    public string LinkConsoles(int first, int second) {
        var consoleCount = m_room.Consoles.Count;

        if ((first < 0) || (first >= consoleCount) || (second < 0) || (second >= consoleCount)) {
            return Format(message: $"[link: indices must be 0..{((consoleCount > 0) ? (consoleCount - 1) : 0)}]");
        }

        if (first == second) {
            return "[link: a console cannot link to itself]";
        }

        // The brick/choir bookkeeping + the ready-check live on the node, so queue the pair; the node applies it next
        // frame and logs the connect/refuse outcome to stderr (exactly as the debug Link verb does).
        m_pendingLink = (First: first, Second: second);

        return Format(message: $"[link: queued consoles {first}+{second} — connecting next frame]");
    }

    /// <inheritdoc/>
    public string WatchSerial(int index) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[serial.watch: no console {index} (there are {consoleCount})]");
        }

        // The bricks + their SerialComponent hooks live on the node, so queue the toggle; the node attaches/detaches the
        // completed-transfer observer next frame and echoes whether a live machine was there to attach to.
        m_pendingSerialWatch.Enqueue(item: index);

        return Format(message: $"[serial.watch: queued console {index} — toggling next frame]");
    }

    /// <inheritdoc/>
    public string PressConsole(int index, string script) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[press: no console {index} (there are {consoleCount})]");
        }

        if (string.IsNullOrWhiteSpace(value: script)) {
            return "[press: usage — press <cabinet> <keys[*frames][xrepeats]> ... (keys a b start select up down left right, '+'-joined; none/- releases; e.g. 'up a*4' or 'a - a - a')]";
        }

        // The node owns the bricks + timeline + the joypad grammar (this composition point stays free of joypad types),
        // so it compiles + installs the tape next frame — narrating a grammar error or an owned/unbooted refusal to
        // stderr, exactly as the `link` verb defers its ready-check.
        m_pendingPress.Enqueue(item: (Index: index, Script: script));

        return Format(message: $"[press: queued console {index} — applied next frame]");
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
        } catch (Exception exception) {
            return $"[capture: cannot write to '{path}' — {exception.Message}]";
        }

        m_pendingCapturePath = path;

        return Format(message: $"[capture: queued -> {path} (written next frame)]");
    }

    /// <inheritdoc/>
    public string TimeTravel(int index, string op, string argument) {
        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[{DescribeTimeTravelVerb(op: op)}: no console {index} (there are {consoleCount})]");
        }

        // The bricks live on the node, so queue the operation (the press pattern); the node applies it next frame —
        // on the render thread between step fan-outs, so the restore/replay never races the fleet threads — and echoes
        // the real outcome (the landed frame counter, the ring status) to stdout.
        m_pendingTimeTravel.Enqueue(item: (Index: index, Op: op, Argument: argument));

        return Format(message: $"[{DescribeTimeTravelVerb(op: op)}: queued console {index} — applied next frame]");
    }

    /// <summary>Drains ONE queued time-travel operation (FIFO) through primitive out-params. A batch may drive several
    /// cabinets; the node drains them all each frame, in order.</summary>
    /// <param name="index">The target cabinet index (already range-checked).</param>
    /// <param name="op">The operation token.</param>
    /// <param name="argument">The operation's argument (may be empty).</param>
    /// <returns>Whether an operation was dequeued.</returns>
    public bool TryConsumeTimeTravel(out int index, out string op, out string argument) {
        if (!m_pendingTimeTravel.TryDequeue(result: out var pending)) {
            index = -1;
            op = "";
            argument = "";

            return false;
        }

        index = pending.Index;
        op = pending.Op;
        argument = pending.Argument;

        return true;
    }

    /// <inheritdoc/>
    public string Debug(int index, string op, string[] args) {
        ArgumentNullException.ThrowIfNull(argument: args);

        var consoleCount = m_room.Consoles.Count;

        if ((index < 0) || (index >= consoleCount)) {
            return Format(message: $"[hgb.{op}: no console {index} (there are {consoleCount})]");
        }

        // The bricks live on the node, so queue the operation (the press pattern); the node applies it next frame — on
        // the render thread between step fan-outs, so single-stepping never races the fleet threads — and echoes the
        // real (possibly multi-line) output to stdout.
        m_pendingDebug.Enqueue(item: (Index: index, Op: op, Args: args));

        return Format(message: $"[hgb.{op}: queued console {index} — output next frame]");
    }

    /// <summary>Drains ONE queued debug operation (FIFO) through primitive out-params. A batch may drive several
    /// cabinets; the node drains them all each frame, in order.</summary>
    /// <param name="index">The target cabinet index (already range-checked).</param>
    /// <param name="op">The operation token.</param>
    /// <param name="args">The operation's arguments.</param>
    /// <returns>Whether an operation was dequeued.</returns>
    public bool TryConsumeDebug(out int index, out string op, out string[] args) {
        if (!m_pendingDebug.TryDequeue(result: out var pending)) {
            index = -1;
            op = "";
            args = [];

            return false;
        }

        index = pending.Index;
        op = pending.Op;
        args = pending.Args;

        return true;
    }

    // The user-facing verb name for a time-travel op token (status echoes under the verb the player typed).
    private static string DescribeTimeTravelVerb(string op) =>
        (string.Equals(a: op, b: "status", comparisonType: StringComparison.OrdinalIgnoreCase) ? "rewind.status" : op);

    /// <inheritdoc/>
    public string DescribeState() {
        // The world-state hash the "South logs the world state hash" debug bind computes — the one-line assertion
        // anchor. The rest is host-side presentation/roster state so a script can assert layout, boots, and players.
        // Both ladder rungs are exposed: `mode` reflects the WORLD reveal (rung 2), `editor` the EDITOR unlock (rung 3),
        // so an agent can assert each latch independently.
        var mode = (m_controlWorldRevealed ? "revealed" : (m_controlImmersed ? "immersed" : "standard"));
        var settled = (m_director.LayoutSettled ? "settled" : "easing");
        var editor = (m_editorRevealed ? "revealed" : "locked");

        return Format(message: $"[state hash=0x{m_world.StateHash():X16} mode={mode}/{settled} editor={editor} booted=0x{m_world.BootedMask:X} ({m_world.BootedCount}) players={m_world.ActivePlayerCount} frame={m_controlProducedFrames} tick={m_world.CurrentTick}]");
    }

    /// <inheritdoc/>
    public (ulong Tick, ulong Hash) CurrentTickState() => (m_world.CurrentTick, m_world.StateHash());

    /// <inheritdoc/>
    public void SetTickObserver(Action<ulong, ulong, ulong>? observer) => m_world.OnTickAdvanced = observer;

    // A deterministic padless-player guid for a scripted `player.add` (bare-room path), distinct from the pad-driven
    // roster's own DeterministicGuid so a scripted player never collides with a real pad slot's identity.
    private static Guid ScriptedPlayerId(int index) {
        var bytes = new byte[16];

        _ = BitConverter.TryWriteBytes(destination: bytes, value: 0x5C81_0000u | (uint)index);

        return new Guid(b: bytes);
    }
    private static string Format(FormattableString message) =>
        message.ToString(formatProvider: CultureInfo.InvariantCulture);
}
