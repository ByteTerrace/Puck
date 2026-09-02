using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A live drive's read-back (<c>replay.status</c>): which tape is being driven, how far it has come, where
/// it stops, and whether it hands over to a recording there.</summary>
/// <param name="SourceName">The tape being driven.</param>
/// <param name="Cursor">The number of recorded ticks already stepped into the live session.</param>
/// <param name="Target">The recorded tick count the drive stops at (the <c>to</c> tick, or the tape's end).</param>
/// <param name="TapeTicks">The tape's own recorded tick count.</param>
/// <param name="ForkName">The recording the drive hands over to at <see cref="Target"/>, or <see langword="null"/>
/// for a plain drive.</param>
/// <param name="FastForward">Whether the drive steps as many recorded ticks per host step as the shell allows
/// rather than one per live tick.</param>
/// <param name="DivergedAt">The first driven tick whose live pose hash disagreed with the recording, or <c>-1</c>
/// while every driven tick has matched.</param>
public readonly record struct WorldReplayDriveProgress(string SourceName, int Cursor, int Target, int TapeTicks, string? ForkName, bool FastForward, int DivergedAt);
public sealed partial class WorldReplayTape {
    private DriveState? m_drive;

    // One live drive's whole mutable state — dropped the instant the drive ends, so no field of it can leak into
    // the recording a fork hands over to.
    private sealed class DriveState {
        public required WorldReplaySnapshot Source { get; init; }
        public required string SourceName { get; init; }
        public required int Target { get; init; }
        public string? ForkName { get; init; }
        public bool FastForward { get; init; }
        public int Cursor { get; set; }
        // Set by InjectDriveTick, cleared by NoteDriveTick: the recorded tick at Cursor is in the server's doors and
        // the next step consumes it. Guards a shell that steps without injecting (or injects twice).
        public bool Injected { get; set; }
        public int DivergedAt { get; set; } = -1;
        public List<ulong> LiveHashes { get; } = [];
        public List<bool> ExpectedMutationOutcomes { get; } = [];
        public Queue<bool> ReplayedMutationOutcomes { get; } = new();
    }

    /// <summary>Gets the live drive in progress, or <see langword="null"/> while <see cref="Mode"/> is not
    /// <see cref="WorldReplayMode.Replaying"/>.</summary>
    public WorldReplayDriveProgress? DriveProgress => ((m_drive is { } drive)
        ? new WorldReplayDriveProgress(
            Cursor: drive.Cursor,
            DivergedAt: drive.DivergedAt,
            FastForward: drive.FastForward,
            ForkName: drive.ForkName,
            SourceName: drive.SourceName,
            TapeTicks: drive.Source.TickCount,
            Target: drive.Target
        )
        : null
    );
    /// <summary>Gets the most recorded ticks a fast-forwarding drive may step inside one shell call — two seconds of
    /// the tape's own simulation time. Pacing only: it bounds how long one host frame stalls, and the next call
    /// resumes at the cursor this one left.</summary>
    public int FastForwardBurst => ((m_drive is { } drive)
        ? Math.Max(
            val1: 1,
            val2: ((int)Math.Min(
                val1: (drive.Source.SimulationRate * 2UL),
                val2: int.MaxValue
            ))
        )
        : 1
    );
    /// <summary>Gets a value indicating whether the shell should step the live server again inside the same call —
    /// a fast-forwarding drive that has not reached its target yet.</summary>
    public bool WantsFastForwardStep => (m_drive is { FastForward: true } drive && (drive.Cursor < drive.Target));

