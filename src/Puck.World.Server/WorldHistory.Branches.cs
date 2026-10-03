using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The outcome of re-entering a kept branch: where the live world went and whether every tick of the branch
/// reproduced the hash it recorded.</summary>
/// <param name="Name">The branch re-entered, which now names the future it displaced.</param>
/// <param name="From">The tick the live world sat at before the switch.</param>
/// <param name="ForkTick">The tick the branch leaves the timeline after.</param>
/// <param name="Head">The tick the live world sits at now: the branch's own head.</param>
/// <param name="TicksResimulated">The ticks re-simulated, to the fork and then through the branch.</param>
/// <param name="KeptTicks">The ticks of the displaced future kept under <paramref name="Name"/>; zero when the fork
/// was the head.</param>
/// <param name="DivergedAt">The first tick whose authoritative hash or mutation outcome disagreed with the recording,
/// up to the fork or through the branch, or <see langword="null"/> when every tick matched.</param>
/// <param name="RecordedHash">The authoritative hash the branch recorded at its head.</param>
/// <param name="LiveHash">The authoritative hash the live world holds at <paramref name="Head"/> now.</param>
public readonly record struct WorldHistorySwitchReport(string Name, ulong From, ulong ForkTick, ulong Head, int TicksResimulated, int KeptTicks, ulong? DivergedAt, ulong RecordedHash, ulong LiveHash) {
    /// <summary>Gets whether the re-entered branch reproduced every hash it recorded.</summary>
    public bool Matches => ((DivergedAt is null) && (RecordedHash == LiveHash));
}
public sealed partial class WorldHistory {
    /// <summary>The fork provenance a saved branch's tape names as its parent: the in-session history it was kept in.</summary>
    public const string SavedBranchParent = "world.history";

