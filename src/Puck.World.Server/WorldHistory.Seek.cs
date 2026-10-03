using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The outcome of a seek: where the live world went, how it got there, and whether every re-simulated tick
/// reproduced the authoritative hash the history recorded for it.</summary>
/// <param name="From">The tick the live world sat at before the seek.</param>
/// <param name="To">The tick the live world sits at now.</param>
/// <param name="KeyframeTick">The keyframe the seek restored, or <see langword="null"/> when it re-simulated forward
/// from the live state.</param>
/// <param name="RebuiltDocument">Whether the restore reinstalled the keyframe's document through the load door
/// first, because a structural edit landed after the keyframe.</param>
/// <param name="TicksResimulated">The ticks re-simulated to reach <paramref name="To"/>.</param>
/// <param name="DivergedAt">The first re-simulated tick whose authoritative hash or mutation outcome disagreed with
/// the recording, or <see langword="null"/> when every tick matched.</param>
/// <param name="RecordedHash">The authoritative hash the history recorded at <paramref name="To"/>.</param>
/// <param name="LiveHash">The authoritative hash the live world holds at <paramref name="To"/> now.</param>
/// <param name="MachineCoresOutsideProof">Whether the world has stepped a machine. Machine cores are outside the
/// authoritative hash, so the proof covers what the cores fed into world state and not the cores themselves: the
/// history records no per-tick machine digest, because taking one would wait on every queued machine worker every
/// tick.</param>
public readonly record struct WorldHistorySeekReport(ulong From, ulong To, ulong? KeyframeTick, bool RebuiltDocument, int TicksResimulated, ulong? DivergedAt, ulong RecordedHash, ulong LiveHash, bool MachineCoresOutsideProof) {
    /// <summary>Gets whether the live world reproduces the recorded authoritative state at <see cref="To"/>; see
    /// <see cref="MachineCoresOutsideProof"/> for what that state leaves out.</summary>
    public bool Matches => ((DivergedAt is null) && (RecordedHash == LiveHash));
}
public sealed partial class WorldHistory {
    // What a re-simulation cannot rewind: an entry whose consequence lives at another authority or connection. A live
    // neighbour's link delivery is not among them: it re-applies through the event feed exactly as a replay drive
    // feeds it, and the neighbour's own bodies are read as they stand now, so seam contact with a neighbour that has
    // moved since shows up as a divergence the per-tick hash proof names.
    private static string? Unrewindable(WorldReplayEntry entry) => (entry switch {
        WorldReplayEntry.Transfer => "a traveler crossed to another authority",
        WorldReplayEntry.Arrival => "a traveler arrived from another authority",
        WorldReplayEntry.FederatedIntents => "a federated traveler drove a body",
        WorldReplayEntry.PeerAdmitted => "a remote peer was admitted",
        WorldReplayEntry.PeerDisconnected => "a remote peer disconnected",
        WorldReplayEntry.SessionEvent => "a session changed state that authority checkpoints do not preserve",
        _ => null,
    });

    /// <summary>Gets the newest recorded tick, or <see langword="null"/> while no window is held.</summary>
    public ulong? HeadTick => ((m_segments.Count > 0)
        ? Head
        : null);
    /// <summary>Gets the oldest tick a seek can reach, or <see langword="null"/> while no window is held.</summary>
    public ulong? OldestTick => ((m_segments.Count > 0)
        ? Oldest
        : null);
    /// <summary>Gets every byte the history holds against its budget.</summary>
    public long BytesHeld => HeldBytes;
    /// <summary>Gets the memory budget, in bytes.</summary>
    public long BudgetBytes => m_budgetBytes;
    /// <summary>Gets the tick the live world sits at in the history, or <see langword="null"/> while no window is
    /// held.</summary>
    public ulong? CursorTick => ((m_segments.Count > 0)
        ? m_cursor
        : null);

