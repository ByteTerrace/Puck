using Puck.World.Server;

namespace Puck.World;

/// <summary>The outcome of a replay-edit: the edits moved back, where the edited timeline first left the recorded
/// one, and what differed there.</summary>
/// <param name="EditTick">The tick the edits were made at (the cursor).</param>
/// <param name="LandedAt">The tick the edits were re-applied at, <paramref name="EditTick"/> less the requested
/// span, plus one: the first tick that consumed them.</param>
/// <param name="Edits">Each moved edit, described, with whether the edited timeline accepted it.</param>
/// <param name="DivergedAt">The first tick whose authoritative hash left the recorded history, or
/// <see langword="null"/> when the edits changed no authoritative state in the span.</param>
/// <param name="Diff">The recorded state against the edited state at <paramref name="DivergedAt"/>, or
/// <see langword="null"/> when nothing diverged.</param>
/// <param name="ControlDivergedAt">The first tick at which the unedited shadow itself left the recording, or
/// <see langword="null"/> when the shadow reproduced every recorded tick — a non-null value means the result cannot
/// be trusted.</param>
public sealed record WorldHistoryReplayEditReport(ulong EditTick, ulong LandedAt, IReadOnlyList<string> Edits, ulong? DivergedAt, WorldHistoryDiff? Diff, ulong? ControlDivergedAt);
/// <summary>An isolated server restored from one of a history's keyframes: its own machine host and a scratch
/// owned-world catalog under the state root, so nothing re-simulated on it reaches the live session, its identities,
/// or the files the live world reads. Disposing it releases the machine host and deletes the scratch catalog.</summary>
public sealed class WorldHistoryShadow : IDisposable {
    private readonly string m_directory;
    private readonly IWorldMachineHost m_machines;

    internal WorldHistoryShadow(WorldServer server, IWorldMachineHost machines, string directory, ulong tick) {
        Server = server;
        Tick = tick;
        m_machines = machines;
        m_directory = directory;
    }

    /// <summary>Gets the restored server.</summary>
    public WorldServer Server { get; }
    /// <summary>Gets the keyframe tick the shadow was restored at.</summary>
    public ulong Tick { get; }

