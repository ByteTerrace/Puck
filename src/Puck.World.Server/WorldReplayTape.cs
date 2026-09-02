using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The tape's live state — what <see cref="WorldReplayTape"/> is doing with the running session.</summary>
public enum WorldReplayMode {
    /// <summary>Neither recording nor replaying; the loopback taps are detached and the session runs untouched.</summary>
    Idle,

    /// <summary>The live session's per-tick server-input stream is being captured into the in-flight recording.</summary>
    Recording,

    /// <summary>A saved tape is being driven into the live session: the world was reset to the tape's boot image,
    /// each recorded tick's input is fed through the server's own doors ahead of its step, and the local seats'
    /// driving input is masked at the loopback until the drive ends (its <c>to</c> tick, the tape's end, or
    /// <c>replay.cancel</c>). A fork hands over to <see cref="Recording"/> at its target tick.</summary>
    Replaying,
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
/// server-input stream — the intent submissions plus the ordered authority inputs (commands, grants, revokes) that reach
/// the <see cref="LoopbackTransport"/> each tick — plus the record-start world definition, active seats, and mounted
/// addon receipts, into a self-contained
/// <see cref="WorldReplaySnapshot"/>. It samples both the live state-system hash used for the verdict and a narrower
/// population-pose hash retained for trajectory inspection. A saved recording
/// rehydrates a fresh world from its captured starting state and re-drives the captured stream through it
/// (<see cref="WorldReplaySnapshot.Drive"/>); the replayed state trace is compared against the live reference, so a MATCH is a
/// genuine live-vs-replay fidelity proof rather than a re-drive compared against another re-drive of the same stream.
/// </summary>
/// <remarks>
/// <para>Verification is an offline recomputation over an isolated shadow world (<see cref="WorldReplaySnapshot.Drive"/>)
/// that never touches the running session, so its verdict is readable synchronously over the pipe the instant it
/// completes. The live drive (<see cref="TryBeginDrive"/>, the <c>replay.drive</c>/<c>replay.fork</c> verbs) is the
/// one path that does touch the session: it resets the running server to a tape's boot image through the server's
/// own rebuild door and feeds the recorded ticks through the same doors the offline drive uses, one per live step,
/// with the local seats' driving input masked at the loopback for the drive's whole span.</para>
/// <para>The scope is honest but narrow. The captured starting state is the server simulation only — definition + active seats + the
/// per-tick authority/intent stream. The rehydrated starting body state is the deterministic boot image of the captured
/// definition (a fresh world reconstructs it exactly), not a per-body pose snapshot, and its starting grant table is
/// likewise the captured definition's own document grants plus the permissive seed, not the live table as it stood at
/// record-start. A MATCH is therefore a fidelity proof precisely when the live session was still at that boot state at
/// record-start (a boot-anchored capture); a capture armed after the session has already diverged — a body moved, or a
/// grant typed before the tape was armed — faithfully re-drives its stream but from a boot start, so
/// <see cref="Verify"/> honestly reports MISMATCH rather than a false MATCH. Full record-start rehydration (so a
/// mid-session capture also matches) is the identified next lever; the live-tail reference hash is the backstop that
/// keeps the verdict honest until it lands. Screen machines, their pixels, cameras, overlays, and audio are
/// presentation and are excluded (see <see cref="WorldReplaySnapshot"/>).</para>
/// <para>The mount pin: a guest's driving never crosses the loopback, so it is never captured; the replay re-runs the
/// document's guests instead. That only stays honest while the modules it re-mounts are the ones that ran live, so
/// record-start also copies the live server's mount receipts (name, module content hash, fuel) into the recording,
/// and <see cref="WorldReplaySnapshot.Drive"/> refuses — before its first tick — a fresh world whose own mount
/// disagrees. The receipts come from the instances that mounted, never from the document's addon rows: a row carries
/// the pin an author wrote, and the tape needs the identity of what actually loaded under it.</para>
/// <para>Single-threaded on the launcher's window-pump thread: the <c>replay.*</c> verbs are Immediate (they run inline
/// during the command pump's drain) and the taps + <see cref="NoteTick"/> run inside the fixed-step
/// <c>Puck.World.WorldSimulation.Step</c> — both on that one thread, so no locking is needed. The <c>replay.*</c> verbs are
/// not folded into the captured stream (they never reach the loopback), so a recording never records the recording
/// verbs themselves; physical device input, the <c>world.grant</c>/<c>world.revoke</c> verbs, and Simulation-routed
/// world verbs do reach the loopback and are captured.</para>
/// </remarks>
public sealed partial class WorldReplayTape {
    private const string Extension = ".puckreplay";

