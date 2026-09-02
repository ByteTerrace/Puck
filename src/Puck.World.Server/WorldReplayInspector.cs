using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A saved tape as <see cref="WorldReplayInspector.Load"/> reads it back: the decoded recording plus the two
/// leading shape-identity words exactly as the file carries them (<see cref="WorldReplaySnapshot.Read"/> has already
/// refused a disagreement with this build's own pinned pair by the time this exists, so the header line prints what
/// the file says rather than what the build expects).</summary>
/// <param name="Recording">The decoded recording.</param>
/// <param name="Magic">The tape's leading magic word.</param>
/// <param name="ShapeToken">The tape's shape token.</param>
/// <param name="Path">The on-disk path the tape was read from.</param>
public readonly record struct WorldReplayLoad(WorldReplaySnapshot Recording, uint Magic, uint ShapeToken, string Path);
/// <summary>
/// The tape's read-back — what <c>replay.inspect</c> renders. A tape nobody can read is evidence nobody can weigh, so
/// this walks a saved <see cref="WorldReplaySnapshot"/> and prints its header facts (shape, rate, tick count, the
/// pinned seats and mounted-addon receipts) and then, per tick, the recorded pose hash beside whatever CHANGED that
/// tick: every authority/server-event entry, compactly named, and every intent channel whose value moved from the
/// entity's previous submission. The default walk prints only ticks carrying such an edge (an unchanged held stick
/// across 90 ticks is one line, not ninety); <c>--all</c> prints every tick.
/// <para><c>--poses</c> additionally re-drives the tape through the SAME offline shadow drive <c>replay.verify</c>
/// runs (<see cref="WorldReplaySnapshot.Drive"/>, untouched) and prints each active body's <c>body.where</c>-style
/// pose beside every printed tick. The per-tick observer rides the addon seam's third pump point
/// (<see cref="IWorldAddonHost.ResolveReads"/> — after the population advances, before the snapshot) through a
/// forwarding host wrapped around the ordinary factory's product, so verify's own behavior is not touched; the
/// observation point is PROVEN each drive by recomputing <see cref="WorldReplaySnapshot.HashState"/> there and
/// refusing by name if it ever disagrees with the trace <c>Drive</c> itself returns.</para>
/// </summary>
public sealed class WorldReplayInspector {
    private const string Prefix = "[replay.inspect: ";
    // The bracketed body.where echo's own fixed prefix — sliced off so a pose rides inside this verb's one line per
    // tick rather than nesting a second bracketed echo in it. Pinned beside WorldBody.DescribeWhere's own format.
    private const string WherePrefix = "[body.where: ";

    private readonly Func<WorldDefinition, WorldServer, IWorldAddonHost> m_addonHostFactory;
    private readonly IReadOnlyList<IScreenMachineEngine> m_engines;
    private readonly WorldOwnedWorlds m_profiles;

    /// <summary>Initializes the inspector over the same three things a re-drive needs — the profile catalog seats
    /// re-resolve against, the screen-machine engine set, and the shadow addon-host factory — so <c>--poses</c>
    /// drives a tape exactly the way <c>replay.verify</c> does.</summary>
    /// <param name="profiles">The profile catalog (handed to <see cref="WorldReplaySnapshot.Drive"/>).</param>
    /// <param name="engines">The registered screen-machine engines (handed to <see cref="WorldReplaySnapshot.Drive"/>).</param>
    /// <param name="addonHostFactory">Builds the shadow addon host over a re-deserialized definition and its shadow
    /// server — wrapped here in the per-tick observer, never replaced.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldReplayInspector(WorldOwnedWorlds profiles, IEnumerable<IScreenMachineEngine> engines, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: addonHostFactory);