    /// <inheritdoc/>
    public void Dispose() {
        m_machines.Dispose();

        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
public sealed partial class WorldHistory {
    // A document-changing entry: what a replay-edit moves back in time.
    private static bool IsEdit(WorldReplayEntry entry) => (entry is WorldReplayEntry.Mutation or WorldReplayEntry.Undo or WorldReplayEntry.Composition or WorldReplayEntry.Rebuild);
    // The machine-host factory consumes a file path only for its directory. Keep that context with the keyframe,
    // even when the caller's current document lives elsewhere.
    private static string? KeyframeDocumentPath(Segment segment, string? documentPath) => ((segment.DocumentDirectory is { } directory)
        ? Puck.Abstractions.PuckPaths.Normalize(path: Path.Combine(path1: directory, path2: (Path.GetFileName(path: documentPath) ?? "history.world.json")))
        : null);
    private WorldHistoryShadow OpenShadow(Segment segment, string? documentPath) {
        if (!WorldAuthorityCheckpointCodec.TryDecode(
            bytes: KeyframeBytes(segment: segment),
            documentDirectory: segment.DocumentDirectory,
            checkpoint: out var checkpoint,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"the keyframe at tick {segment.KeyframeTick} does not decode ({reason}) — a host defect");
        }

        var documentDirectory = segment.DocumentDirectory;
        var definition = WorldDefinitionSerialization.Deserialize(
            documentDirectory: documentDirectory,
            utf8Json: checkpoint!.Server.DefinitionJson
        );
        var directory = Path.Combine(
            path1: m_stateRoot.PathOf(name: "HistoryShadows"),
            path2: Guid.NewGuid().ToString(format: "N")
        );
        var machines = m_machineHostFactory(
            definition.Screens,
            m_engines,
            KeyframeDocumentPath(documentPath: documentPath, segment: segment),
            null
        );

        try {
            var profiles = new WorldOwnedWorlds(
                directory: directory,
                machineId: m_server.Profiles.MachineId,
                template: definition
            );

            var (server, _) = WorldServer.FromCheckpoint(
                checkpoint: checkpoint,
                documentDirectory: documentDirectory,
                instanceIdentity: m_server.InstanceIdentity,
                machines: machines,
                profiles: profiles
            );

            server.RebuildDocuments = m_server.RebuildDocuments;
            server.PipelineSources = m_server.PipelineSources;
            server.Extensions.EnterReplay();
            // A rule's save effect re-derives on the shadow like any other effect; writing the world's file is
            // engine I/O the shadow never performs.
            server.SaveEffectTap = static _ => { };

            return new WorldHistoryShadow(
                directory: directory,
                machines: machines,
                server: server,
                tick: segment.KeyframeTick
            );
        } catch {
            machines.Dispose();

            throw;
        }
    }

    /// <summary>Diffs the recorded state at two ticks of the window: restores the latest keyframe at or before the
    /// earlier one into an isolated shadow server, re-simulates the recorded input through both ticks, and images
    /// the state at each. The live world is untouched.</summary>
    /// <param name="from">The earlier tick (either order is accepted; the diff reads from the earlier).</param>
    /// <param name="to">The later tick.</param>
    /// <param name="documentPath">The boot document's path, which the shadow's machine host reads content beside,
    /// or <see langword="null"/>.</param>
    /// <param name="diff">The diff, on success.</param>
    /// <param name="divergedAt">The first re-simulated tick whose hash left the recording, or
    /// <see langword="null"/> — a non-null value means the images describe a re-simulation that is not the recorded
    /// history.</param>
    /// <param name="refusal">Why the diff could not run, when it could not.</param>
    /// <returns><see langword="true"/> when the diff ran.</returns>
    public bool TryDiff(ulong from, ulong to, string? documentPath, out WorldHistoryDiff? diff, out ulong? divergedAt, out string refusal) {
        diff = null;
        divergedAt = null;

        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (from > to) {
            (from, to) = (to, from);
        }

        if (
            (from < Oldest) ||
            (to > Head)
        ) {
            refusal = $"ticks {from}..{to} leave the window {Oldest}..{Head}";

            return false;
        }

        var keyframe = KeyframeAtOrBefore(tick: from);

        if (RecordedSpanRefusal(from: keyframe.KeyframeTick, to: to) is { } spanRefusal) {
            refusal = spanRefusal;
            return false;
        }
        WorldHistoryImage? before = null;
        WorldHistoryImage? after = null;

        using (var shadow = OpenShadow(
            documentPath: documentPath,
            segment: keyframe
        )) {
            if (from == keyframe.KeyframeTick) {
                before = WorldHistoryImage.Capture(server: shadow.Server);
            }

            if (to == keyframe.KeyframeTick) {
                after = before;
            }

            divergedAt = Resimulate(
                from: keyframe.KeyframeTick,
                observe: (tick, server) => {
                    if (tick == from) {
                        before = WorldHistoryImage.Capture(server: server);
                    }

                    if (tick == to) {
                        after = ((from == to)
                            ? before
                            : WorldHistoryImage.Capture(server: server));
                    }
                },
                server: shadow.Server,
                to: to
            );
        }

        diff = WorldHistoryDiff.Between(
            from: before!,
            to: after!
        );
        refusal = string.Empty;

        return true;
    }
    /// <summary>Asks what the edits made at the cursor would have done had they landed <paramref name="ticksBack"/>
    /// ticks earlier: two isolated shadow servers re-simulate the recorded input from the latest keyframe at or before
    /// that point, one unedited as the control and one with the edits applied ahead of the first tick of the span,
    /// and the edited timeline is compared tick by tick against the recorded authoritative hashes. The edits are the
    /// document changes submitted since the cursor's tick closed or, when none are waiting, the ones recorded at the
    /// cursor's own tick. The live world is untouched.</summary>
    /// <param name="ticksBack">How many ticks earlier the edits land, at least 1.</param>
    /// <param name="documentPath">The boot document's path, which the shadow's machine host reads content beside,
    /// or <see langword="null"/>.</param>
    /// <param name="report">The outcome, on success.</param>
    /// <param name="refusal">Why the replay-edit could not run, when it could not.</param>
    /// <returns><see langword="true"/> when the replay-edit ran.</returns>
    public bool TryReplayEdit(int ticksBack, string? documentPath, out WorldHistoryReplayEditReport? report, out string refusal) {
        report = null;

        if (WindowRefusal() is { } window) {
            refusal = window;

            return false;
        }

        if (ticksBack < 1) {
            refusal = "the span must be at least one tick";

            return false;
        }

        var editTick = m_cursor;

        if (((ulong)ticksBack) > (editTick - Oldest)) {
            refusal = $"{ticksBack} tick(s) before tick {editTick} leaves the window, which reaches back to tick {Oldest}";

            return false;
        }

        var pending = m_tape.OpenAuthority.Where(predicate: IsEdit).ToArray();
        var fromCursorTick = (pending.Length == 0);
        var edits = pending;

        if (fromCursorTick) {
            edits = ((editTick > Oldest)
                ? [.. InputAtTick(tick: editTick).Authority.Where(predicate: IsEdit)]
                : []);
        }

        if (edits.Length == 0) {
            refusal = $"no edit at tick {editTick} to move back — change the document first (world.row.set, world.state.cell.set, …)";

            return false;
        }

        var start = (editTick - ((ulong)ticksBack));
        var keyframe = KeyframeAtOrBefore(tick: start);

        if (RecordedSpanRefusal(from: keyframe.KeyframeTick, to: editTick) is { } spanRefusal) {
            refusal = spanRefusal;
            return false;
        }
        // Edits recorded at the cursor's tick lie inside the span just verified; pending ones were never recorded, so
        // they are verified here, once, like any other entry.
        if (!fromCursorTick) {
            foreach (var edit in edits) {
                if (RecordedEntryRefusal(entry: edit) is { } editRefusal) {
                    refusal = editRefusal;
                    return false;
                }
            }
        }
        ulong? controlDiverged = null;
        ulong? diverged = null;
        WorldHistoryDiff? diff = null;
        var editOutcomes = new List<bool>();

        using (var control = OpenShadow(documentPath: documentPath, segment: keyframe))
        using (var edited = OpenShadow(documentPath: documentPath, segment: keyframe)) {
            var expected = new List<bool>();
            var replayed = new Queue<bool>();
            var editedExpected = new List<bool>();
            var editedReplayed = new Queue<bool>();

            for (var tick = (keyframe.KeyframeTick + 1UL); (tick <= editTick); tick++) {
                var (segment, offset) = Locate(tick: tick);
                var input = InputAt(offset: offset, segment: segment);

                if (StepRecorded(
                    expected: expected,
                    input: input,
                    recordedHash: segment.Hashes[offset],
                    replayed: replayed,
                    server: control.Server,
                    stepTicks: segment.StepTicks[offset],
                    tick: tick
                ) is false) {
                    controlDiverged ??= tick;
                }

                var editedInput = input;

                if (
                    fromCursorTick &&
                    (tick == editTick)
                ) {
                    editedInput = (editedInput with { Authority = [.. editedInput.Authority.Where(predicate: entry => !edits.Contains(value: entry))] });
                }
                if (tick == (start + 1UL)) {
                    editedInput = (editedInput with { Authority = [.. edits, .. editedInput.Authority] });
                }

                editedExpected.Clear();
                WorldReplaySnapshot.ApplyRecordedTick(
                    expectedMutationOutcomes: editedExpected,
                    input: editedInput,
                    population: edited.Server.Population,
                    rebuildSource: VerifiedRebuild,
                    replayedMutationOutcomes: editedReplayed,
                    server: edited.Server
                );
                edited.Server.ReplaysInput = true;
                edited.Server.Advance(stepTicks: segment.StepTicks[offset]);
                edited.Server.EnforceJournalDepth();

                // The moved edits lead the authority list of their landing tick, so their outcomes lead its queue.
                if (tick == (start + 1UL)) {
                    for (var index = 0; ((index < edits.Count(predicate: static edit => (edit is WorldReplayEntry.Mutation))) && editedReplayed.TryDequeue(result: out var accepted)); index++) {
                        editOutcomes.Add(item: accepted);
                    }
                }

                editedReplayed.Clear();
                m_counters = (m_counters with {
                    HashFolds = (m_counters.HashFolds + 1L),
                    TicksResimulated = (m_counters.TicksResimulated + 1L),
                });

                if (
                    (diverged is null) &&
                    (tick > start) &&
                    (WorldStateHashComposition.HashAuthoritative(server: edited.Server, tick: tick) != segment.Hashes[offset])
                ) {
                    diverged = tick;
                    diff = WorldHistoryDiff.Between(
                        from: WorldHistoryImage.Capture(server: control.Server),
                        to: WorldHistoryImage.Capture(server: edited.Server)
                    );
                }
            }
        }

        var described = new List<string>(capacity: edits.Length);
        var channels = m_server.Population.Channels;
        var mutationIndex = 0;

        foreach (var edit in edits) {
            var outcome = string.Empty;

            if (edit is WorldReplayEntry.Mutation) {
                outcome = ((mutationIndex < editOutcomes.Count)
                    ? (editOutcomes[mutationIndex] ? " — accepted" : " — refused")
                    : " — not applied");
                mutationIndex++;
            }

            described.Add(item: $"{WorldReplayEntryDescriber.Describe(channels: channels, entry: edit)}{outcome}");
        }

        report = new WorldHistoryReplayEditReport(
            ControlDivergedAt: controlDiverged,
            Diff: diff,
            DivergedAt: diverged,
            EditTick: editTick,
            Edits: described,
            LandedAt: (start + 1UL)
        );
        refusal = string.Empty;

        return true;
    }

    /// <summary>Gets the ticks of the keyframes the window holds, oldest first.</summary>
    public IReadOnlyList<ulong> KeyframeTicks => [.. m_segments.Select(selector: static segment => segment.KeyframeTick)];

    // The recorded input of one tick inside the window.
    private WorldReplayTickInput InputAtTick(ulong tick) {
        var (segment, offset) = Locate(tick: tick);

        return InputAt(offset: offset, segment: segment);
    }

    /// <summary>Opens an isolated shadow server restored from a held keyframe.</summary>
    /// <param name="keyframeTick">The tick of a held keyframe (<see cref="KeyframeTicks"/>).</param>
    /// <param name="documentPath">The boot document's path, which the shadow's machine host reads content beside,
    /// or <see langword="null"/>.</param>
    /// <returns>The shadow; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No held keyframe sits at <paramref name="keyframeTick"/>.</exception>
    public WorldHistoryShadow OpenShadow(ulong keyframeTick, string? documentPath) {
        foreach (var segment in m_segments) {
            if (segment.KeyframeTick == keyframeTick) {
                return OpenShadow(
                    documentPath: documentPath,
                    segment: segment
                );
            }
        }

        throw new ArgumentOutOfRangeException(paramName: nameof(keyframeTick), message: $"no keyframe is held at tick {keyframeTick}");
    }
    /// <summary>Returns the recorded input of one tick, in the replay tape's own entries.</summary>
    /// <param name="tick">A recorded tick after the oldest keyframe and at or before the head.</param>
    /// <returns>The tick's authority entries and intents.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is outside the recorded ticks.</exception>
    public WorldReplayTickInput RecordedInput(ulong tick) {
        if (
            (m_segments.Count == 0) ||
            (tick <= Oldest) ||
            (tick > Head)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(tick));
        }

        return InputAtTick(tick: tick);
    }
    /// <summary>Returns the authoritative hash the history recorded at a tick of the window.</summary>
    /// <param name="tick">A tick from the oldest keyframe to the head.</param>
    /// <returns>The recorded hash.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is outside the window.</exception>
    public ulong RecordedHash(ulong tick) {
        if (
            (m_segments.Count == 0) ||
            (tick < Oldest) ||
            (tick > Head)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(tick));
        }

        return RecordedHashAt(tick: tick);
    }
    /// <summary>Re-simulates the recorded ticks after <paramref name="from"/> through <paramref name="to"/> on a
    /// shadow server already holding the state of <paramref name="from"/>, through the doors and in the order the
    /// live step took them, and proves each tick's mutation outcomes and authoritative hash against the recording.
    /// <paramref name="rewrite"/> lets a tool feed a tick different input than was recorded — the edit a what-if
    /// asks about, or a planted defect a law expects the proof to catch.</summary>
    /// <param name="shadow">The shadow to step.</param>
    /// <param name="from">The tick the shadow's state describes.</param>
    /// <param name="to">The last tick to re-simulate, at most the head.</param>
    /// <param name="rewrite">Maps a tick and its recorded input to the input fed instead, or
    /// <see langword="null"/> to feed the recording.</param>
    /// <param name="observe">Called after each re-simulated tick with the tick and the stepped server.</param>
    /// <returns>The first tick that disagreed with the recording, or <see langword="null"/> when every tick
    /// matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shadow"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span leaves the window.</exception>
    /// <exception cref="InvalidOperationException">The span holds an entry history cannot re-simulate — a crossing, a
    /// screen operation, an addon edit, or a recorded reload whose content no longer matches its recorded hash. The
    /// span is checked, and every reload it names read once, before the shadow moves.</exception>
    public ulong? Resimulate(WorldHistoryShadow shadow, ulong from, ulong to, Func<ulong, WorldReplayTickInput, WorldReplayTickInput>? rewrite = null, Action<ulong, WorldServer>? observe = null) {
        ArgumentNullException.ThrowIfNull(argument: shadow);

        if (
            (m_segments.Count == 0) ||
            (from < Oldest) ||
            (to > Head) ||
            (from > to)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(to));
        }

        if (RecordedSpanRefusal(from: from, to: to) is { } refusal) {
            throw new InvalidOperationException(message: refusal);
        }

        return Resimulate(
            from: from,
            observe: observe,
            rewrite: rewrite,
            server: shadow.Server,
            to: to
        );
    }
}
