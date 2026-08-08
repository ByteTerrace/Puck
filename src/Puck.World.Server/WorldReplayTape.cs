using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The tape's live state — what <see cref="WorldReplayTape"/> is doing with the running session.</summary>
public enum WorldReplayMode {
    /// <summary>Neither recording; the loopback taps are detached and the session runs untouched.</summary>
    Idle,

    /// <summary>The live session's per-tick server-input stream is being captured into the in-flight recording.</summary>
    Recording,
}

/// <summary>The outcome of <see cref="WorldReplayTape.StopRecording"/>. The tape file at <see cref="Path"/> is always
/// persisted before this is returned (or before <see cref="WorldReplayTape.StopRecording"/> throws, for any reason but
/// a genuine write failure) — the tape is evidence of the capture, and a verdict that will refuse it must never cost
/// the operator the recording. Exactly one of <see cref="Verdict"/>/<see cref="VerifyFault"/> is non-null.</summary>
/// <param name="Path">The path the tape was written to.</param>
/// <param name="Verdict">The comparison verdict, when the post-persist re-drive completed.</param>
/// <param name="VerifyFault">Why the post-persist re-drive itself could not complete — e.g. the mount pin refusing a
/// world whose addon set moved since record-start (see <see cref="WorldReplaySnapshot.Drive"/>'s mount-pin remarks) —
/// never a persistence failure, because the tape above is already on disk by the time this can be non-null.</param>
public readonly record struct WorldReplayStopResult(string Path, WorldReplayVerdict? Verdict, string? VerifyFault);

/// <summary>
/// The record side of Puck.World's true deterministic replay. While armed it captures the live session's authoritative
/// SERVER-input stream — the intent submissions plus the ordered authority inputs (commands, grants, revokes) that reach
/// the <see cref="LoopbackTransport"/> each tick — plus the record-start world definition, active seats, and MOUNTED
/// ADDON RECEIPTS, into a self-contained
/// <see cref="WorldReplaySnapshot"/>. It also samples the LIVE population's tail pose hash (the state the running world
/// actually reached at the last recorded tick) and persists it as the recording's reference hash. A saved recording
/// rehydrates a FRESH world from its captured starting state and re-drives the captured stream through it
/// (<see cref="WorldReplaySnapshot.Drive"/>); the replayed tail is compared against the LIVE reference, so a MATCH is a
/// genuine live-vs-replay fidelity proof rather than a re-drive compared against another re-drive of the same stream.
/// </summary>
/// <remarks>
/// <para>This REPLACES the earlier live input re-injection lever. There is no live-playback mode: a replay is an OFFLINE
/// recomputation over an isolated shadow world (<see cref="WorldReplaySnapshot.Drive"/>) that never touches the running
/// session, so live seat input is structurally excluded from a playback rather than merely advised against, and the
/// verdict is readable synchronously over the pipe the instant it completes (no per-tick drain to wait out).</para>
/// <para>HONEST SCOPE. The captured starting state is the SERVER simulation only — definition + active seats + the
/// per-tick authority/intent stream. The rehydrated starting body state is the deterministic BOOT IMAGE of the captured
/// definition (a fresh world reconstructs it exactly), not a per-body pose snapshot, and its starting GRANT table is
/// likewise the captured definition's own document grants plus the permissive seed, not the live table as it stood at
/// record-start. A MATCH is therefore a fidelity proof precisely when the live session was still AT that boot state at
/// record-start (a boot-anchored capture); a capture armed after the session has already diverged — a body moved, or a
/// grant typed before the tape was armed — faithfully re-drives its stream but from a boot start, so
/// <see cref="Verify"/> honestly reports MISMATCH rather than a false MATCH. Full record-start rehydration (so a
/// mid-session capture also MATCHes) is the identified next lever; the live-tail reference hash is the backstop that
/// keeps the verdict honest until it lands. Screen machines, their pixels, cameras, overlays, and audio are
/// PRESENTATION and are excluded (see <see cref="WorldReplaySnapshot"/>).</para>
/// <para>THE MOUNT PIN. A guest's driving never crosses the loopback, so it is never captured; the replay RE-RUNS the
/// document's guests instead. That only stays honest while the modules it re-mounts are the ones that ran live, so
/// record-start also copies the live server's mount receipts (name, module content hash, fuel) into the recording,
/// and <see cref="WorldReplaySnapshot.Drive"/> refuses — before its first tick — a fresh world whose own mount
/// disagrees. The receipts come from the INSTANCES that mounted, never from the document's addon rows: a row carries
/// the pin an author wrote, and the tape needs the identity of what actually loaded under it.</para>
/// <para>Single-threaded on the launcher's window-pump thread: the <c>replay.*</c> verbs are Immediate (they run inline
/// during the command pump's drain) and the taps + <see cref="NoteTick"/> run inside the fixed-step
/// <c>Puck.World.WorldSimulation.Step</c> — both on that one thread, so no locking is needed. The <c>replay.*</c> verbs are
/// NOT folded into the captured stream (they never reach the loopback), so a recording never records the recording
/// verbs themselves; physical device input, the <c>world.grant</c>/<c>world.revoke</c> verbs, and Simulation-routed
/// world verbs DO reach the loopback and are captured.</para>
/// </remarks>
public sealed class WorldReplayTape {
    private const string Extension = ".puckreplay";