    // Every live drive ends here: seats return to live input, and a completed fork hands its prefix over to a fresh
    // recording; anything else — a plain drive's end, or a cancel — leaves the tape Idle.
    private void EndDrive(bool completed) {
        var drive = m_drive!;

        m_drive = null;
        m_transport.InputMasked = false;

        var verdict = ((drive.DivergedAt < 0)
            ? "every driven tick matched the recording"
            : $"first divergence at tick {drive.DivergedAt}"
        );

        Console.Error.WriteLine(value: $"[replay.drive: '{drive.SourceName}' {(completed
            ? "reached"
            : "cancelled at")} tick {drive.Cursor} of {drive.Target} — {verdict}; local seats returned to live input]");

        if (
            !completed ||
            (drive.ForkName is not { } forkName)
        ) {
            m_mode = WorldReplayMode.Idle;

            return;
        }

        var source = drive.Source;
        var prefix = new List<WorldReplayTickInput>(capacity: drive.Target);

        for (var tick = 0; (tick < drive.Target); tick++) {
            prefix.Add(item: source.Ticks[tick]);
        }

        m_recordName = forkName;
        m_definitionJson = source.DefinitionJson;
        m_recordRateHz = source.SimulationRate;
        m_mountedAddons = [.. source.MountedAddons];
        m_seats = [.. source.Seats];
        m_ticks = prefix;
        m_liveHashes.Clear();
        // The child's prefix hashes are what the live session reached while re-driving the parent — identical to
        // the parent's on a matching drive, and the honest live trace on a diverged one.
        m_liveHashes.AddRange(collection: drive.LiveHashes);
        m_forkedFrom = new WorldReplayForkProvenance(
            ParentName: drive.SourceName,
            Tick: drive.Target
        );
        AttachTaps();
        m_mode = WorldReplayMode.Recording;
        Console.Error.WriteLine(value: $"[replay.fork: recording '{forkName}' from tick {drive.Target} — ticks 0..{(drive.Target - 1)} copied from '{drive.SourceName}'; replay.stop persists it, replay.cancel drops it]");
    }
    // Narrates a recorded Load/Reload whose pinned file no longer reads to the recorded bytes, then lets the live
    // rebuild apply whatever the file now holds: a pin that refused from inside the live step would throw out of the
    // running session, and the hash comparison that follows reports the resulting divergence honestly.
    private string? NarrateRebuildContentPin(WorldReplayEntry.Rebuild rebuild) {
        if (
            (rebuild.PathHint is { } path) &&
            (m_drive is { } drive)
        ) {
            if (!WorldDefinitionFileSource.TryLoadLocally(
                contentHash: out var contentHash,
                definition: out _,
                path: path,
                reason: out var reason
            )) {
                Console.Error.WriteLine(value: $"[replay.drive: tick {drive.Cursor} — the recorded rebuild's pinned file '{path}' cannot be re-read ({reason}); the live rebuild will refuse it]");
            } else if (!string.Equals(
                a: contentHash,
                b: rebuild.ContentHash,
                comparisonType: StringComparison.Ordinal
            )) {
                Console.Error.WriteLine(value: $"[replay.drive: tick {drive.Cursor} — '{path}' now reads {contentHash}, the recording pinned {rebuild.ContentHash}; driving the file as it stands]");
            }
        }

        return null;
    }
    // The live drive's per-tick close, from NoteTick: sample the live pose hash the step just produced, compare it
    // (and the tick's mutation outcomes) against the recording, and advance the cursor — ending the drive at its
    // target.
    private void NoteDriveTick() {
        if (m_drive is not { Injected: true } drive) {
            return;
        }

        var tick = drive.Cursor;
        var liveHash = WorldReplaySnapshot.HashState(population: m_liveServer.Population);

        drive.LiveHashes.Add(item: liveHash);
        drive.Injected = false;

        try {
            WorldReplaySnapshot.VerifyRecordedMutationOutcomes(
                expected: drive.ExpectedMutationOutcomes,
                replayed: drive.ReplayedMutationOutcomes,
                tick: tick
            );
        } catch (InvalidDataException exception) {
            drive.ReplayedMutationOutcomes.Clear();
            Console.Error.WriteLine(value: $"[replay.drive: {exception.Message}]");

            if (drive.DivergedAt < 0) {
                drive.DivergedAt = tick;
            }
        }

        var recordedHash = drive.Source.RecordedHashes[tick];

        if (
            (liveHash != recordedHash) &&
            (drive.DivergedAt < 0)
        ) {
            drive.DivergedAt = tick;
            Console.Error.WriteLine(value: $"[replay.drive: divergence at tick {tick} of {drive.Target} — live hash 0x{liveHash:X16}, recorded 0x{recordedHash:X16}; the drive continues]");
        }

        drive.Cursor = (tick + 1);

        if (drive.Cursor >= drive.Target) {
            EndDrive(completed: true);
        }
    }
    // Installs the tape's boot image into the running server through its own doors: a forced world.load of the
    // embedded definition (solids, machines, addon plan, document grants, journal, base — the whole rebuild
    // pipeline), then the population image a fresh server of that definition reaches once the recorded seats join
    // on their pinned rates, installed through the population's checkpoint door (a rebuild alone keeps every live
    // body's pose, and a seat leave/rejoin resumes the parked body rather than respawning it).
    private string? ResetLiveWorldToBootImage(WorldReplaySnapshot source, WorldDefinition definition, string? documentPath) {
        m_liveServer.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: definition,
                PathHint: documentPath,
                Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: source.DefinitionJson)
            ),
            principal: WorldPrincipal.Console
        );
        _ = m_liveServer.DrainAdministrative();

        if (!ReferenceEquals(
            objA: m_liveServer.Definition,
            objB: definition
        )) {
            return "the boot-image rebuild of the tape's embedded definition was refused (the [world.definition rejected: …] line above names why)";
        }

        var population = new WorldPopulation(definition: definition);
        using var machines = new WorldMachineHost(
            screens: definition.Screens,
            engines: m_engines
        );
        var shadow = new WorldServer(
            definition: definition,
            population: population,
            profiles: m_profiles,
            envelope: new WorldRenderEnvelope(),
            machines: machines
        );

        source.SeatRecordedSeats(
            definition: definition,
            population: population,
            profiles: m_profiles,
            server: shadow
        );
        m_liveServer.Population.Restore(
            checkpoint: population.Capture(),
            defaults: definition.PlayerDefaults,
            tick: m_liveServer.NextInputTick
        );
        m_liveServer.Events.Restore(checkpoint: shadow.Events.Capture());

        return null;
    }

    /// <summary>Ends the live drive where it stands: the recorded ticks already stepped stay applied, the local
    /// seats return to live input, and a pending fork is abandoned rather than handed over.</summary>
    /// <returns>The name of the tape that was being driven.</returns>
    /// <exception cref="InvalidOperationException">No drive is in progress.</exception>
    public string CancelDrive() {
        if (
            (m_mode != WorldReplayMode.Replaying) ||
            (m_drive is not { } drive)
        ) {
            throw new InvalidOperationException(message: "No replay drive is in progress.");
        }

        var name = drive.SourceName;

        EndDrive(completed: false);

        return name;
    }
    /// <summary>Feeds the recorded tick at the drive cursor into the live server ahead of the step that consumes it
    /// — the same doors and order the offline drive uses — while <see cref="Mode"/> is
    /// <see cref="WorldReplayMode.Replaying"/>. Called by <see cref="WorldServerStepShell"/> immediately before
    /// <see cref="WorldServer.Step"/>; a no-op otherwise, or when the cursor's tick is already in the doors.</summary>
    public void InjectDriveTick() {
        if (
            (m_mode != WorldReplayMode.Replaying) ||
            (m_drive is not { } drive) ||
            drive.Injected ||
            (drive.Cursor >= drive.Target)
        ) {
            return;
        }

        drive.ExpectedMutationOutcomes.Clear();
        drive.Injected = true;
        WorldReplaySnapshot.ApplyRecordedTick(
            expectedMutationOutcomes: drive.ExpectedMutationOutcomes,
            input: drive.Source.Ticks[drive.Cursor],
            population: m_liveServer.Population,
            rebuildContentPin: NarrateRebuildContentPin,
            replayedMutationOutcomes: drive.ReplayedMutationOutcomes,
            server: m_liveServer
        );
    }
    /// <summary>Starts driving a saved tape into the live session: resets the running server to the tape's boot
    /// image (see <see cref="WorldReplayMode.Replaying"/>), masks the local seats' driving input at the loopback,
    /// and arms the per-step injection that feeds one recorded tick ahead of each live step (or a burst per step
    /// when fast-forwarding). Refuses, leaving the session untouched, when the tape is busy, recorded nothing, its
    /// active seat set differs from the live one (join or leave to match — a seat cannot be respawned through the
    /// session door), a screen machine or addon guest already holds state the rebuild door cannot reset, an
    /// engagement is in flight, or the boot-image rebuild is rejected.</summary>
    /// <param name="name">The saved tape's name.</param>
    /// <param name="toTick">How many recorded ticks to drive (<c>1..TickCount</c>), or <see langword="null"/> for
    /// the whole tape.</param>
    /// <param name="forkName">The recording to hand over to at <paramref name="toTick"/>, or <see langword="null"/>
    /// for a plain drive. A fork fast-forwards.</param>
    /// <param name="documentPath">The live world document's path — the rebuild's path hint, so relative machine
    /// content keeps resolving and the base origin reads honestly — or <see langword="null"/> when there is none.</param>
    /// <param name="refusal">Why the drive did not start; empty on success.</param>
    /// <returns><see langword="true"/> when the drive armed.</returns>
    /// <exception cref="FileNotFoundException">No tape of that name exists.</exception>
    /// <exception cref="InvalidDataException">The tape is unreadable, pins a rate its own definition disagrees with,
    /// or pins an addon set the live world does not mount.</exception>
    /// <exception cref="System.Text.Json.JsonException">The embedded definition cannot be re-read by this build.</exception>
    public bool TryBeginDrive(string name, int? toTick, string? forkName, string? documentPath, out string refusal) {
        if (m_mode != WorldReplayMode.Idle) {
            refusal = ((m_mode == WorldReplayMode.Replaying)
                ? $"a replay drive of '{m_drive?.SourceName}' is already in progress — replay.cancel ends it"
                : $"recording '{m_recordName}' — replay.stop persists it or replay.cancel drops it first"
            );
            return false;
        }

        WorldReplaySnapshot source;

        using (var stream = File.OpenRead(path: PathFor(name: name))) {
            source = WorldReplaySnapshot.Read(stream: stream);
        }

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: source.DefinitionJson);

        if (source.SimulationRate != ((uint)definition.SimulationRateHz)) {
            throw ReplayRefusal.RateMismatch.Raise(message: $"This .puckreplay recording's header pins {source.SimulationRate} Hz, but its own embedded world definition authors {definition.SimulationRateHz} Hz — this tape is internally inconsistent; re-record it.");
        }

        if (source.TickCount == 0) {
            refusal = $"'{name}' recorded zero ticks — there is nothing to drive";
            return false;
        }

        var target = (toTick ?? source.TickCount);

        if (
            (target < 1) ||
            (target > source.TickCount)
        ) {
            refusal = $"tick {target} is outside '{name}' — it carries {source.TickCount} tick(s), so the target must be 1..{source.TickCount}";
            return false;
        }

        var liveSeats = new List<int>();

        for (var slot = 0; (slot < m_liveServer.Population.LocalSeatCount); slot++) {
            if (m_liveServer.Population.IsActive(index: slot)) {
                liveSeats.Add(item: slot);
            }
        }

        var tapeSeats = new List<int>();

        foreach (var seat in source.Seats) {
            tapeSeats.Add(item: seat.Slot);
        }

        tapeSeats.Sort();

        if (!liveSeats.SequenceEqual(second: tapeSeats)) {
            refusal = $"'{name}' pins seats [{string.Join(
                separator: ", ",
                values: tapeSeats.Select(selector: static slot => (slot + 1)))}] but the live session has players [{string.Join(
                separator: ", ",
                values: liveSeats.Select(selector: static slot => (slot + 1)))}] joined — player.join/player.leave to match first";
            return false;
        }

        if (
            (definition.Screens.Count > 0) &&
            (m_liveServer.AnyMachineEverPumped || m_liveServer.AnyScreenOpEverApplied)
        ) {
            refusal = "the tape's world declares screens and a live screen machine has already stepped or a screen op has already applied — the rebuild door reconciles screens but never resets a booted cartridge's core state, so the boot image cannot be reached in this session";
            return false;
        }

        if (
            (source.MountedAddons.Count > 0) &&
            m_liveServer.AnyAddonEverPumped
        ) {
            refusal = "the tape pins mounted addons and a live guest has already been pumped — the rebuild door reuses an unchanged addon row's guest with its accumulated state, so the boot image cannot be reached in this session";
            return false;
        }

        try {
            m_liveServer.Engagement.AssertCheckpointQuiescent();
        } catch (InvalidOperationException exception) {
            refusal = $"an engagement is in flight ({exception.Message}) — body.disengage first";
            return false;
        }

        if (ResetLiveWorldToBootImage(
            definition: definition,
            documentPath: documentPath,
            source: source
        ) is { } resetRefusal) {
            refusal = resetRefusal;
            return false;
        }

        WorldReplaySnapshot.VerifyMountedAddons(
            fresh: m_liveServer.AddonReceipts,
            recorded: source.MountedAddons
        );

        m_drive = new DriveState {
            FastForward = (forkName is not null),
            ForkName = forkName,
            Source = source,
            SourceName = name,
            Target = target,
        };
        m_transport.InputMasked = true;
        m_mode = WorldReplayMode.Replaying;
        refusal = "";

        return true;
    }
}
