using Puck.Maths;
using System.Text;

namespace Puck.World.Server;

/// <summary>The named live-state boundaries exposed by <c>world.state.hash</c>.</summary>
public enum WorldStateHashScope : byte {
    /// <summary>The historical capture digest: the pose digest followed by resolved <c>state.world</c> values.</summary>
    Capture,
    /// <summary>Only active-body authoritative poses, identical to <see cref="WorldReplaySnapshot.HashState"/>.</summary>
    Pose,
    /// <summary>The complete document-owned <c>state.world</c> substrate and its values at the requested tick.</summary>
    World,
    /// <summary><see cref="World"/> plus poses, rule latches, body/identity action state, and live field cells.</summary>
    Authoritative,
}

/// <summary>Computes deterministic hashes over explicitly named live-state boundaries.</summary>
public static partial class WorldRuntimeStateHash {
    private const ulong WorldDomain = 0x574f524c44535431UL; // "WORLDST1"
    private const ulong AuthoritativeDomain = 0x4155544853543031UL; // "AUTHST01"

    /// <summary>Computes one named state scope. <see cref="WorldStateHashScope.Capture"/> preserves the historical
    /// capture-manifest fold exactly.</summary>
    public static ulong Hash(WorldServer server, ulong tick, WorldStateHashScope scope) {
        ArgumentNullException.ThrowIfNull(argument: server);

        return scope switch {
            WorldStateHashScope.Capture => HashCapture(server: server, tick: tick),
            WorldStateHashScope.Pose => WorldReplaySnapshot.HashState(population: server.Population),
            WorldStateHashScope.World => HashWorld(server: server, tick: tick),
            WorldStateHashScope.Authoritative => HashAuthoritative(server: server, tick: tick),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(scope)),
        };
    }

    /// <summary>Hashes the document-owned state substrate, including stored traits and the values they resolve to at
    /// <paramref name="tick"/>. Declaration order and cell order are significant.</summary>
    public static ulong HashWorld(WorldServer server, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var hash = Fnv1aHash.Create();

        hash.Add(value: WorldDomain);
        AppendWorld(
            hash: ref hash,
            server: server,
            tick: tick
        );

        return hash.Value;
    }

    /// <summary>Hashes the state system's authoritative live lanes: world rows and traits, fields, rule/interaction
    /// latches, body action, cached navigation and flock perception, slot generations, prior travel, and poses. The rest of the world document, grants, presentation caches, pending
    /// transport work, diagnostics, and screen-machine cores are deliberately outside this boundary.</summary>
    public static ulong HashAuthoritative(WorldServer server, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var hash = Fnv1aHash.Create();

        hash.Add(value: AuthoritativeDomain);
        hash.Add(value: tick);
        hash.Add(value: WorldReplaySnapshot.HashState(population: server.Population));
        AppendWorld(
            hash: ref hash,
            server: server,
            tick: tick
        );
        server.AppendStateFeatureHash(hash: ref hash);

        return hash.Value;
    }

    private static ulong HashCapture(WorldServer server, ulong tick) {
        var hash = Fnv1aHash.Create();
        var catalog = server.Definition.StateCatalog;
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(charCount: WorldStateCapacity.MaxTextValueLength)];

        hash.Add(value: WorldReplaySnapshot.HashState(population: server.Population));

        foreach (var row in server.Definition.State) {
            var cells = row.Cells ?? [];

            if (!catalog.TryResolve(
                lane: WorldStateOwnershipLane.World,
                name: row.Name,
                handle: out var handle
            )) {
                continue;
            }

            for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
                var cell = cells[cellIndex];

                if (!WorldStateReader.TryReadHandle(
                    catalog: catalog,
                    definition: server.Definition,
                    handle: handle,
                    key: cell.Key,
                    rawValue: out var rawValue,
                    row: out _,
                    text: out var text,
                    tick: tick
                )) {
                    continue;
                }

                hash.Add(value: (rawValue ?? 0L));

                if (
                    (row.Kind == CellKind.Text) &&
                    (text is { Length: > 0 })
                ) {
                    var written = Encoding.UTF8.GetBytes(
                        chars: text.AsSpan(),
                        bytes: utf8
                    );

                    hash.Add(values: utf8[..written]);
                }
            }
        }

        return hash.Value;
    }

    private static void AppendAdvance(ref Fnv1aHash hash, WorldStateAdvance? advance) {
        hash.Add(value: ((byte)(advance is null ? 0 : 1)));

        if (advance is not null) {
            hash.Add(value: advance.RateNumerator);
            hash.Add(value: advance.RateDenominator);
            hash.Add(value: advance.EpochTick);
        }
    }
    private static void AppendCycle(ref Fnv1aHash hash, WorldStateCycle? cycle) {
        hash.Add(value: ((byte)(cycle is null ? 0 : 1)));

        if (cycle is not null) {
            var word = cycle.Word;

            hash.Add(value: ((uint)(word?.Count ?? 0)));

            if (word is not null) {
                foreach (var letter in word) { hash.Add(value: letter); }
            }

            hash.Add(value: cycle.Power);
            hash.Add(value: ((byte)cycle.Output));
            hash.Add(value: cycle.TicksPerStep);
            hash.Add(value: cycle.EpochTick);
            hash.Add(value: cycle.SubstepTicks);
        }
    }
    private static void AppendDynamics(ref Fnv1aHash hash, WorldStateDynamics? dynamics) {
        hash.Add(value: ((byte)(dynamics is null ? 0 : 1)));

        if (dynamics is not null) {
            AppendString(hash: ref hash, value: dynamics.Row);
            hash.Add(value: dynamics.Y0);
            hash.Add(value: dynamics.V0);
            hash.Add(value: dynamics.EpochTick);
        }
    }
    private static void AppendDraw(ref Fnv1aHash hash, WorldDraw? draw) {
        hash.Add(value: ((byte)(draw is null ? 0 : 1)));

        if (draw is not null) {
            AppendString(hash: ref hash, value: draw.Source?.Value);
            AppendGenerator(hash: ref hash, generator: draw.Generator);
            hash.Add(value: ((byte)draw.Timing));
            hash.Add((byte)(draw.Secret is null ? 0 : 1));
            if (draw.Secret is { } secret) { hash.Add(secret.Word0); hash.Add(secret.Word1); hash.Add(secret.Word2); hash.Add(secret.Word3); }
        }
    }
    private static void AppendGenerator(ref Fnv1aHash hash, WorldGenerator? generator) {
        hash.Add(value: ((byte)(generator is null ? 0 : 1)));

        if (generator is null) {
            return;
        }

        hash.Add(value: ((byte)generator.Source));
        AppendString(hash: ref hash, value: generator.Start?.Value);
        hash.Add(value: ((uint)generator.Bound));
        hash.Add(value: ((byte)generator.Mode));
        hash.Add(value: ((byte)(generator.RangeMin is null ? 0 : 1)));
        hash.Add(value: (generator.RangeMin ?? 0L));
        hash.Add(value: ((byte)(generator.RangeMax is null ? 0 : 1)));
        hash.Add(value: (generator.RangeMax ?? 0L));

        var contexts = generator.Contexts ?? [];

        hash.Add(value: ((uint)contexts.Count));

        for (var contextIndex = 0; (contextIndex < contexts.Count); contextIndex++) {
            var context = contexts[contextIndex];
            var alternatives = context.Alternatives ?? [];

            AppendString(hash: ref hash, value: context.Key.Value);
            hash.Add(value: ((uint)alternatives.Count));

            for (var alternativeIndex = 0; (alternativeIndex < alternatives.Count); alternativeIndex++) {
                var alternative = alternatives[alternativeIndex];

                AppendString(hash: ref hash, value: alternative.Token);
                hash.Add(value: alternative.Weight);
                AppendString(hash: ref hash, value: alternative.Next.Value);
                hash.Add(value: ((byte)(alternative.Count is null ? 0 : 1)));
                hash.Add(value: ((uint)(alternative.Count ?? 0)));
            }
        }

        var weighted = generator.Weighted ?? [];

        hash.Add(value: ((uint)weighted.Count));

        for (var weightedIndex = 0; (weightedIndex < weighted.Count); weightedIndex++) {
            var outcome = weighted[weightedIndex];

            hash.Add(value: outcome.Value);
            hash.Add(value: outcome.Weight);
            hash.Add(value: ((byte)(outcome.Count is null ? 0 : 1)));
            hash.Add(value: ((uint)(outcome.Count ?? 0)));
        }
    }
    private static void AppendLattice(ref Fnv1aHash hash, WorldStateLatticeTrait? lattice) {
        hash.Add(value: ((byte)(lattice is null ? 0 : 1)));

        if (lattice is null) {
            return;
        }

        AppendString(hash: ref hash, value: lattice.Topology);
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Initial));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Min));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Max));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.HeightScale));
        AppendString(hash: ref hash, value: lattice.Color);
        hash.Add(value: ((byte)(lattice.Medium is null ? 0 : 1)));

        var paint = lattice.Paint ?? [];

        hash.Add(value: ((uint)paint.Count));

        for (var paintIndex = 0; (paintIndex < paint.Count); paintIndex++) {
            switch (paint[paintIndex]) {
                case WorldLatticeFill.Rect rect:
                    hash.Add(value: ((byte)1));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.Value));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MinX));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MinZ));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MaxX));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MaxZ));
                    break;
                case WorldLatticeFill.Noise noise:
                    hash.Add(value: ((byte)2));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: noise.Value));
                    hash.Add(value: ((uint)noise.Frequency));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: noise.Threshold));
                    hash.Add(value: ((uint)noise.Octaves));
                    hash.Add(value: noise.Seed);
                    break;
                case WorldLatticeFill.Scatter scatter:
                    hash.Add(value: ((byte)3));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: scatter.Value));
                    hash.Add(value: ((uint)scatter.Spacing));
                    hash.Add(value: ((uint)scatter.Radius));
                    hash.Add(value: scatter.Seed);
                    break;
                case WorldLatticeFill.Draw draw:
                    hash.Add(value: ((byte)4));
                    AppendString(hash: ref hash, value: draw.Source?.Value);
                    AppendGenerator(hash: ref hash, generator: draw.Generator);
                    break;
                default:
                    throw new InvalidOperationException(message: $"unsupported lattice fill '{paint[paintIndex].GetType().Name}'");
            }
        }
    }
    private static void AppendString(ref Fnv1aHash hash, string? value) {
        if (value is null) {
            hash.Add(value: uint.MaxValue);
            return;
        }

        hash.Add(value: ((uint)value.Length));

        foreach (var character in value) {
            hash.Add(value: ((uint)character));
        }
    }
    private static void AppendWorld(ref Fnv1aHash hash, WorldServer server, ulong tick) {
        AppendDiscreteTopologies(ref hash, server.Definition.StateRaw);
        var rows = server.Definition.State;
        var catalog = server.Definition.StateCatalog;

        hash.Add(value: ((uint)rows.Count));

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];
            var cells = row.Cells ?? [];
            var hasHandle = catalog.TryResolve(
                lane: WorldStateOwnershipLane.World,
                name: row.Name,
                handle: out var handle
            );

            AppendString(hash: ref hash, value: row.Name.Value);
            hash.Add(value: ((byte)row.Kind));
            hash.Add(value: ((byte)(row.Min is null ? 0 : 1)));
            hash.Add(value: (row.Min ?? 0L));
            hash.Add(value: ((byte)(row.Max is null ? 0 : 1)));
            hash.Add(value: (row.Max ?? 0L));
            hash.Add(value: ((byte)(row.Capacity is null ? 0 : 1)));
            hash.Add(value: ((uint)(row.Capacity ?? 0)));
            hash.Add(value: ((byte)(row.NonNegative ? 1 : 0)));
            hash.Add(value: ((byte)(row.GatesDrive ? 1 : 0)));
            hash.Add(value: ((byte)(row.Evicts ? 1 : 0)));
            hash.Add(value: row.DrawCursor);
            AppendDiscreteRow(ref hash, row);
            AppendAdvance(hash: ref hash, advance: row.Advance);
            AppendDraw(hash: ref hash, draw: row.Draw);
            AppendDynamics(hash: ref hash, dynamics: row.Dynamics);
            AppendLattice(hash: ref hash, lattice: row.Lattice);
            AppendCycle(hash: ref hash, cycle: row.Cycle);
            hash.Add(value: ((uint)cells.Count));

            for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
                var cell = cells[cellIndex];

                AppendVisibility(ref hash, cell.Visibility);
                AppendString(hash: ref hash, value: cell.Key.Value);
                hash.Add(value: cell.Value);
                hash.Add(value: (byte)(cell.Observation is null ? 0 : 1));
                if (cell.Observation is { } observed) { hash.Add(value: observed.Tick); hash.Add(value: (byte)(observed.Visible ? 1 : 0)); }
                AppendString(hash: ref hash, value: cell.Text);
                AppendString(hash: ref hash, value: cell.Provenance);
                AppendAdvance(hash: ref hash, advance: cell.Advance);
                AppendDynamics(hash: ref hash, dynamics: cell.Dynamics);
                AppendCycle(hash: ref hash, cycle: cell.Cycle);

                if (
                    hasHandle &&
                    WorldStateReader.TryReadHandle(
                        catalog: catalog,
                        definition: server.Definition,
                        handle: handle,
                        key: cell.Key,
                        rawValue: out var resolved,
                        row: out _,
                        text: out var resolvedText,
                        tick: tick
                    )
                ) {
                    hash.Add(value: ((byte)1));
                    hash.Add(value: (resolved ?? 0L));
                    AppendString(hash: ref hash, value: resolvedText);
                } else {
                    hash.Add(value: ((byte)0));
                }
            }

            hash.Add(value: (byte)(row.Phase is null ? 0 : 1));
            if (row.Phase is { } phase) {
                hash.Add(value: phase.Current);
                hash.Add(value: phase.Active);
                hash.Add(value: phase.Ready);
                hash.Add(value: phase.Sequence);
                hash.Add(value: phase.Round);
                hash.Add(value: phase.DeadlineTick);
            }
            var decks = row.DrawDecks ?? [];

            hash.Add(value: ((uint)decks.Count));

            for (var deckIndex = 0; (deckIndex < decks.Count); deckIndex++) {
                hash.Add(value: decks[deckIndex].Word0);
                hash.Add(value: decks[deckIndex].Word1);
                hash.Add(value: decks[deckIndex].Word2);
                hash.Add(value: decks[deckIndex].Word3);
            }
        }

        hash.Add(value: ((byte)(server.Population.Fields is null ? 0 : 1)));
        server.Population.Fields?.AppendStateHash(hash: ref hash);
    }
}