    private readonly Func<WorldDefinition, WorldServer, IWorldAddonHost> m_addonHostFactory;
    private readonly IReadOnlyList<IScreenMachineEngine> m_engines;
    private readonly WorldServer m_liveServer;
    private readonly WorldOwnedWorlds m_profiles;
    private readonly LoopbackTransport m_transport;

    private byte[]? m_definitionJson;
    // Set only by a fork's handover: the parent tape and how many of its leading tick groups this recording began
    // with. Persisted as the child's header provenance; null for a recording armed from boot.
    private WorldReplayForkProvenance? m_forkedFrom;
    private WorldReplayMode m_mode;
    // The guests MOUNTED at record-start, copied out of the live server's runtime. Read once here rather than at stop:
    // the pin must describe the world that produced the recorded stream, and mounting is a boot-time act that a later
    // read could only re-report, never re-witness.
    private List<WorldAddonReceipt>? m_mountedAddons;
    private string? m_recordName;
    // The rate THIS recording is captured at, snapshotted at TryBeginRecording alongside the definition it is read
    // off — never re-read live at StopRecording. A tape spans exactly one rate (see NoteTick's own mid-capture
    // rebuild check below), so the header must stamp the rate the RECORDED SPAN actually ran at, not whatever the
    // live server happens to author at the moment the operator typed replay.stop.
    private uint m_recordRateHz;
    private List<WorldReplaySeat>? m_seats;
    private List<WorldReplayTickInput>? m_ticks;

    // The LIVE session's per-tick pose hash trace — one entry appended each NoteTick (after that tick's server step), so
    // the final entry is the true live tail and the whole array is the trajectory. Persisted as the recording's
    // RecordedHashes, so a replay's fresh re-drive is compared against the ACTUAL live session tick by tick, not against
    // another re-drive of itself and not only at the end.
    private readonly List<ulong> m_liveHashes = [];
    // The verdict trace: authoritative state-system lanes at the same post-step boundary as m_liveHashes.
    private readonly List<ulong> m_liveAuthoritativeHashes = [];
    // The current tick's accumulating input, rotated into m_ticks at each NoteTick. ONE authority list, not one per
    // kind: a command and a grant that crossed the link in a given order must replay in that order, and parallel lists
    // have no relative order left to preserve.
    private List<WorldReplayEntry> m_currentAuthority = new();
    private List<IntentSubmission> m_currentIntents = new();
    // The FIFO correlation between a Mutation entry MutationTap just added (by its index into m_currentAuthority)
    // and the MutationOutcomeTap call that will patch its Outcome later the SAME tick — see MutationTap/
    // MutationOutcomeTap's own remarks. Always empty by the time NoteTick rotates m_currentAuthority (every
    // mutation tapped this tick is also drained this tick), but cleared defensively alongside the rest of a
    // recording's mutable state.
    private readonly Queue<int> m_openMutationEntryIndices = new();