        m_profiles = profiles;
        m_engines = [.. engines];
        m_addonHostFactory = addonHostFactory;
    }

    private static void AppendHeader(List<string> lines, string name, in WorldReplayLoad loaded) {
        var recording = loaded.Recording;

        lines.Add(item: $"{Prefix}tape '{name}' path={loaded.Path}]");
        lines.Add(item: $"{Prefix}shape magic=0x{loaded.Magic:X8} token={loaded.ShapeToken}]");
        lines.Add(item: $"{Prefix}rate {recording.SimulationRate} Hz | ticks {recording.TickCount} | tail hash 0x{recording.RecordedTailHash:X16}]");

        if (recording.ForkedFrom is { } fork) {
            lines.Add(item: $"{Prefix}forked from '{fork.ParentName}' at tick {fork.Tick}]");
        }

        foreach (var seat in recording.Seats) {
            lines.Add(item: ((seat.Profile is { } pin)
                ? $"{Prefix}seat slot={seat.Slot} profile='{pin.Name}' move={DescribeRate(rate: pin.MoveSpeed)} turn={DescribeRate(rate: pin.TurnSpeed)}]"
                : $"{Prefix}seat slot={seat.Slot} profile=none]"
            ));
        }

        if (recording.MountedAddons.Count == 0) {
            lines.Add(item: $"{Prefix}addons none]");
        }

        foreach (var receipt in recording.MountedAddons) {
            lines.Add(item: $"{Prefix}addon '{receipt.Name}' hash={receipt.Hash} fuel={receipt.Fuel}/tick]");
        }
    }
    // A pinned rate as the operator reads it beside the raw lane the re-drive actually integrates with; 'kit' for an
    // identity that claimed none (the re-drive falls back to the kit's rate the same way the live run did).
    private static string DescribeRate(FixedQ4816? rate) {
        return ((rate is { } value)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((double)value):0.####} (raw {value.Value})"
            )
            : "kit"
        );
    }

    /// <summary>Renders the per-tick lines — the pure walk <c>replay.inspect</c> prints after its header, exposed on
    /// its own so a law can drive it over a hand-built tape. Walks EVERY tick from 0 (an intent edge is a change
    /// against the entity's previous submission, wherever that submission was) but prints only ticks inside
    /// <paramref name="from"/>..<paramref name="to"/>, and among those only ticks carrying an edge, an authority
    /// entry, or — when <paramref name="poses"/> is supplied — a recorded hash that moved from the previous tick's,
    /// unless <paramref name="all"/>.</summary>
    /// <param name="lines">The line sink.</param>
    /// <param name="ticks">The tape's per-tick input.</param>
    /// <param name="hashes">The tape's recorded per-tick pose hashes (one per tick).</param>
    /// <param name="channels">The recorded world's compiled channel table — what names an intent ordinal.</param>
    /// <param name="from">The first tick to print (inclusive).</param>
    /// <param name="to">The last tick to print (inclusive; clamped to the tape).</param>
    /// <param name="all">Whether to print every tick in range rather than only ticks carrying an edge.</param>
    /// <param name="poses">Per-tick re-driven poses to print beside each line, or <see langword="null"/>.</param>
    /// <param name="divergedAt">The first tick the re-driven trace disagrees with the recorded one, or <c>-1</c>.</param>
    public static void AppendTicks(List<string> lines, IReadOnlyList<WorldReplayTickInput> ticks, IReadOnlyList<ulong> hashes, WorldChannelTable channels, int from, int to, bool all, IReadOnlyList<string[]?>? poses, int divergedAt) {
        ArgumentNullException.ThrowIfNull(argument: lines);
        ArgumentNullException.ThrowIfNull(argument: ticks);
        ArgumentNullException.ThrowIfNull(argument: hashes);
        ArgumentNullException.ThrowIfNull(argument: channels);

        // The last submitted intent per entity — the "previous tick" an edge is measured against. Starts at the zero
        // vector, which is what a body reads before its first submission, so a tape whose first tick already holds a
        // stick prints that as the edge it is.
        var previous = new PlayerIntent[WorldBodiesLimits.CapacityCeiling];
        var previousHeld = new PlayerIntent[WorldBodiesLimits.CapacityCeiling];
        var last = Math.Min(
            val1: to,
            val2: (ticks.Count - 1)
        );
        var edges = new StringBuilder();

        for (var tick = 0; (tick <= last); tick++) {
            var input = ticks[tick];

            edges.Clear();

            foreach (var submission in input.Intents) {
                // Both lanes of a submission: the composed device intent, and the client's held-control overlay
                // (a bound held verb), the latter named `held.<channel>` so the two never read as one.
                AppendIntentEdge(
                    after: submission.Intent,
                    channels: channels,
                    edges: edges,
                    index: submission.EntityIndex,
                    lane: "",
                    previous: previous
                );
                AppendIntentEdge(
                    after: submission.HeldChannels,
                    channels: channels,
                    edges: edges,
                    index: submission.EntityIndex,
                    lane: "held.",
                    previous: previousHeld
                );
            }

            if (tick < from) {
                continue;
            }

            // With poses requested, a pose that moved is itself an edge: the recorded hash differing from the previous
            // tick's (tick 0 against the boot image always counts) — a body advancing under a held stick prints every
            // tick it moves, a body at rest prints none.
            var poseMoved = (
                (poses is not null) &&
                (
                    (tick == 0) ||
                    (hashes[tick] != hashes[(tick - 1)])
                )
            );
            var carries = (
                (edges.Length > 0) ||
                (input.Authority.Count > 0) ||
                (tick == divergedAt) ||
                poseMoved
            );

            if (
                !all &&
                !carries
            ) {
                continue;
            }

            lines.Add(item: DescribeTick(
                authority: input.Authority,
                channels: channels,
                diverged: (tick == divergedAt),
                edges: edges,
                hash: hashes[tick],
                poses: ((poses is not null) && (tick < poses.Count)
                    ? poses[tick]
                    : null),
                posesRequested: (poses is not null),
                tick: tick
            ));
        }
    }
    // One entity's changed channels against its previous submission — appended as "p1 forward=1 strafe=-0.5" (seats)
    // or "body:7 turn=0.25" (everything else); nothing when the vector is unchanged. `previous` is advanced either way.
    private static void AppendIntentEdge(WorldChannelTable channels, StringBuilder edges, PlayerIntent[] previous, int index, PlayerIntent after, string lane) {
        if (((uint)index) >= ((uint)previous.Length)) {
            return;
        }

        var before = previous[index];
        var labelled = false;

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (before[ordinal] == after[ordinal]) {
                continue;
            }

            if (!labelled) {
                if (edges.Length > 0) {
                    edges.Append(value: ' ');
                }

                edges.Append(value: DescribeEntity(index: index));
                labelled = true;
            }

            edges.Append(value: ' ');
            edges.Append(value: lane);
            edges.Append(value: (channels.Name(ordinal: ordinal) ?? $"ch{ordinal}"));
            edges.Append(value: '=');
            edges.Append(value: DescribeValue(value: after[ordinal]));
        }

        previous[index] = after;
    }
    private static string DescribeTick(int tick, ulong hash, IReadOnlyList<WorldReplayEntry> authority, WorldChannelTable channels, StringBuilder edges, string[]? poses, bool posesRequested, bool diverged) {
        var line = new StringBuilder();

        line.Append(value: Prefix);
        line.Append(value: "tick ");
        line.Append(value: tick);
        line.Append(value: " hash=0x");
        line.Append(value: hash.ToString(
            format: "X16",
            provider: CultureInfo.InvariantCulture
        ));

        if (authority.Count > 0) {
            line.Append(value: " | ");

            for (var index = 0; (index < authority.Count); index++) {
                if (index > 0) {
                    line.Append(value: "; ");
                }

                line.Append(value: WorldReplayEntryDescriber.Describe(
                    channels: channels,
                    entry: authority[index]
                ));
            }
        }

        if (edges.Length > 0) {
            line.Append(value: " | ");
            line.Append(value: edges);
        }

        if (posesRequested) {
            line.Append(value: " | ");

            if (poses is null) {
                line.Append(value: "(no pose observed)");
            } else {
                for (var index = 0; (index < poses.Length); index++) {
                    if (index > 0) {
                        line.Append(value: ' ');
                    }

                    line.Append(value: poses[index]);
                }
            }
        }

        if (diverged) {
            line.Append(value: " | DIVERGED — the re-driven hash first disagrees with the recorded one here");
        }

        line.Append(value: ']');

        return line.ToString();
    }
    /// <summary>Labels an entity the way the console addresses it — <c>p1</c>..<c>p4</c> for a local seat (the
    /// 1-based player display index), <c>body:N</c> (0-based) for everything else.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The label.</returns>
    public static string DescribeEntity(int index) => ((index < WorldBodiesLimits.LocalSeatCount)
        ? $"p{(index + 1)}"
        : $"body:{index}"
    );
    /// <summary>Renders a raw intent lane as the operator typed it — <c>1</c>, <c>-0.5</c>, <c>0.25</c> — never the
    /// raw fixed lane, which is the codec's currency rather than the console's.</summary>
    /// <param name="value">The channel value.</param>
    /// <returns>The decimal.</returns>
    public static string DescribeValue(FixedQ4816 value) => ((double)value).ToString(
        format: "0.####",
        provider: CultureInfo.InvariantCulture
    );

    private static string[] CapturePoses(WorldPopulation population) {
        var poses = new List<string>();

        for (var index = 0; (index < population.Capacity); index++) {
            if (
                !population.IsActive(index: index) ||
                (population.EntryBody(index: index) is not { } body)
            ) {
                continue;
            }

            var where = body.DescribeWhere(index: index);

            // The standalone echo is "[body.where: body:N pos=(…) yaw=…° pitch=…° roll=…°]" — keep exactly its
            // body-and-pose payload so the line reads like body.where without nesting a second bracketed echo.
            poses.Add(item: ((where.StartsWith(
                value: WherePrefix,
                comparisonType: StringComparison.Ordinal
            ) && where.EndsWith(value: ']'))
                ? where[WherePrefix.Length..^1]
                : where
            ));
        }

        return [.. poses];
    }
    // Re-drives the tape through the untouched WorldReplaySnapshot.Drive, observing every tick's post-step population
    // at the addon seam's third pump point. Returns the per-tick poses and Drive's own trace.
    private (string[]?[] Poses, ulong[] Trace) DrivePoses(WorldReplaySnapshot recording) {
        var poses = new string[]?[recording.TickCount];
        var observed = new ulong[recording.TickCount];
        var observedCount = 0;
        WorldPopulation? shadow = null;
        var trace = recording.Drive(
            addonHostFactory: (definition, server) => {
                shadow = server.Population;

                return new ObservingAddonHost(
                    inner: m_addonHostFactory(
                        definition,
                        server
                    ),
                    onResolved: tick => {
                        // ResolveReads receives context.Tick + 1 (the tick that just advanced, 1-based).
                        var index = ((int)(tick - 1UL));

                        if (
                            (((uint)index) >= ((uint)poses.Length)) ||
                            (shadow is not { } population)
                        ) {
                            return;
                        }

                        poses[index] = CapturePoses(population: population);
                        observed[index] = WorldReplaySnapshot.HashState(population: population);
                        observedCount = Math.Max(
                            val1: observedCount,
                            val2: (index + 1)
                        );
                    }
                );
            },
            engines: m_engines,
            profiles: m_profiles
        );

        // The observation point is a CLAIM about WorldServer.Step's internal order (ResolveReads runs after the
        // population advances) — prove it every drive rather than trust it: the hash recomputed at the observer must
        // be the hash Drive itself folded after the same Step, tick for tick, or the poses printed here would be a
        // tick stale and silently wrong.
        if (observedCount != trace.Length) {
            throw new InvalidOperationException(message: $"the --poses observer saw {observedCount} of {trace.Length} re-driven ticks — the shadow server's addon seam did not reach ResolveReads on every step, so the poses cannot be trusted; this is a host bug, not tape data.");
        }

        var disagreement = HashTrace.FirstDivergence(
            left: trace,
            right: observed
        );

        if (disagreement >= 0) {
            throw new InvalidOperationException(message: $"the --poses observer's hash at tick {disagreement} (0x{observed[disagreement]:X16}) disagrees with the re-drive's own trace (0x{trace[disagreement]:X16}) — the observation point (IWorldAddonHost.ResolveReads) no longer sits after pose integration, so the poses would be stale; this is a host bug, not tape data.");
        }

        return (poses, trace);
    }
    private static void AppendDriveVerdict(List<string> lines, WorldReplaySnapshot recording, ulong[] trace, string[]?[] poses, int divergedAt) {
        if (divergedAt < 0) {
            lines.Add(item: $"{Prefix}pose re-drive MATCH over {recording.TickCount} ticks | tail 0x{recording.RecordedPoseTailHash:X16}]");

            return;
        }

        var recorded = ((divergedAt < recording.RecordedHashes.Length)
            ? $"0x{recording.RecordedHashes[divergedAt]:X16}"
            : "(none — the re-drive ran past the recorded trace)"
        );
        var replayed = ((divergedAt < trace.Length)
            ? $"0x{trace[divergedAt]:X16}"
            : "(none — the re-drive stopped short of the recorded trace)"
        );
        var bodies = (((divergedAt < poses.Length) && (poses[divergedAt] is { } atDivergence))
            ? string.Join(
                separator: " ",
                values: atDivergence
            )
            : "(no pose observed)"
        );

        // The tape pins ONE hash per tick, never a per-body pose, so the body cannot be named from the tape alone;
        // what CAN be shown is every body the re-drive holds at that tick — the operator compares against the live
        // session's own body.where at the same tick.
        lines.Add(item: $"{Prefix}re-drive DIVERGED first at tick {divergedAt} of {recording.TickCount} | recorded {recorded} replayed {replayed} | re-driven bodies there: {bodies} — the tape pins only the hash per tick, so the diverging body is read against the live session's body.where at that tick, never off the tape]");
    }

    /// <summary>Reads a saved tape by name — its leading shape words verbatim, then the decoded recording through
    /// <see cref="WorldReplaySnapshot.Read"/> (which refuses a foreign shape by name before anything else decodes).</summary>
    /// <param name="name">The saved recording's name (already validated by <see cref="WorldReplayTape.IsValidName"/>).</param>
    /// <returns>The loaded tape.</returns>
    /// <exception cref="FileNotFoundException">No recording of that name exists.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable <c>.puckreplay</c> tape.</exception>
    public static WorldReplayLoad Load(string name) {
        var path = WorldReplayTape.PathFor(name: name);

        using var stream = File.OpenRead(path: path);

        Span<byte> header = stackalloc byte[8];

        try {
            stream.ReadExactly(buffer: header);
        } catch (EndOfStreamException exception) {
            throw new InvalidDataException(
                message: "Corrupt .puckreplay recording (shorter than its own shape header).",
                innerException: exception
            );
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source: header);
        var shapeToken = BinaryPrimitives.ReadUInt32LittleEndian(source: header[4..]);

        stream.Position = 0L;

        return new WorldReplayLoad(
            Magic: magic,
            Path: path,
            Recording: WorldReplaySnapshot.Read(stream: stream),
            ShapeToken: shapeToken
        );
    }
    /// <summary>Renders the whole read-back: the header (one line per fact), the per-tick walk over
    /// <paramref name="from"/>..<paramref name="to"/>, and — with <paramref name="poses"/> — the re-drive's own
    /// verdict line naming the first divergent tick, if any.</summary>
    /// <param name="name">The tape's name (for the header).</param>
    /// <param name="loaded">The loaded tape.</param>
    /// <param name="from">The first tick to print (the caller has already refused one beyond the tape).</param>
    /// <param name="to">The last tick to print (clamped to the tape here).</param>
    /// <param name="all">Whether to print every tick rather than only ticks carrying an edge.</param>
    /// <param name="poses">Whether to re-drive the tape and print each active body's pose beside every line.</param>
    /// <returns>The lines, in print order.</returns>
    /// <exception cref="InvalidDataException">The re-drive refused the tape by name (the mount pin, the rate pin, a
    /// CAS pin, a mutation-outcome pin).</exception>
    /// <exception cref="WorldReplayCodecException">The re-drive hit a host-side codec bug.</exception>
    /// <exception cref="InvalidOperationException">The pose observer could not prove its observation point.</exception>
    public IReadOnlyList<string> Inspect(string name, in WorldReplayLoad loaded, int from, int to, bool all, bool poses) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        var recording = loaded.Recording;
        var lines = new List<string>();

        AppendHeader(
            lines: lines,
            loaded: in loaded,
            name: name
        );

        var last = Math.Min(
            val1: to,
            val2: Math.Max(
                val1: (recording.TickCount - 1),
                val2: 0
            )
        );

        lines.Add(item: $"{Prefix}range {from}-{last} of {recording.TickCount} | {(all
            ? "every tick"
            : "edges only")} | poses {(poses
            ? "on"
            : "off")}]");

        var channels = WorldChannelTable.Compile(channels: WorldDefinitionSerialization.Deserialize(utf8Json: recording.DefinitionJson).Channels);
        string[]?[]? drivenPoses = null;
        ulong[]? trace = null;
        var divergedAt = -1;

        if (poses) {
            (drivenPoses, trace) = DrivePoses(recording: recording);
            divergedAt = HashTrace.FirstDivergence(
                left: recording.RecordedHashes,
                right: trace
            );
        }

        AppendTicks(
            all: all,
            channels: channels,
            divergedAt: divergedAt,
            from: from,
            hashes: recording.RecordedHashes,
            lines: lines,
            poses: drivenPoses,
            ticks: recording.Ticks,
            to: last
        );

        if (
            (drivenPoses is not null) &&
            (trace is not null)
        ) {
            AppendDriveVerdict(
                divergedAt: divergedAt,
                lines: lines,
                poses: drivenPoses,
                recording: recording,
                trace: trace
            );
        }

        return lines;
    }

    // A forwarding host: every member reaches the ordinary factory's product untouched, and ResolveReads — pump point
    // 3, after the population advanced — additionally fires the observer. Wrapping rather than subclassing keeps the
    // shadow drive's own guest set (and its receipts, which the mount pin compares) exactly what the factory built.
    private sealed class ObservingAddonHost(IWorldAddonHost inner, Action<ulong> onResolved) : IWorldAddonHost {
        private readonly IWorldAddonHost m_inner = inner;
        private readonly Action<ulong> m_onResolved = onResolved;

        /// <inheritdoc/>
        public bool AnyEverPumped => m_inner.AnyEverPumped;
        /// <inheritdoc/>
        public int MountedCount => m_inner.MountedCount;
        /// <inheritdoc/>
        public IReadOnlyList<WorldAddonReceipt> Receipts => m_inner.Receipts;

        /// <inheritdoc/>
        public void ApplyContributions(ulong tick) => m_inner.ApplyContributions(tick: tick);
        /// <inheritdoc/>
        public void Commit(IWorldAddonPreparedPlan plan) => m_inner.Commit(plan: plan);
        /// <inheritdoc/>
        public void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied) => m_inner.CompleteMutation(
            actOrdinal: actOrdinal,
            addonInstanceId: addonInstanceId,
            applied: applied
        );
        /// <inheritdoc/>
        public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) => m_inner.DescribeUndeclaredGrantedChannels(
            channels: channels,
            principal: principal,
            reach: reach
        );
        /// <inheritdoc/>
        public void Dispose() => m_inner.Dispose();
        /// <inheritdoc/>
        public void Finish(IWorldAddonPreparedPlan plan) => m_inner.Finish(plan: plan);
        /// <inheritdoc/>
        public void ResolveReads(ulong tick) {
            m_inner.ResolveReads(tick: tick);
            m_onResolved(tick);
        }
        /// <inheritdoc/>
        public void TickAddons(ulong tick) => m_inner.TickAddons(tick: tick);
        /// <inheritdoc/>
        public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason) => m_inner.TryPrepare(
            candidate: candidate,
            current: current,
            plan: out plan,
            reason: out reason
        );
    }
}