    // The refusal a seek, a diff, or a replay-edit shares before touching anything: the history holds a window.
    private string? WindowRefusal() {
        if (!m_on) {
            return "the history is off — world.history on starts one";
        }

        if (m_segments.Count == 0) {
            return $"no window yet — {m_waiting}";
        }

        return null;
    }
    // Every reason the live timeline cannot be rewound from the cursor to `target` right now, by name.
    private string? SeekRefusal(ulong target, ulong start) {
        if (m_tape.Mode == WorldReplayMode.Recording) {
            return $"replay.record '{m_tape.Name}' is capturing this timeline — replay.stop or replay.cancel first";
        }

        if (m_tape.Mode == WorldReplayMode.Replaying) {
            return "a replay drive holds the session — replay.cancel ends it";
        }

        if (m_tape.OpenTickHoldsInput) {
            return $"input submitted since tick {m_cursor} closed has not run yet, and a seek would drop it — world.history resume runs it";
        }

        if (
            (m_server.Document.Pending.Count != 0) ||
            (m_server.Tick.Ordered.Count != 0)
        ) {
            return "a buffered edit has not applied yet — seek after it lands";
        }
        if (m_server.Extensions.PendingContributionCount != 0) {
            return "a provider contribution has not applied yet — seek after it lands";
        }

        if (UncapturableLiveState() is { } reset) {
            return reset;
        }

        try {
            m_server.Engagement.AssertCheckpointQuiescent();
        } catch (InvalidOperationException exception) {
            return $"an engagement is in flight ({exception.Message}) — body.disengage first";
        }

        var upper = Math.Max(
            val1: m_cursor,
            val2: target
        );

        // From the seek's start: an entry between the cursor and a later keyframe is one a backward seek already
        // scanned to reach the cursor (Append's invariant).
        foreach (var (tick, entry) in EntriesBetween(from: start, to: upper)) {
            if (Unrewindable(entry: entry) is { } reason) {
                return $"at tick {tick} {reason}; rewinding across it would leave the other side of it standing";
            }
        }

        return RecordedSpanRefusal(from: start, to: target);
    }
    // Restores a keyframe into the live server: in place when no structural edit landed since it, otherwise through
    // the load door first, so solids, machines, and the rule compilation match the keyframe's document.
    private string? RestoreLive(Segment segment, string? documentPath, out bool rebuilt) {
        if (!WorldAuthorityCheckpointCodec.TryDecode(
            bytes: KeyframeBytes(segment: segment),
            documentDirectory: segment.DocumentDirectory,
            checkpoint: out var checkpoint,
            reason: out var decodeReason
        )) {
            rebuilt = false;

            return $"the keyframe at tick {segment.KeyframeTick} does not decode ({decodeReason}) — a host defect";
        }

        var live = WorldHistoryFingerprint.Of(server: m_server);

        rebuilt = !segment.Fingerprint.Matches(other: live);

        if (rebuilt) {
            var definition = WorldDefinitionSerialization.Deserialize(
                documentDirectory: segment.DocumentDirectory,
                utf8Json: checkpoint!.Server.DefinitionJson
            );

            m_server.EnqueueRebuild(
                principal: Principal.Console,
                request: new WorldRebuildRequest(
                    ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: checkpoint.Server.DefinitionJson),
                    Definition: definition,
                    Force: true,
                    Kind: WorldRebuildKind.Load,
                    Origin: ((KeyframeDocumentPath(documentPath: documentPath, segment: segment) is { } keyframePath)
                        ? new WorldRebuildOrigin.File(Path: keyframePath)
                        : null)
                )
            );
            _ = m_server.DrainAdministrative();

            if (!ReferenceEquals(
                objA: m_server.Definition,
                objB: definition
            )) {
                return $"the keyframe's document at tick {segment.KeyframeTick} was refused by the load door (the [world.definition rejected: …] line names why)";
            }

            m_server.RestoreCheckpoint(checkpoint: checkpoint);
            m_counters = (m_counters with { RebuildRestores = (m_counters.RebuildRestores + 1L) });

            return null;
        }

        // The fingerprint proves the live base and journal are the keyframe's own, so they are kept by identity: the
        // restore's decoded copies would hold equal content under new references, and every later in-place restore
        // reads that identity.
        var document = m_server.Document;
        var baseDefinition = document.Base;
        var baseOrigin = document.BaseOrigin;
        var journal = document.Journal.ToArray();