    private readonly WorldServer m_liveServer;
    private readonly WorldOwnedWorlds m_profiles;
    private readonly LoopbackTransport m_transport;
    private readonly IReadOnlyList<IScreenMachineEngine> m_engines;
    private WorldReplayMode m_mode;
    private string? m_recordName;
    private byte[]? m_definitionJson;
    // The guests MOUNTED at record-start, copied out of the live server's runtime. Read once here rather than at stop:
    // the pin must describe the world that produced the recorded stream, and mounting is a boot-time act that a later
    // read could only re-report, never re-witness.
    private List<WorldAddonReceipt>? m_mountedAddons;
    private List<WorldReplaySeat>? m_seats;
    private List<WorldReplayTickInput>? m_ticks;
    // The LIVE session's per-tick pose hash trace — one entry appended each NoteTick (after that tick's server step), so
    // the final entry is the true live tail and the whole array is the trajectory. Persisted as the recording's
    // RecordedHashes, so a replay's fresh re-drive is compared against the ACTUAL live session tick by tick, not against
    // another re-drive of itself and not only at the end.
    private readonly List<ulong> m_liveHashes = [];
    // The current tick's accumulating input, rotated into m_ticks at each NoteTick. ONE authority list, not one per
    // kind: a command and a grant that crossed the link in a given order must replay in that order, and parallel lists
    // have no relative order left to preserve.
    private List<WorldReplayEntry> m_currentAuthority = new();
    private List<IntentSubmission> m_currentIntents = new();