    private WorldHistoryBranch? FindBranch(string name) {
        foreach (var branch in m_branches) {
            if (string.Equals(a: branch.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return branch;
            }
        }

        return null;
    }
    // Every reason a kept branch's own ticks cannot be re-simulated, by tick; adds each verified rebuild beside those
    // the span preflight already read.
    private string? BranchEntryRefusal(WorldHistoryBranch branch) {
        for (var index = 0; (index < branch.Ticks.Count); index++) {
            foreach (var entry in branch.Ticks[index].Authority) {
                if (RecordedEntryRefusal(entry: entry) is { } reason) {
                    return $"at branch tick {((branch.ForkTick + ((ulong)index)) + 1UL)} {reason}";
                }
            }
        }

        return null;
    }

    /// <summary>Re-enters a kept branch: moves the live world to the branch's fork exactly as a seek does, keeps the
    /// future recorded after the fork as a branch under the same name in its place, then re-simulates the branch's
    /// recorded ticks through the live server's own doors, proving each against the hash the branch recorded and
    /// appending it to the window as the timeline that now stands. The live world ends at the branch's head.</summary>
    /// <param name="name">The kept branch to re-enter.</param>
    /// <param name="documentPath">The boot document's path, the load door's path hint when a restore must reinstall a
    /// keyframe's document, or <see langword="null"/>.</param>
    /// <param name="report">The switch's outcome, on success.</param>
    /// <param name="refusal">Why the live timeline could not be switched, by name, when it was not.</param>
    /// <returns><see langword="true"/> when the live world now runs the branch.</returns>
    public bool TrySwitch(string name, string? documentPath, out WorldHistorySwitchReport report, out string refusal) {
        report = default;

        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (FindBranch(name: name) is not { } branch) {
            refusal = $"no kept branch named '{name}' — world.history status lists them";

            return false;
        }

        var fork = branch.ForkTick;
        var keyframe = KeyframeAtOrBefore(tick: fork);
        var fromCursor = (
            (m_cursor <= fork) &&
            (m_cursor >= keyframe.KeyframeTick)
        );
        var start = (fromCursor
            ? m_cursor
            : keyframe.KeyframeTick);
        var from = m_cursor;
        var keptTicks = ((int)(Head - fork));
        ulong? diverged = null;
        var failure = m_server.ExecuteAuthorityOperation<string?>(operation: () => {
            if (SeekRefusal(start: start, target: fork) is { } seekRefusal) {
                return seekRefusal;
            }

            if (BranchEntryRefusal(branch: branch) is { } branchRefusal) {
                return branchRefusal;
            }

            m_tape.SuspendCapture();
            m_server.Extensions.EnterReplay();
            m_server.Output.WithholdsTimeline = true;
            var save = m_server.SaveEffectTap;

            m_server.SaveEffectTap = static _ => { };
            try {
                if (
                    !fromCursor &&
                    (RestoreLive(
                        documentPath: documentPath,
                        rebuilt: out _,
                        segment: keyframe
                    ) is { } restoreFailure)
                ) {
                    return restoreFailure;
                }

                if (
                    !fromCursor &&
                    (WorldStateHashComposition.HashAuthoritative(
                        server: m_server,
                        tick: keyframe.KeyframeTick
                    ) != keyframe.KeyframeHash)
                ) {
                    diverged = keyframe.KeyframeTick;
                }

                diverged ??= Resimulate(
                    from: start,
                    server: m_server,
                    to: fork
                );
                m_cursor = fork;
                // The branch leaves the list before the displaced future is kept under its name.
                _ = m_branches.Remove(item: branch);

                if (fork < Head) {
                    m_pendingBranchName = name;
                    BranchAtCursor();
                }

                diverged ??= ReenterBranch(branch: branch);

                return null;
            } finally {
                m_server.SaveEffectTap = save;
                m_server.Output.WithholdsTimeline = false;
                m_server.Extensions.CompleteReplay();
                m_tape.ResumeCapture();
            }
        });

        if (failure is not null) {
            refusal = failure;

            return false;
        }

        EnforceBudget();
        PublishRow();
        m_counters = (m_counters with { Seeks = (m_counters.Seeks + 1L) });
        m_server.Tick.PresentRestoredTimeline();
        m_tape.RaiseTimelineRestored();
        report = new WorldHistorySwitchReport(
            DivergedAt: diverged,
            ForkTick: fork,
            From: from,
            Head: m_cursor,
            KeptTicks: keptTicks,
            LiveHash: WorldStateHashComposition.HashAuthoritative(
                server: m_server,
                tick: m_cursor
            ),
            Name: name,
            RecordedHash: ((branch.AuthoritativeHashes.Length > 0)
                ? branch.AuthoritativeHashes[^1]
                : RecordedHashAt(tick: fork)),
            TicksResimulated: (((int)(fork - start)) + branch.Ticks.Count)
        );
        refusal = string.Empty;

        return true;
    }

    // Re-simulates a branch's recorded ticks on the live server from its fork, which is now the head, appending each to
    // the window as the live capture would and keyframing on the interval, and returns the first tick that disagreed
    // with the branch's recording.
    private ulong? ReenterBranch(WorldHistoryBranch branch) {
        ulong? diverged = null;
        var expected = new List<bool>();
        var replayed = new Queue<bool>();

        for (var index = 0; (index < branch.Ticks.Count); index++) {
            var tick = ((branch.ForkTick + ((ulong)index)) + 1UL);
            var input = branch.Ticks[index];

            if (!StepRecorded(
                expected: expected,
                input: input,
                recordedHash: branch.AuthoritativeHashes[index],
                replayed: replayed,
                server: m_server,
                stepTicks: branch.StepTicks[index],
                tick: tick
            )) {
                diverged ??= tick;
            }

            var hash = WorldStateHashComposition.HashAuthoritative(
                server: m_server,
                tick: tick
            );

            Append(
                authoritativeHash: hash,
                input: in input,
                stepTicks: branch.StepTicks[index],
                tick: tick
            );
            m_cursor = tick;

            if (m_segments[^1].Count >= m_interval) {
                _ = TryKeyframe(authoritativeHash: hash, tick: tick);
            }
        }

        return diverged;
    }

    /// <summary>Builds a kept branch as a standalone tape: it starts from the checkpoint of the latest keyframe at or
    /// before the branch's fork (without the owned-world catalog), carries the recorded timeline's ticks from that
    /// keyframe to the fork, then the branch's own ticks, each with the authoritative hash it recorded, and names its
    /// fork (<see cref="WorldReplaySnapshot.ForkedFrom"/>: <see cref="SavedBranchParent"/> and the count of leading
    /// ticks copied from the timeline). The pose trace is left for the caller to fill from a re-drive, since the
    /// history records only authoritative hashes.</summary>
    /// <param name="name">The kept branch.</param>
    /// <param name="tape">The tape draft, on success.</param>
    /// <param name="refusal">Why the branch cannot become a tape, when it cannot.</param>
    /// <returns><see langword="true"/> when the draft was built.</returns>
    public bool TryBranchTape(string name, out WorldReplaySnapshot? tape, out string refusal) {
        tape = null;

        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (FindBranch(name: name) is not { } branch) {
            refusal = $"no kept branch named '{name}' — world.history status lists them";

            return false;
        }

        var keyframe = KeyframeAtOrBefore(tick: branch.ForkTick);

        if (!WorldAuthorityCheckpointCodec.TryDecode(
            bytes: KeyframeBytes(segment: keyframe),
            checkpoint: out var checkpoint,
            documentDirectory: keyframe.DocumentDirectory,
            reason: out var decodeReason
        )) {
            refusal = $"the keyframe at tick {keyframe.KeyframeTick} does not decode ({decodeReason}) — a host defect";

            return false;
        }

        var definition = WorldDefinitionSerialization.Deserialize(
            documentDirectory: keyframe.DocumentDirectory,
            utf8Json: checkpoint!.Server.DefinitionJson
        );
        var rate = ((uint)Math.Max(val1: 0, val2: definition.SimulationRateHz));
        var width = ((rate == 0U) ? 0UL : EngineTicks.PerRate(ratePerSecond: rate));
        var ticks = new List<WorldReplayTickInput>();
        var hashes = new List<ulong>();
        var steps = new List<ulong>();

        for (var tick = (keyframe.KeyframeTick + 1UL); (tick <= branch.ForkTick); tick++) {
            var (segment, offset) = Locate(tick: tick);

            ticks.Add(item: InputAt(offset: offset, segment: segment));
            hashes.Add(item: segment.Hashes[offset]);
            steps.Add(item: segment.StepTicks[offset]);
        }

        var copied = ticks.Count;

        ticks.AddRange(collection: branch.Ticks);
        hashes.AddRange(collection: branch.AuthoritativeHashes);
        steps.AddRange(collection: branch.StepTicks);

        for (var index = 0; (index < steps.Count); index++) {
            if (steps[index] != width) {
                refusal = $"tick {((keyframe.KeyframeTick + ((ulong)index)) + 1UL)} stepped {steps[index]} engine ticks, and a tape steps every tick at its rate's width ({width})";

                return false;
            }
        }

        var start = WorldReplaySnapshot.ForTape(checkpoint: checkpoint);

        tape = new WorldReplaySnapshot {
            Authority = m_server.AuthorityIdentity,
            DefinitionJson = start.Server.DefinitionJson,
            DocumentDirectory = keyframe.DocumentDirectory,
            ForkedFrom = new WorldReplayForkProvenance(
                ParentName: SavedBranchParent,
                Tick: copied
            ),
            Instance = m_server.InstanceIdentity,
            MountedAddons = [],
            PipelineSourceDirectory = m_server.PipelineSources?.DocumentDirectory,
            RecordedAuthoritativeHashes = [.. hashes],
            RecordedHashes = [.. hashes],
            Seats = WorldReplaySnapshot.SeatsOf(checkpoint: start),
            SimulationRate = rate,
            StartCheckpoint = start,
            Ticks = ticks,
        };
        refusal = string.Empty;

        return true;
    }
    /// <summary>Saves a kept branch as a <c>.puckreplay</c> tape under <paramref name="tapeName"/>
    /// (<see cref="TryBranchTape"/>, written by <see cref="WorldReplayTape.SaveDraft"/>): the tape starts from the
    /// keyframe before the fork and names its fork, and its re-drive's verdict against the hashes the history recorded
    /// is returned, the same verdict <c>replay.verify</c> reads from the file.</summary>
    /// <param name="name">The kept branch.</param>
    /// <param name="tapeName">The tape's name.</param>
    /// <param name="path">The tape's path, on success.</param>
    /// <param name="verdict">The re-drive's verdict, on success.</param>
    /// <param name="refusal">Why nothing was written, when nothing was.</param>
    /// <returns><see langword="true"/> when the tape was written.</returns>
    public bool TrySaveBranch(string name, string tapeName, out string path, out WorldReplayVerdict verdict, out string refusal) {
        path = string.Empty;
        verdict = default;

        if (!WorldReplayTape.IsValidName(name: tapeName)) {
            refusal = "a tape name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters";

            return false;
        }

        if (!TryBranchTape(
            name: name,
            refusal: out refusal,
            tape: out var draft
        )) {
            return false;
        }

        try {
            verdict = m_tape.SaveDraft(
                draft: draft!,
                name: tapeName,
                path: out path
            );
        } catch (Exception exception) when ((exception is InvalidDataException or System.Text.Json.JsonException)) {
            refusal = $"the branch's re-drive refused it, so nothing was written — {exception.Message}";

            return false;
        }

        return true;
    }
}