        m_server.RestoreCheckpoint(checkpoint: checkpoint!);
        document.AdoptBase(
            definition: baseDefinition,
            origin: baseOrigin
        );
        document.Journal.Clear();
        document.Journal.AddRange(collection: journal);
        m_counters = (m_counters with { InPlaceRestores = (m_counters.InPlaceRestores + 1L) });

        return null;
    }
    // Re-simulates the recorded ticks after `from` up to and including `to` on `server`, through the same doors and
    // in the same order the live step took them, and returns the first tick whose mutation outcomes or
    // authoritative hash disagree with the recording.
    private ulong? Resimulate(WorldServer server, ulong from, ulong to, Func<ulong, WorldReplayTickInput, WorldReplayTickInput>? rewrite = null, Action<ulong, WorldServer>? observe = null) {
        ulong? diverged = null;
        var expected = new List<bool>();
        var replayed = new Queue<bool>();

        for (var tick = (from + 1UL); (tick <= to); tick++) {
            var (segment, offset) = Locate(tick: tick);
            var input = InputAt(offset: offset, segment: segment);

            if (StepRecorded(
                expected: expected,
                input: ((rewrite is null)
                    ? input
                    : rewrite(arg1: tick, arg2: input)),
                recordedHash: segment.Hashes[offset],
                replayed: replayed,
                server: server,
                stepTicks: segment.StepTicks[offset],
                tick: tick
            ) is false) {
                diverged ??= tick;
            }

            observe?.Invoke(arg1: tick, arg2: server);
        }

        return diverged;
    }
    // One re-simulated tick: the recorded input into the doors, the step, the journal horizon the shell enforces, then
    // the outcome and hash proof. Returns whether the tick reproduced the recording.
    private bool StepRecorded(WorldServer server, ulong tick, in WorldReplayTickInput input, ulong stepTicks, ulong recordedHash, List<bool> expected, Queue<bool> replayed) {
        expected.Clear();
        WorldReplaySnapshot.ApplyRecordedTick(
            expectedMutationOutcomes: expected,
            input: input,
            population: server.Population,
            rebuildSource: VerifiedRebuild,
            replayedMutationOutcomes: replayed,
            server: server
        );
        // The re-simulated step forwards nothing through a portal: the worlds beyond it are not rewinding.
        server.ReplaysInput = true;
        server.Advance(stepTicks: stepTicks);
        server.EnforceJournalDepth();
        m_counters = (m_counters with {
            HashFolds = (m_counters.HashFolds + 1L),
            TicksResimulated = (m_counters.TicksResimulated + 1L),
        });

        var matched = true;

        try {
            WorldReplaySnapshot.VerifyRecordedMutationOutcomes(
                expected: expected,
                replayed: replayed,
                tick: ((int)Math.Min(
                    val1: tick,
                    val2: int.MaxValue
                ))
            );
        } catch (InvalidDataException) {
            replayed.Clear();
            matched = false;
        }

        return (
            matched &&
            (WorldStateHashComposition.HashAuthoritative(
                server: server,
                tick: tick
            ) == recordedHash)
        );
    }

    /// <summary>Names the future a live tick behind the head replaces: the next live tick keeps it under
    /// <paramref name="name"/> instead of discarding it.</summary>
    /// <param name="name">The branch's name, or <see langword="null"/> to discard the future.</param>
    /// <param name="refusal">Why the request was refused, when it was.</param>
    /// <returns><see langword="true"/> when the cursor sits behind the head and the request is armed.</returns>
    public bool TryArmBranch(string? name, out string refusal) {
        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (m_cursor >= Head) {
            refusal = $"the cursor is at the head (tick {m_cursor}) — there is no future to {((name is null) ? "discard" : "keep")}";

            return false;
        }

        if (
            (name is not null) &&
            !WorldReplayTape.IsValidName(name: name)
        ) {
            refusal = "a branch name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters";

            return false;
        }

        m_pendingBranchName = name;
        refusal = string.Empty;

        return true;
    }
    /// <summary>Moves the live world to <paramref name="target"/>: restores the latest keyframe at or before it (or
    /// keeps the live state when the target lies ahead of the cursor inside the same span) and re-simulates the
    /// recorded input to it, proving each tick against the recorded authoritative hash. The capture is suspended for
    /// the re-simulation, the restored timeline is delivered to every client, and the history's cursor moves; a later
    /// live tick behind the head replaces the recorded future.</summary>
    /// <param name="target">The tick to reach, inside the window.</param>
    /// <param name="documentPath">The boot document's path — the load door's path hint when a restore must reinstall
    /// the keyframe's document — or <see langword="null"/> when there is none.</param>
    /// <param name="report">The seek's outcome, on success.</param>
    /// <param name="refusal">Why the live timeline could not be moved, by name, when it was not.</param>
    /// <returns><see langword="true"/> when the live world now sits at <paramref name="target"/>.</returns>
    public bool TrySeek(ulong target, string? documentPath, out WorldHistorySeekReport report, out string refusal) {
        report = default;

        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (
            (target < Oldest) ||
            (target > Head)
        ) {
            refusal = $"tick {target} is outside the window {Oldest}..{Head}";

            return false;
        }

        var keyframe = KeyframeAtOrBefore(tick: target);
        var fromCursor = (
            (m_cursor <= target) &&
            (m_cursor >= keyframe.KeyframeTick)
        );
        var start = (fromCursor
            ? m_cursor
            : keyframe.KeyframeTick);

        var from = m_cursor;
        var rebuilt = false;
        ulong? diverged = null;
        var failure = m_server.ExecuteAuthorityOperation<string?>(operation: () => {
            if (SeekRefusal(start: start, target: target) is { } seekRefusal) {
                return seekRefusal;
            }
            m_tape.SuspendCapture();
            m_server.Extensions.EnterReplay();
            // Nothing the seek passes through reaches a viewer: not the restore's delivery, not a load-door install,
            // not a re-simulated tick. PresentRestoredTimeline below delivers the target once.
            m_server.Output.WithholdsTimeline = true;
            var save = m_server.SaveEffectTap;

            m_server.SaveEffectTap = static _ => { };
            try {
                if (
                    !fromCursor &&
                    (RestoreLive(
                        documentPath: documentPath,
                        rebuilt: out rebuilt,
                        segment: keyframe
                    ) is { } restoreFailure)
                ) {
                    return restoreFailure;
                }

                // The restore's own proof: the live state now folds to the hash the keyframe's tick recorded.
                m_counters = (m_counters with { HashFolds = (m_counters.HashFolds + (fromCursor ? 0L : 1L)) });

                if (
                    !fromCursor &&
                    (WorldStateHashComposition.HashAuthoritative(
                        server: m_server,
                        tick: keyframe.KeyframeTick
                    ) != keyframe.KeyframeHash)
                ) {
                    diverged = keyframe.KeyframeTick;
                }

                var resimulated = Resimulate(
                    from: start,
                    server: m_server,
                    to: target
                );

                diverged ??= resimulated;

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

        m_cursor = target;
        m_counters = (m_counters with { Seeks = (m_counters.Seeks + 1L) });
        m_server.Tick.PresentRestoredTimeline();
        m_tape.RaiseTimelineRestored();
        report = new WorldHistorySeekReport(
            DivergedAt: diverged,
            From: from,
            KeyframeTick: (fromCursor
                ? null
                : keyframe.KeyframeTick),
            LiveHash: WorldStateHashComposition.HashAuthoritative(
                server: m_server,
                tick: target
            ),
            MachineCoresOutsideProof: m_server.AnyMachineEverPumped,
            RebuiltDocument: rebuilt,
            RecordedHash: RecordedHashAt(tick: target),
            TicksResimulated: ((int)(target - start)),
            To: target
        );
        refusal = string.Empty;

        return true;
    }

    // The authoritative hash recorded at a tick inside the window.
    private ulong RecordedHashAt(ulong tick) {
        if (tick == Oldest) {
            return m_segments[0].KeyframeHash;
        }

        var (segment, offset) = Locate(tick: tick);

        return segment.Hashes[offset];
    }
}