    /// <summary>Initializes the tape over the live server it snapshots the starting state from, the profile catalog a
    /// replay's seats re-resolve against, and the loopback whose per-tick submissions it taps.</summary>
    /// <param name="liveServer">The authoritative live server (read at record-start for the definition and active seats).</param>
    /// <param name="profiles">The profile catalog (handed to a replay's fresh world for seat re-resolution).</param>
    /// <param name="transport">The client→server loopback whose intent/command submissions the tape captures.</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) — handed to
    /// <see cref="WorldReplaySnapshot.Drive"/> so the offline re-drive's own <see cref="Server.WorldMachineHost"/>
    /// boots against the SAME engine set the live session ran under.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldReplayTape(WorldServer liveServer, WorldOwnedWorlds profiles, LoopbackTransport transport, IEnumerable<IScreenMachineEngine> engines) {
        ArgumentNullException.ThrowIfNull(argument: liveServer);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: transport);
        ArgumentNullException.ThrowIfNull(argument: engines);

        m_liveServer = liveServer;
        m_profiles = profiles;
        m_transport = transport;
        m_engines = [.. engines];
    }

    /// <summary>Gets the tape's current mode.</summary>
    public WorldReplayMode Mode => m_mode;

    /// <summary>Gets the ticks captured so far in the active recording.</summary>
    public int TickCount => (m_ticks?.Count ?? 0);

    /// <summary>Gets the name the active recording will persist under.</summary>
    public string? Name => m_recordName;

    /// <summary>Returns the <c>Replays/</c> directory (created on first use), beside World's other local data.</summary>
    public static string Directory() {
        var directory = Path.Combine(path1: WorldStateRoot.Resolve(), path2: "Replays");

        _ = System.IO.Directory.CreateDirectory(path: directory);

        return directory;
    }

    /// <summary>Validates a replay name: non-empty and free of path-navigation characters — a console verb argument is
    /// untrusted, so this keeps every resolved path under <see cref="Directory"/>.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is safe to use as a filename stem.</returns>
    public static bool IsValidName(string name) {
        return (!string.IsNullOrWhiteSpace(value: name) &&
            (name.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) < 0) &&
            !name.Contains(value: '.') &&
            !name.Contains(value: '/') &&
            !name.Contains(value: '\\'));
    }

    /// <summary>Returns the on-disk path a valid <paramref name="name"/> resolves to.</summary>
    /// <param name="name">The replay's name.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string name) {
        return Path.Combine(path1: Directory(), path2: (name + Extension));
    }

    /// <summary>Returns the names of every persisted replay.</summary>
    /// <returns>The saved names, sorted; empty when none exist.</returns>
    public static IReadOnlyList<string> List() {
        var directory = Directory();
        var names = new List<string>();

        foreach (var path in System.IO.Directory.EnumerateFiles(path: directory, searchPattern: $"*{Extension}")) {
            names.Add(item: Path.GetFileNameWithoutExtension(path: path));
        }

        names.Sort(comparer: StringComparer.OrdinalIgnoreCase);

        return names;
    }

    /// <summary>Arms recording — REFUSING first when arming would be dishonest (Phase-3 plan AXIS 1's boot-anchored
    /// contract): <see cref="Server.WorldServer.AnyAddonEverPumped"/> refuses if ANY addon has already had an
    /// admitted execution attempted before this call, because offline replay creates fresh guests at sim-counter
    /// zero — a guest's accumulated memory/tick state from before recording began is exactly what that fresh
    /// re-drive can never re-establish. L6's interim refuse-while-verb-mask-live gate was removed with P4-lean:
    /// <see cref="Protocol.WorldGrant.KindMask"/> (and its <see cref="Protocol.WorldGrant.WriteMask"/> sibling) now
    /// ride the shared grant/revoke leaf on tape.
    /// <see cref="Server.WorldServer.AnyMachineEverPumped"/> refuses for the IDENTICAL reason: offline replay rehydrates a FRESH
    /// <see cref="Server.WorldMachineHost"/> from the tape's embedded definition — it can reconstruct a
    /// boot-declared cartridge's BOOT image (and CAS-verify a later <c>screen.insert</c>/<c>.select</c>'s content),
    /// but never a machine's ACCUMULATED core state (WRAM, CPU registers) once real ticks have run it, and the pose
    /// hash covers no machine state at all to catch the divergence after the fact — see this file's own remarks on
    /// hash-coverage scope. A world with a boot-declared cartridge means recording must arm before its first step,
    /// exactly like a world that mounts an addon must arm before its first tick.
    /// <see cref="Server.WorldServer.AnyScreenOpEverApplied"/> refuses for a RELATED but distinct reason: screen ops
    /// apply SYNCHRONOUSLY, between fixed steps — a <c>screen.insert</c> immediately
    /// followed by <c>replay.record</c>, with no step run in between, still arms clean under the first two checks
    /// (nothing has stepped), yet the insert already changed live <see cref="Server.WorldMachineHost"/> state that
    /// the record-start definition snapshot below never reflects (a screen op is not a document mutation, and only
    /// ever joins the tape's own authority list from the moment this method's own <c>ScreenOpTap</c> attaches
    /// onward — never retroactively for an op that already landed). Left ungated, offline replay would reconstruct a
    /// screen with NO machine at all where the live session already has one.
    /// <para>On success: snapshots the record-start starting state (the live definition, the mounted addon
    /// receipts, and the active seats) and attaches the loopback taps — intent, command, grant, revoke, session, and
    /// addon-lifecycle (submission-time) plus rebuild (apply-time, <see cref="Server.WorldServer.RebuildTap"/> — see
    /// its own remarks for why) — so the next ticks' whole server-input stream is captured. The authority taps write
    /// into ONE ordered list, which is what preserves the live interleaving between (for example) a driving command
    /// and a grant change, or a mount and the grant that follows it. A rebuild's list POSITION reflects when it
    /// APPLIED (drain order) rather than when it was SUBMITTED — the one known narrowing this apply-time capture
    /// accepts, immaterial unless a rebuild and an addon-lifecycle change are submitted in the identical tick.</para></summary>
    /// <param name="name">The name the recording will persist under at <see cref="StopRecording"/>.</param>
    /// <param name="refusal">The refusal reason, on failure; empty on success.</param>
    /// <returns><see langword="true"/> when recording armed.</returns>
    public bool TryBeginRecording(string name, out string refusal) {
        if (m_liveServer.AnyAddonEverPumped) {
            refusal = "an addon has already had an admitted execution attempted this session — offline replay creates FRESH guests at sim-counter zero, so a guest's accumulated memory/tick state from before recording began can never be re-established; record from a fresh boot, before any addon's first tick";
            return false;
        }

        if (m_liveServer.AnyMachineEverPumped) {
            refusal = "a screen machine has already stepped this session — offline replay reconstructs a FRESH WorldMachineHost from the tape's embedded definition, so a booted cartridge's accumulated core state (WRAM, CPU registers) from before recording began can never be re-established, and the pose hash covers no machine state to catch the divergence; record from a fresh boot, before any machine's first step";
            return false;
        }

        if (m_liveServer.AnyScreenOpEverApplied) {
            refusal = "a screen op (insert/eject/select/options/link/unlink) has already applied this session — screen ops are not document mutations, so the recording's own definition snapshot cannot capture whichever one already landed, and offline replay reconstructs a FRESH WorldMachineHost from that snapshot alone; a pre-record insert/select can leave the live session running a machine replay never even creates, and the pose hash covers no machine state to catch it; record from a fresh boot, before any screen op applies";
            return false;
        }

        refusal = "";
        m_recordName = name;
        m_definitionJson = WorldDefinitionSerialization.Serialize(definition: m_liveServer.Definition);
        m_mountedAddons = [.. m_liveServer.AddonReceipts];
        m_seats = CaptureActiveSeats();
        m_ticks = new List<WorldReplayTickInput>();
        m_liveHashes.Clear();
        m_currentAuthority = new List<WorldReplayEntry>();
        m_currentIntents = new List<IntentSubmission>();
        m_transport.IntentTap = submission => m_currentIntents.Add(item: submission);
        m_transport.CommandTap = command => m_currentAuthority.Add(item: new WorldReplayEntry.Command(Value: command));
        m_transport.DesignationTap = (designation, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Designation(Value: designation, Actor: actor));
        m_transport.GrantTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Grant(Value: grant, Actor: actor));
        m_transport.RevokeTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Revoke(Value: grant, Actor: actor));
        m_transport.SessionTap = request => m_currentAuthority.Add(item: new WorldReplayEntry.Session(Value: request));
        m_transport.AddonLifecycleTap = (lifecycle, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.AddonLifecycle(Value: lifecycle, Actor: actor));
        // Apply-time, not submission-time — see WorldServer.RebuildTap's own remarks for why Reset's hash cannot be
        // known any earlier (m_base is private, server-internal state that can move between submission and drain).
        m_liveServer.RebuildTap = (request, actor, contentHash) => m_currentAuthority.Add(item: new WorldReplayEntry.Rebuild(Kind: request.Kind, PathHint: request.PathHint, Force: request.Force, ContentHash: contentHash, Actor: actor));
        // Apply-time, mirroring RebuildTap exactly — a screen op the Control gate refuses is still taped (fires
        // AFTER the outcome is known, carrying a null hash for a refusal or for any non-Insert kind).
        m_liveServer.ScreenOpTap = (op, contentHash, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.ScreenOp(Value: op, ContentHash: contentHash, Actor: actor));
        m_liveServer.ServerEventTap = serverEvent => {
            switch (serverEvent) {
                case WorldServerEvent.PeerAdmitted admitted:
                    m_currentAuthority.Add(item: new WorldReplayEntry.PeerAdmitted(Value: admitted));
                    break;
                case WorldServerEvent.PeerDisconnected disconnected:
                    m_currentAuthority.Add(item: new WorldReplayEntry.PeerDisconnected(Value: disconnected));
                    break;
            }
        };
        m_mode = WorldReplayMode.Recording;

        return true;
    }

    /// <summary>Closes the current tick while recording: the submissions captured since the last call become one tick's
    /// input group, and the accumulators reset for the next tick. Called once per fixed tick from
    /// <c>Puck.World.WorldSimulation.Step</c> AFTER the server step, when the tick's whole stream has been submitted. A
    /// no-op while idle.</summary>
    public void NoteTick() {
        if ((m_mode != WorldReplayMode.Recording) || (m_ticks is not { } ticks)) {
            return;
        }

        ticks.Add(item: new WorldReplayTickInput(Authority: m_currentAuthority, Intents: m_currentIntents));
        m_currentAuthority = new List<WorldReplayEntry>();
        m_currentIntents = new List<IntentSubmission>();
        // Sample the LIVE population's pose hash AFTER this tick's server step and KEEP it. The hash was always
        // computed here; only the last one used to survive, which cost a mismatch its location. Appending instead of
        // overwriting adds one list slot per tick and no hashing at all, and it is what lets the verdict name the
        // first divergent tick. The trace stays one entry per tick, in lockstep with `ticks` above.
        m_liveHashes.Add(item: WorldReplaySnapshot.HashState(population: m_liveServer.Population));
    }

    /// <summary>Finalizes the active recording: PERSISTS the self-contained recording FIRST (the tape is evidence of
    /// the capture — see <see cref="WorldReplayStopResult"/>), detaches the taps and resets the tape's state on every
    /// exit path, then re-drives the persisted recording once through a fresh world and reports the outcome. A MATCH
    /// means the recording faithfully rehydrates — its captured starting state (the definition boot image + seats)
    /// reproduces the live session under the recorded input stream; a MISMATCH means the live session had already
    /// diverged from that boot image before record-start (a mid-session capture), which the fresh re-drive cannot
    /// reproduce; and a <see cref="WorldReplayStopResult.VerifyFault"/> means the post-persist re-drive itself could
    /// not complete (e.g. the mount pin — see <see cref="WorldReplaySnapshot.Drive"/>'s remarks — refusing a world
    /// whose addon set has moved since record-start). Reporting the verdict (or fault) at stop time makes that
    /// boundary loud rather than hidden; persisting first means a refusal there never costs the operator the
    /// capture.</summary>
    /// <returns>The stop result: the path always written, and either the comparison verdict or why the post-persist
    /// verify itself faulted.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    /// <exception cref="WorldReplayCodecException">Persisting, or the post-persist re-drive, hit a host-side codec bug
    /// (see <see cref="WorldReplaySnapshot.WriteFile"/> and <see cref="WorldReplaySnapshot.Drive"/>). This one is
    /// deliberately NOT folded into <see cref="WorldReplayStopResult.VerifyFault"/>: that field's framing is "the live
    /// tree moved past this recording", which a determinism hole is not.</exception>
    /// <exception cref="IOException">The tape file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The tape file could not be written.</exception>
    public WorldReplayStopResult StopRecording() {
        if ((m_mode != WorldReplayMode.Recording) || (m_definitionJson is not { } definitionJson) || (m_mountedAddons is not { } mountedAddons) || (m_seats is not { } seats) || (m_ticks is not { } ticks) || (m_recordName is not { } name)) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        // Persist under the LIVE tail hash — the state the running session actually reached at the last recorded tick.
        // The verify side re-drives a fresh world and compares against THIS, so a MATCH is a genuine live-vs-replay
        // fidelity proof, not a fresh-drive compared against another fresh drive of the same stream.
        var recording = new WorldReplaySnapshot {
            DefinitionJson = definitionJson,
            MountedAddons = mountedAddons,
            RecordedHashes = [.. m_liveHashes],
            Seats = seats,
            Ticks = ticks,
        };
        var path = PathFor(name: name);

        try {
            // PERSIST FIRST, before anything that can refuse: the re-drive below (Compare -> Drive) runs the mount-pin
            // verify, which can throw ROUTINELY (a document-only world.row.set addons/world.row.remove addons mutates the definition while
            // the live runtime keeps its boot receipts — mounting only happens at boot — so the recorded receipts and
            // the embedded definition can legitimately disagree). A refusal there must never destroy a capture that
            // already completed successfully; WriteFile also never leaves a truncated file on a codec throw (its own
            // remarks).
            WorldReplaySnapshot.WriteFile(path: path, recording: recording);
        } finally {
            // EVERY exit path from this method — a clean persist, a persist that threw, or (below) a post-persist
            // verify that faulted — leaves the tape Idle. Stop is a terminal request: once issued, there is no live
            // recording left to resume, so m_mode must never stay at Recording after this method returns, even when
            // something downstream refuses.
            DetachTaps();
            ResetRecordingState();
        }

        try {
            var verdict = Compare(recording: recording);

            return new WorldReplayStopResult(Path: path, Verdict: verdict, VerifyFault: null);
        } catch (Exception exception) when (exception is InvalidDataException or JsonException) {
            // The tape is already on disk (persisted above) — this is the LIVE TREE having moved past what the
            // recording pinned, never a persistence failure. See WorldReplaySnapshot.VerifyMountedAddons' remarks.
            // JsonException joins it because the re-drive re-parses the recording's OWN embedded definition
            // (WorldReplaySnapshot.Drive's first line); a definition this build cannot re-read is the same class of
            // "the recording no longer fits the tree" refusal, and letting it escape crashed the host instead.
            // WorldReplayCodecException is deliberately NOT caught here: it is a determinism hole in the host's own
            // codec, and folding it into this benign framing is exactly the misattribution this narrow catch removes.
            return new WorldReplayStopResult(Path: path, Verdict: null, VerifyFault: exception.Message);
        }
    }

    /// <summary>Aborts the active recording WITHOUT persisting it: detaches the taps and drops the captured stream.</summary>
    /// <returns>The dropped recording's name.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    public string CancelRecording() {
        if ((m_mode != WorldReplayMode.Recording) || (m_recordName is not { } name)) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        DetachTaps();
        ResetRecordingState();

        return name;
    }

    // Shared by StopRecording (every exit path, via try/finally) and CancelRecording: a recording's whole mutable
    // state, back to Idle. m_mode always ends at Idle here — leaving it at Recording with no live recording behind
    // it is the zombie state this method exists to prevent.
    private void ResetRecordingState() {
        m_mode = WorldReplayMode.Idle;
        m_recordName = null;
        m_definitionJson = null;
        m_mountedAddons = null;
        m_seats = null;
        m_ticks = null;
        m_liveHashes.Clear();
    }

    /// <summary>Loads a saved recording, rehydrates a FRESH world from it, re-drives the recorded server-input stream,
    /// and compares the replayed tail hash against the recorded one — the offline verification, run synchronously so the
    /// verdict is readable the instant it returns. Never touches the live session.</summary>
    /// <param name="name">The saved recording's name.</param>
    /// <returns>The comparison verdict, which names the first divergent tick when there is one.</returns>
    /// <exception cref="FileNotFoundException">No recording of that name exists.</exception>
    /// <exception cref="InvalidDataException">The file is not a <c>.puckreplay</c> recording, does not carry this
    /// build's tape shape token — greenfield, so a foreign shape is refused outright rather than read tolerantly;
    /// re-record it — carries a value no pinned wire set names, or pins a mounted addon set the fresh world would not
    /// reproduce.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug in the re-drive (see
    /// <see cref="WorldReplaySnapshot.Drive"/>) — never tape data.</exception>
    public WorldReplayVerdict Verify(string name) {
        var path = PathFor(name: name);
        WorldReplaySnapshot recording;

        using (var stream = File.OpenRead(path: path)) {
            recording = WorldReplaySnapshot.Read(stream: stream);
        }

        return Compare(recording: recording);
    }

    // The one comparison both verbs reduce through: re-drive the recording through a fresh world and fold the two
    // per-tick traces to their first disagreement. Live-vs-replay, never replay-vs-replay — the recorded trace was
    // sampled off the running population, so a match is a fidelity proof rather than a re-drive agreeing with itself.
    private WorldReplayVerdict Compare(WorldReplaySnapshot recording) {
        var replayedTrace = recording.Drive(profiles: m_profiles, engines: m_engines);

        return new WorldReplayVerdict(
            Ticks: recording.TickCount,
            Recorded: recording.RecordedTailHash,
            Replayed: ((replayedTrace.Length > 0) ? replayedTrace[^1] : 0UL),
            DivergedAt: HashTrace.FirstDivergence(left: recording.RecordedHashes, right: replayedTrace)
        );
    }

    // Snapshot the seats active at record-start: their slot and their seated profile — its name AND the locomotion
    // rates it carried right now, which is the whole reason this reads the live handle rather than only its name. Those
    // rates are simulation INPUT (WorldBody.Advance reads them every frame), so pinning them here is what stops a later
    // identity.motion from re-driving a different world under this recording's stream. Only the four local seats can be
    // active; a peer/inhabitant is boot-derived from the definition.
    private List<WorldReplaySeat> CaptureActiveSeats() {
        var seats = new List<WorldReplaySeat>();

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (m_liveServer.Population.IsActive(index: slot)) {
                seats.Add(item: new WorldReplaySeat(Slot: slot, Profile: PinProfile(profile: m_liveServer.Body(index: slot)?.Profile)));
            }
        }

        return seats;
    }

    // Read straight off the live handle in the simulation's own fixed-point currency — never through the float
    // accessors, which would quantize a rate that is already exact.
    private static WorldReplayProfilePin? PinProfile(WorldIdentity? profile) {
        if (profile is null) {
            return null;
        }

        return new WorldReplayProfilePin(Name: profile.Name, MoveSpeed: profile.FixedMoveSpeed, TurnSpeed: profile.FixedTurnSpeed);
    }

    private void DetachTaps() {
        m_transport.IntentTap = null;
        m_transport.CommandTap = null;
        m_transport.DesignationTap = null;
        m_transport.GrantTap = null;
        m_transport.RevokeTap = null;
        m_transport.SessionTap = null;
        m_transport.AddonLifecycleTap = null;
        m_liveServer.RebuildTap = null;
        m_liveServer.ScreenOpTap = null;
        m_liveServer.ServerEventTap = null;
    }
}