    /// <summary>Initializes the tape over the live server it snapshots the starting state from, the profile catalog a
    /// replay's seats re-resolve against, and the loopback whose per-tick submissions it taps.</summary>
    /// <param name="liveServer">The authoritative live server (read at record-start for the definition and active seats).</param>
    /// <param name="profiles">The profile catalog (handed to a replay's fresh world for seat re-resolution).</param>
    /// <param name="transport">The client→server loopback whose intent/command submissions the tape captures.</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) — handed to
    /// <see cref="WorldReplaySnapshot.Drive"/> so the offline re-drive's own <see cref="Server.WorldMachineHost"/>
    /// boots against the same engine set the live session ran under.</param>
    /// <param name="addonHostFactory">Builds a fresh <see cref="IWorldAddonHost"/> over a re-deserialized definition
    /// and its shadow server — handed to <see cref="WorldReplaySnapshot.Drive"/> so each re-drive mounts its own
    /// guest set rather than reusing the live session's. The factory must return a host already attached to the
    /// <see cref="WorldServer"/> it is handed (or rely on <see cref="WorldReplaySnapshot.Drive"/> attaching it) — a
    /// host that never reaches <see cref="WorldServer.AttachAddons"/> re-drives with no guests and produces a MATCH
    /// that proves nothing.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldReplayTape(WorldServer liveServer, WorldOwnedWorlds profiles, LoopbackTransport transport, IEnumerable<IScreenMachineEngine> engines, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory) {
        ArgumentNullException.ThrowIfNull(argument: liveServer);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: transport);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: addonHostFactory);

        m_liveServer = liveServer;
        m_profiles = profiles;
        m_transport = transport;
        m_engines = [.. engines];
        m_addonHostFactory = addonHostFactory;
    }

    /// <summary>Gets the tape's current mode.</summary>
    public WorldReplayMode Mode => m_mode;
    /// <summary>Gets the name the active recording will persist under.</summary>
    public string? Name => m_recordName;
    /// <summary>Gets the ticks captured so far in the active recording.</summary>
    public int TickCount => (m_ticks?.Count ?? 0);

    // Snapshot the seats active at record-start: their slot and their seated profile — its name AND the locomotion
    // rates it carried right now, which is the whole reason this reads the live handle rather than only its name. Those
    // rates are simulation INPUT (WorldBody.Advance reads them every frame), so pinning them here is what stops a later
    // identity.motion from re-driving a different world under this recording's stream. Only the four local seats can be
    // active; a peer/inhabitant is boot-derived from the definition.
    private List<WorldReplaySeat> CaptureActiveSeats() {
        var seats = new List<WorldReplaySeat>();

        for (var slot = 0; (slot < m_liveServer.Population.LocalSeatCount); slot++) {
            if (m_liveServer.Population.IsActive(index: slot)) {
                seats.Add(item: new WorldReplaySeat(
                    Slot: slot,
                    Profile: PinProfile(profile: m_liveServer.Body(index: slot)?.Profile)
                ));
            }
        }

        return seats;
    }
    // The one comparison both verbs reduce through: re-drive the recording through a fresh world and fold the two
    // per-tick traces to their first disagreement. Live-vs-replay, never replay-vs-replay — the recorded trace was
    // sampled off the running population, so a match is a fidelity proof rather than a re-drive agreeing with itself.
    private WorldReplayVerdict Compare(WorldReplaySnapshot recording) {
        var replayedTrace = recording.DriveTraces(
            addonHostFactory: m_addonHostFactory,
            engines: m_engines,
            profiles: m_profiles
        );

        return new WorldReplayVerdict(
            Ticks: recording.TickCount,
            Recorded: recording.RecordedTailHash,
            Replayed: ((replayedTrace.Authoritative.Length > 0)
            ? replayedTrace.Authoritative[^1]
            : 0UL),
            DivergedAt: HashTrace.FirstDivergence(
                left: recording.RecordedAuthoritativeHashes,
                right: replayedTrace.Authoritative
            )
        );
    }
    private void DetachTaps() {
        m_transport.IntentTap = null;
        m_transport.CommandTap = null;
        m_transport.DesignationTap = null;
        m_transport.GrantTap = null;
        m_transport.RevokeTap = null;
        m_transport.SessionTap = null;
        m_transport.UndoTap = null;
        m_transport.CompositionTap = null;
        m_transport.QueryTap = null;
        m_liveServer.LinkDeliveryTap = null;
        m_liveServer.MutationTap = null;
        m_liveServer.MutationOutcomeTap = null;
        m_liveServer.RebuildTap = null;
        m_liveServer.ScreenOpTap = null;
        m_liveServer.ServerEventTap = null;
    }
    // Read straight off the live handle in the simulation's own fixed-point currency — never through the float
    // accessors, which would quantize a rate that is already exact.
    private static WorldReplayProfilePin? PinProfile(WorldIdentity? profile) {
        if (profile is null) {
            return null;
        }

        return new WorldReplayProfilePin(
            Name: profile.Name,
            MoveSpeed: profile.FixedMoveSpeed,
            TurnSpeed: profile.FixedTurnSpeed
        );
    }
    // Shared by StopRecording (every exit path, via try/finally) and CancelRecording: a recording's whole mutable
    // state, back to Idle. m_mode always ends at Idle here — leaving it at Recording with no live recording behind
    // it is the zombie state this method exists to prevent.
    private void ResetRecordingState() {
        m_mode = WorldReplayMode.Idle;
        m_recordName = null;
        m_definitionJson = null;
        m_forkedFrom = null;
        m_recordRateHz = 0U;
        m_mountedAddons = null;
        m_seats = null;
        m_ticks = null;
        m_liveHashes.Clear();
        m_liveAuthoritativeHashes.Clear();
        m_openMutationEntryIndices.Clear();
    }

    /// <summary>Aborts the active recording without persisting it: detaches the taps and drops the captured stream.</summary>
    /// <returns>The dropped recording's name.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    public string CancelRecording() {
        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_recordName is not { } name)
        ) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        DetachTaps();
        ResetRecordingState();

        return name;
    }
    /// <summary>Returns the <c>Replays/</c> directory (created on first use), beside World's other local data.</summary>
    public static string Directory() {
        var directory = Path.Combine(
            path1: WorldStateRoot.Resolve(),
            path2: "Replays"
        );

        _ = System.IO.Directory.CreateDirectory(path: directory);

        return directory;
    }
    /// <summary>Validates a replay name: non-empty and free of path-navigation characters — a console verb argument is
    /// untrusted, so this keeps every resolved path under <see cref="Directory"/>.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is safe to use as a filename stem.</returns>
    public static bool IsValidName(string name) {
        return (
            !string.IsNullOrWhiteSpace(value: name) &&
            (name.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) < 0) &&
            !name.Contains(value: '.') &&
            !name.Contains(value: '/') &&
            !name.Contains(value: '\\')
        );
    }
    /// <summary>Returns the names of every persisted replay.</summary>
    /// <returns>The saved names, sorted; empty when none exist.</returns>
    public static IReadOnlyList<string> List() {
        var directory = Directory();
        var names = new List<string>();

        foreach (var path in System.IO.Directory.EnumerateFiles(
            path: directory,
            searchPattern: $"*{Extension}"
        )) {
            names.Add(item: Path.GetFileNameWithoutExtension(path: path));
        }

        names.Sort(comparer: StringComparer.OrdinalIgnoreCase);

        return names;
    }
    /// <summary>Records a pause or resume of the boot instance's own live schedule lever into the active recording's
    /// authority stream — a no-op while <see cref="Mode"/> is <see cref="WorldReplayMode.Idle"/>. The live pause
    /// lever itself lives on <c>Puck.World.WorldInstance</c>/<c>WorldInstanceHost</c>, a layer above this assembly
    /// (see this type's own class remarks on the dependency direction), so <c>WorldRateCommandModule</c> — the
    /// console door that owns the lever — calls this directly whenever a pause/resume actually changes the boot
    /// instance's own state (never for a named instance's own lever: this tape covers the boot instance only).</summary>
    /// <param name="paused"><see langword="true"/> for a pause, <see langword="false"/> for a resume.</param>
    public void NoteRateLever(bool paused) {
        if (m_mode != WorldReplayMode.Recording) {
            return;
        }

        m_currentAuthority.Add(item: new WorldReplayEntry.RateLever(Paused: paused));
    }
    /// <summary>Closes the current tick while recording: the submissions captured since the last call become one tick's
    /// input group, and the accumulators reset for the next tick. Called once per fixed tick from
    /// <c>Puck.World.WorldSimulation.Step</c> after the server step, when the tick's whole stream has been submitted. A
    /// no-op while idle.</summary>
    public void NoteTick() {
        if (m_mode == WorldReplayMode.Replaying) {
            NoteDriveTick();

            return;
        }

        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_ticks is not { } ticks)
        ) {
            return;
        }

        ticks.Add(item: new WorldReplayTickInput(
            Authority: m_currentAuthority,
            Intents: m_currentIntents
        ));
        m_currentAuthority = new List<WorldReplayEntry>();
        m_currentIntents = new List<IntentSubmission>();
        // Sample both traces AFTER this tick's server step and APPEND them (never overwrite). The authoritative
        // state-system trace drives the verdict; the pose trace lets inspection localize visible motion divergence.
        // Both stay one entry per tick, in lockstep with `ticks` above.
        m_liveHashes.Add(item: WorldReplaySnapshot.HashState(population: m_liveServer.Population));
        m_liveAuthoritativeHashes.Add(item: WorldRuntimeStateHash.HashAuthoritative(
            server: m_liveServer,
            tick: (m_liveServer.NextInputTick - 1UL)
        ));

        // A REBUILD (world.reset/world.load/world.reload) applies inside THIS same tick's server.Step, before
        // NoteTick runs — so by now m_liveServer.Definition already reflects whatever it swapped to. A tape spans
        // exactly one rate (the header stamped at record-start, above); a rebuild that swapped in a
        // differently-rated document would otherwise let this recording keep going and silently mix rates under one
        // header, producing a tape that reports RateMismatch against itself the moment it is driven. Stop loudly
        // here instead — the rebuild's own authority entry is already captured in the tick group just closed, so the
        // tape still carries a legible record of what happened, it simply ends there.
        var liveRateHz = ((uint)m_liveServer.Definition.SimulationRateHz);

        if (liveRateHz != m_recordRateHz) {
            var recordedRateHz = m_recordRateHz;

            Console.Error.WriteLine(value: $"[replay.record: stopped — a rebuild changed the simulation rate mid-capture ({recordedRateHz} Hz -> {liveRateHz} Hz); a recording spans exactly one rate, so this tape ends at the tick the rebuild landed rather than silently mixing rates]");

            try {
                _ = StopRecording();
            } catch (Exception exception) {
                Console.Error.WriteLine(value: $"[replay.record: the forced stop above failed to complete ({exception.Message})]");
            }
        }
    }
    /// <summary>Records one same-process transfer's decided outcome into the active recording's authority stream —
    /// the local multi-authority tape contract. A no-op while <see cref="Mode"/> is
    /// <see cref="WorldReplayMode.Idle"/>. The live cohort/resolver machinery lives on
    /// <c>Puck.World.WorldInstanceHost</c>, a layer above this assembly (the same reason <see cref="NoteRateLever"/>
    /// is called directly rather than tapped through the loopback), so <c>WorldInstanceHost.ApplyTransfer</c> calls
    /// this the moment a transfer touching the boot instance (as source or destination) is fully decided —
    /// committed or aborted, never at enqueue, since only the decided outcome is worth taping. This records the
    /// crossing's outcome for <c>replay.verify</c> to refuse a tampered one by name; it does not let
    /// <see cref="WorldReplaySnapshot.Drive"/> re-derive a destination instance's own simulation — the offline
    /// re-drive constructs one shadow <see cref="Server.WorldServer"/> for the boot instance alone, so a crossing
    /// that actually lands a body in another instance is, and remains, outside what this tape can
    /// re-execute.</summary>
    /// <param name="transferId">The transfer id minted for this crossing.</param>
    /// <param name="destinationName">The resolved destinations row name.</param>
    /// <param name="scopeKey">The resolved scope key.</param>
    /// <param name="generationId">The resolver-issued generation id the cohort resolved against.</param>
    /// <param name="outcome">A short canonical outcome summary (e.g. <c>"committed:2/2"</c> or
    /// <c>"aborted:&lt;reason&gt;"</c>) — narration only, never re-interpreted by <see cref="WorldReplaySnapshot.Drive"/>.</param>
    /// <param name="departedBootSlots">The 0-based boot local-seat slots this crossing actually removed from boot's
    /// own population (empty unless this transfer's source is boot and it committed) — re-applied against the
    /// replay's shadow population at re-drive so a departed body stops contributing to the pose hash there exactly
    /// as it stopped contributing live (see <see cref="WorldReplayEntry.Transfer"/>'s own remarks).</param>
    public void NoteTransfer(ulong transferId, string destinationName, string scopeKey, ulong generationId, string outcome, IReadOnlyList<int> departedBootSlots) {
        if (m_mode != WorldReplayMode.Recording) {
            return;
        }

        m_currentAuthority.Add(item: new WorldReplayEntry.Transfer(
            DepartedBootSlots: departedBootSlots,
            DestinationName: destinationName,
            GenerationId: generationId,
            Outcome: outcome,
            ScopeKey: scopeKey,
            TransferId: transferId
        ));
    }
    /// <summary>Returns the on-disk path a valid <paramref name="name"/> resolves to.</summary>
    /// <param name="name">The replay's name.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string name) {
        return Path.Combine(
            path1: Directory(),
            path2: (name + Extension)
        );
    }
    /// <summary>Finalizes the active recording: persists the self-contained recording first (the tape is evidence of
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
    /// deliberately not folded into <see cref="WorldReplayStopResult.VerifyFault"/>: that field's framing is "the live
    /// tree moved past this recording", which a determinism hole is not.</exception>
    /// <exception cref="IOException">The tape file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The tape file could not be written.</exception>
    public WorldReplayStopResult StopRecording() {
        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_definitionJson is not { } definitionJson) ||
            (m_mountedAddons is not { } mountedAddons) ||
            (m_seats is not { } seats) ||
            (m_ticks is not { } ticks) ||
            (m_recordName is not { } name)
        ) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        // FLUSH the pending (still-open) authority bucket before persisting — but ONLY its RATE-LEVER entries.
        // NoteTick only rotates m_currentAuthority/m_currentIntents into `ticks` immediately after a completed
        // server step — while the boot world is paused, no step happens, so NoteTick never runs and whatever arrived
        // since the last closed tick stays stranded in the open bucket. A rate-lever pause/resume fact is the one
        // kind proven harmless to fold onto the last closed tick — Drive's own re-drive treats
        // WorldReplayEntry.RateLever as a documented no-op, so WHERE it lands among a tick's authority list changes
        // nothing about what the replay computes. Everything else stranded here — a command, grant, revoke, session,
        // designation, addon-lifecycle op, rebuild, screen op, or any pending intent — is DISCARDED instead, loudly
        // and by name: it belongs to a tick that never closed, and recording it anywhere in this tape would claim it
        // ran on a tick it did not.
        var pendingLevers = m_currentAuthority.FindAll(match: static entry => (entry is WorldReplayEntry.RateLever));
        var discardedAuthorityCount = (m_currentAuthority.Count - pendingLevers.Count);
        var discardedIntentCount = m_currentIntents.Count;

        if (ticks.Count > 0) {
            if (pendingLevers.Count > 0) {
                var lastIndex = (ticks.Count - 1);
                var last = ticks[lastIndex];
                var mergedAuthority = new List<WorldReplayEntry>(collection: last.Authority);

                mergedAuthority.AddRange(collection: pendingLevers);

                ticks[lastIndex] = new WorldReplayTickInput(
                    Authority: mergedAuthority,
                    Intents: last.Intents
                );
            }
        } else if (pendingLevers.Count > 0) {
            // No closed tick to fold onto (stopped before the first step ever completed) — this tape's wire shape
            // (WorldReplaySnapshot) carries no header/trailer slot outside the per-tick Ticks list a lever fact
            // could attach to without one, so a zero-tick tape drops a pending lever BY DESIGN rather than minting a
            // phantom extra tick (which would make Drive take one more Step than the live session ever did). Stated
            // loudly here rather than silently. Both side-channel notes carry the [replay.tape: prefix, never
            // [replay.stop: — the canary runner accounts exactly one "[<verb>:" line per submitted command, so a
            // second line under the verb's own prefix makes a leg unaccountable.
            Console.Error.WriteLine(value: $"[replay.tape: '{name}' recorded zero ticks — {pendingLevers.Count} pending rate-lever event(s) have no closed tick to attach to and are dropped by design (a zero-tick tape cannot carry one)]");
        }

        if (
            (discardedAuthorityCount > 0) ||
            (discardedIntentCount > 0)
        ) {
            Console.Error.WriteLine(value: $"[replay.tape: '{name}' discarded {discardedAuthorityCount} pending authority entr{((discardedAuthorityCount == 1)
                ? "y"
                : "ies")} and {discardedIntentCount} pending intent submission(s) that never closed onto a tick — recording them would have claimed they ran on a tick they did not]");
        }

        // Persist under the LIVE tail hash — the state the running session actually reached at the last recorded tick.
        // The verify side re-drives a fresh world and compares against THIS, so a MATCH is a genuine live-vs-replay
        // fidelity proof, not a fresh-drive compared against another fresh drive of the same stream.
        var recording = new WorldReplaySnapshot {
            DefinitionJson = definitionJson,
            ForkedFrom = m_forkedFrom,
            MountedAddons = mountedAddons,
            RecordedHashes = [.. m_liveHashes],
            RecordedAuthoritativeHashes = [.. m_liveAuthoritativeHashes],
            Seats = seats,
            // Stamped from the RECORD-START snapshot (see m_recordRateHz's own remarks) — the rate the recorded span
            // actually ran at, never the live server's rate as it happens to stand at stop time: a mid-capture
            // rebuild that changed the rate already force-stopped this recording from NoteTick before this method
            // could ever see a disagreement between the two.
            SimulationRate = m_recordRateHz,
            Ticks = ticks,
        };
        string path;

        try {
            // PathFor (Directory's own CreateDirectory) is resolved INSIDE this guarded region: an unwritable state
            // root must throw from inside the try so the finally below still runs and detaches the tape rather than
            // leaving it stuck in Recording with its taps attached. PERSIST FIRST, before anything that can refuse:
            // the re-drive below (Compare -> Drive) runs the mount-pin verify, which can throw ROUTINELY (a
            // document-only world.row.set addons/world.row.remove addons mutates the definition while the live
            // runtime keeps its boot receipts — mounting only happens at boot — so the recorded receipts and the
            // embedded definition can legitimately disagree). A refusal there must never destroy a capture that
            // already completed successfully; WriteFile also never leaves a truncated file on a codec throw (its own
            // remarks).
            path = PathFor(name: name);

            WorldReplaySnapshot.WriteFile(
                path: path,
                recording: recording
            );
        } finally {
            // EVERY exit path from this method — a clean persist, a PathFor/WriteFile failure, or (below) a
            // post-persist verify that faulted — leaves the tape Idle. Stop is a terminal request: once issued,
            // there is no live recording left to resume, so m_mode must never stay at Recording after this method
            // returns, even when something upstream or downstream refuses. On a PathFor/WriteFile failure the
            // recording itself is lost (never persisted) — loudly, by the exception this method still lets escape —
            // but the TAPE never gets stuck: it is Idle, its taps detached, ready to arm a fresh recording rather
            // than wedged mid-capture forever.
            DetachTaps();
            ResetRecordingState();
        }

        try {
            var verdict = Compare(recording: recording);

            return new WorldReplayStopResult(
                Path: path,
                Verdict: verdict,
                VerifyFault: null
            );
        } catch (Exception exception) when ((exception is InvalidDataException or JsonException)) {
            // The tape is already on disk (persisted above) — this is the LIVE TREE having moved past what the
            // recording pinned, never a persistence failure. See WorldReplaySnapshot.VerifyMountedAddons' remarks.
            // JsonException joins it because the re-drive re-parses the recording's OWN embedded definition
            // (WorldReplaySnapshot.Drive's first line); a definition this build cannot re-read is the same class of
            // "the recording no longer fits the tree" refusal. WorldReplayCodecException is deliberately NOT caught
            // here: it is a determinism hole in the host's own codec, and folding it into this benign framing would
            // misattribute it.
            return new WorldReplayStopResult(
                Path: path,
                Verdict: null,
                VerifyFault: exception.Message
            );
        }
    }
    /// <summary>Arms recording — refusing first when arming would be dishonest (the boot-anchored
    /// contract): <see cref="Server.WorldServer.AnyAddonEverPumped"/> refuses if any addon has already had an
    /// admitted execution attempted before this call, because offline replay creates fresh guests at sim-counter
    /// zero — a guest's accumulated memory/tick state from before recording began is exactly what that fresh
    /// re-drive can never re-establish.
    /// <see cref="Protocol.WorldGrant.KindMask"/> (and its <see cref="Protocol.WorldGrant.WriteMask"/> sibling) now
    /// ride the shared grant/revoke leaf on tape.
    /// <see cref="Server.WorldServer.AnyMachineEverPumped"/> refuses for the identical reason: offline replay rehydrates a fresh
    /// <see cref="Server.WorldMachineHost"/> from the tape's embedded definition — it can reconstruct a
    /// boot-declared cartridge's boot image (and CAS-verify a later <c>screen.insert</c>/<c>.select</c>'s content),
    /// but never a machine's accumulated core state (WRAM, CPU registers) once real ticks have run it, and the pose
    /// hash covers no machine state at all to catch the divergence after the fact — see this file's own remarks on
    /// hash-coverage scope. A world with a boot-declared cartridge means recording must arm before its first step,
    /// exactly like a world that mounts an addon must arm before its first tick.
    /// <see cref="Server.WorldServer.AnyScreenOpEverApplied"/> refuses for a related but distinct reason: screen ops
    /// apply synchronously, between fixed steps — a <c>screen.insert</c> immediately
    /// followed by <c>replay.record</c>, with no step run in between, still arms clean under the first two checks
    /// (nothing has stepped), yet the insert already changed live <see cref="Server.WorldMachineHost"/> state that
    /// the record-start definition snapshot below never reflects (a screen op is not a document mutation, and only
    /// ever joins the tape's own authority list from the moment this method's own <c>ScreenOpTap</c> attaches
    /// onward — never retroactively for an op that already landed). Left ungated, offline replay would reconstruct a
    /// screen with no machine at all where the live session already has one.
    /// <para>On success: snapshots the record-start starting state (the live definition, the mounted addon
    /// receipts, and the active seats) and attaches the loopback taps — intent, command, grant, revoke, session, and
    /// mutation (submission-time via <see cref="Server.WorldServer.MutationTap"/>, its outcome patched in at drain
    /// via <see cref="Server.WorldServer.MutationOutcomeTap"/> — an addon row's mount/unmount/reload rides this same
    /// leaf as an ordinary <c>UpsertAddon</c>/<c>RemoveAddon</c> mutation, never a separate lifecycle leaf) plus
    /// rebuild (apply-time, <see cref="Server.WorldServer.RebuildTap"/> — see its own remarks for why) — so the next
    /// ticks' whole server-input stream is captured. The authority taps write into one ordered list, which is what
    /// preserves the live interleaving between (for example) a driving command and a grant change, or an addon
    /// mount and the grant that follows it. A rebuild's list position reflects when it applied (drain order) rather
    /// than when it was submitted — the one known narrowing this apply-time capture accepts, immaterial unless a
    /// rebuild and an addon-affecting mutation are submitted in the identical tick.</para></summary>
    /// <param name="name">The name the recording will persist under at <see cref="StopRecording"/>.</param>
    /// <param name="refusal">The refusal reason, on failure; empty on success.</param>
    /// <returns><see langword="true"/> when recording armed.</returns>
    public bool TryBeginRecording(string name, out string refusal) {
        if (m_mode != WorldReplayMode.Idle) {
            refusal = ((m_mode == WorldReplayMode.Replaying)
                ? $"a replay drive of '{m_drive?.SourceName}' is in progress — replay.cancel ends it, or replay.fork records from a drive"
                : $"already recording '{m_recordName}'"
            );
            return false;
        }

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
        // Stamped HERE, from the SAME record-start read as the definition snapshot above — the rate the recorded
        // span is actually about to run at. See this field's own remarks for why StopRecording must never re-read
        // the live rate instead.
        m_recordRateHz = ((uint)m_liveServer.Definition.SimulationRateHz);
        m_mountedAddons = [.. m_liveServer.AddonReceipts];
        m_seats = CaptureActiveSeats();
        m_ticks = new List<WorldReplayTickInput>();
        m_liveHashes.Clear();
        m_liveAuthoritativeHashes.Clear();
        AttachTaps();
        m_mode = WorldReplayMode.Recording;

        return true;
    }
    // Attaches every capture tap over fresh per-tick accumulators — the record half of arming, shared by
    // TryBeginRecording and a fork's handover (which arrives with its prefix already in m_ticks/m_liveHashes).
    private void AttachTaps() {
        m_currentAuthority = new List<WorldReplayEntry>();
        m_currentIntents = new List<IntentSubmission>();
        m_openMutationEntryIndices.Clear();
        m_transport.IntentTap = submission => m_currentIntents.Add(item: submission);
        m_transport.CommandTap = command => m_currentAuthority.Add(item: new WorldReplayEntry.Command(Value: command));
        m_transport.DesignationTap = (designation, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Designation(
            Actor: actor,
            Value: designation
        ));
        m_transport.GrantTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Grant(
            Actor: actor,
            Value: grant
        ));
        m_transport.RevokeTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Revoke(
            Actor: actor,
            Value: grant
        ));
        m_transport.SessionTap = request => m_currentAuthority.Add(item: new WorldReplayEntry.Session(Value: request));
        // Apply-time on the SERVER, not at the loopback: the loopback is only one of three mutation ingresses (a
        // local console/client write, an admitted socket peer, and a traveller's submission forwarded by its source
        // authority), and only the envelope dispatch sees all three with the actor each one stamped. Outcome starts
        // false — MutationOutcomeTap patches it to the real accept/refuse verdict before this SAME tick closes (both
        // fire from within the one server.Step this entry's own tick belongs to; see the field's own remarks).
        m_liveServer.MutationTap = (mutation, actor) => {
            m_openMutationEntryIndices.Enqueue(item: m_currentAuthority.Count);
            m_currentAuthority.Add(item: new WorldReplayEntry.Mutation(
                Actor: actor,
                Outcome: false,
                Value: mutation
            ));
        };
        m_liveServer.MutationOutcomeTap = (_, applied) => {
            // Correlates by POSITION, never by decoding mutation content: MutationTap and MutationOutcomeTap fire in
            // the identical FIFO order — both ultimately driven by the one m_pending queue every mutation kind
            // shares — so the Nth open entry always answers the Nth outcome, even across several mutations pending
            // in the same tick.
            if (m_openMutationEntryIndices.TryDequeue(result: out var index) && (index < m_currentAuthority.Count) && (m_currentAuthority[index] is WorldReplayEntry.Mutation pending)) {
                m_currentAuthority[index] = (pending with { Outcome = applied });
            }
        };
        m_liveServer.LinkDeliveryTap = adjacency => m_currentAuthority.Add(item: new WorldReplayEntry.LinkDelivery(Adjacency: adjacency));
        m_transport.UndoTap = (count, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Undo(
            Actor: actor,
            Count: count
        ));
        m_transport.CompositionTap = (composition, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Composition(
            Actor: actor,
            Value: composition
        ));
        m_transport.QueryTap = (query, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Query(
            Actor: actor,
            Value: query
        ));
        // Apply-time, not submission-time — see WorldServer.RebuildTap's own remarks for why Reset's hash cannot be
        // known any earlier (m_base is private, server-internal state that can move between submission and drain).
        m_liveServer.RebuildTap = (request, actor, contentHash) => m_currentAuthority.Add(item: new WorldReplayEntry.Rebuild(
            Kind: request.Kind,
            PathHint: request.PathHint,
            Force: request.Force,
            ContentHash: contentHash,
            Actor: actor
        ));
        // Apply-time, mirroring RebuildTap exactly — a screen op the Control gate refuses is still taped (fires
        // AFTER the outcome is known, carrying a null hash for a refusal or for any non-Insert kind).
        m_liveServer.ScreenOpTap = (op, contentHash, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.ScreenOp(
            Actor: actor,
            ContentHash: contentHash,
            Value: op
        ));
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
    }
    /// <summary>Loads a saved recording, rehydrates a fresh world from it, re-drives the recorded server-input stream,
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
}
