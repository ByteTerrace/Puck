using Puck.Commands;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The arena a judge may inspect. Its rows are built only from a recipient projection and the plan seed.</summary>
internal sealed class WorldSearchPosition {
    private readonly WorldDefinition m_definition;
    private readonly StateArena m_source;
    private readonly WorldSearchRow m_job;
    private readonly Principal? m_actor;
    private readonly ulong[] m_versions;
    internal WorldDefinition Definition { get; }
    internal StateArena Arena { get; }

    internal WorldSearchPosition(WorldDefinition definition, StateArena source, WorldSearchRow job, ArenaTime time) {
        m_definition = definition;
        m_source = source;
        m_job = job;
        // An answer with a broader audience can use only the public part of the actor's view.
        var audience = job.Actor is { } actor ? new StateVisibility(Readers: [actor.Describe()]) : null;
        m_actor = new[] { job.Best, job.Legal, job.Reach, job.Counts }.OfType<string>()
            .All(name => StateVisibility.Encloses(audience, WorldDefinitionRows.FindStateRow(definition.AuthoredState, name)?.Visibility))
            ? job.Actor : null;
        m_versions = new ulong[source.Rows.Count];
        var projected = Project();
        var section = projected.StateRaw!;
        Definition = projected;
        Arena = new StateArena(Definition.StateCatalog, section, time, WorldSlotLanes.Options(Definition));
    }

    internal void Refresh(ArenaTime time) {
        var changed = false;
        for (var i = 0; i < m_versions.Length; i++) {
            var version = m_source.RowVersion(i);
            changed |= version != m_versions[i];
            m_versions[i] = version;
        }
        if (!changed) { return; }
        var projected = Project();
        if (!Arena.TryLoad(projected.AuthoredState, time, out var reason) ||
            (m_source.Catalog.Pools.Count != 0 && !Arena.TryLoadPools(projected.StateRaw!, time, out reason))) {
            throw new InvalidOperationException(reason);
        }
    }

    private WorldDefinition Project() {
        var visible = WorldStateDisclosure.Disclose(m_definition, m_source, m_actor);
        var rows = visible.Definition.AuthoredState.ToArray();
        for (var i = 0; i < rows.Length; i++) {
            var row = rows[i];
            var cells = row.Cells?.Select(cell => cell with { Visibility = null }).ToList() ?? [];
            if (visible.Withheld.TryGetValue(row.Name.Value, out var hidden)) {
                // Best-move addresses are part of the job declaration, even when their values are private.
                foreach (var job in m_definition.Search.Rows.Where(job => job.Best == row.Name.Value)) {
                    foreach (var name in job.Revision is null ? new[] { "token", "to", "score" } : ["token", "to", "score", "revision"]) {
                        var address = CellName.Parse(name);
                        if (!cells.Any(cell => cell.Key == address)) { cells.Add(new(address, CellValue.Int(0))); }
                    }
                }
                // A slot has a public address. A counted pile discloses its size, never its members' names.
                var count = row.IsSlot && cells.Count == 0 ? 1 :
                    !hidden.WholeRow && hidden.Policy != HiddenCells.Omit ? hidden.Count : 0;
                var keys = HiddenKeys(row, rows, cells, count, hidden.WholeRow || hidden.Policy == HiddenCells.Omit);
                foreach (var key in keys) {
                    cells.Add(new(key, row.Domain is StateDomain.KeysOf { Ordered: true } ? CellValue.Bool(true) : Sample(row, key)));
                }
            }
            rows[i] = row with {
                Cells = cells, Visibility = null, CellVisibility = false,
                Draw = row.Draw?.ForSearch(m_job.DrawSeed),
                DrawCursor = row.Draw?.PrivateStream == true ? 0 : row.DrawCursor,
                DrawnMasks = row.Draw?.PrivateStream == true ? null : row.DrawnMasks
            };
        }
        // Compile keys from the disclosed cells. A compatible live catalog can retain names of hidden members.
        var projected = visible.Definition with { StateRaw = visible.Definition.StateRaw! with { World = rows } };
        if (m_source.Catalog.Pools.Count == 0) { return projected; }
        // A pair identifies its endpoints. Carry the allocator and its generated fields together only when disclosed.
        var section = projected.StateRaw!;
        var poolsVisible = Enumerable.Range(0, m_source.Rows.Count).Where(m_source.Catalog.IsPoolRow)
            .All(index => WorldStateDisclosure.ReadsWholeRow(m_source, index, m_actor));
        return projected with { StateRaw = section with {
            Pools = poolsVisible ? m_source.ToPools() : section.Pools?.Select(p => p with { Initial = null, Snapshot = null }).ToArray(),
            PairPools = poolsVisible ? m_source.ToPairPools() : section.PairPools?.Select(p => p with { Initial = null, Snapshot = null }).ToArray()
        } };
    }

    private IEnumerable<CellName> HiddenKeys(WorldStateRow row, WorldStateRow[] rows, List<StateCell> cells, int count, bool unknownSize) {
        if (row.IsSlot) {
            if (count > 0) { yield return StateRow.SlotKey; }
            yield break;
        }
        var used = cells.Select(cell => cell.Key).ToHashSet();
        IEnumerable<CellName>? domainKeys = null;
        var rng = Pcg32XshRr.Create(m_job.DrawSeed, GeneratorEngine.ComputeStreamId(row.Name.Value));
        if (row.Domain is StateDomain.KeysOf domain) {
            if (domain.Ordered) {
                foreach (var peer in rows.Where(peer => peer.Domain is StateDomain.KeysOf { Ordered: true } pile && pile.Row == domain.Row)) {
                    foreach (var member in peer.Cells ?? []) { used.Add(member.Key); }
                }
            }
            domainKeys = WorldDefinitionRows.FindStateRow(rows, domain.Row.Value)?.Cells?.Select(cell => cell.Key) ?? [];
        } else if (row.Domain is StateDomain.CellsOf board && WorldTopologyCompilation.Find(m_definition, board.Topology) is { } topology) {
            domainKeys = Enumerable.Range(0, topology.CellCount).Select(topology.NameOf);
        } else if (row.Domain is StateDomain.Ring ring) {
            domainKeys = Enumerable.Range(0, ring.Capacity).Select(i => CellName.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (domainKeys is not null) {
            var available = domainKeys.Where(key => !used.Contains(key)).ToArray();
            if (unknownSize) {
                var limit = Math.Min(available.Length, (row.Capacity ?? available.Length) - cells.Count);
                count = limit > 0 ? (int)rng.NextUInt32(0, (uint)limit) : 0;
            }
            rng.Shuffle(available.AsSpan());
            foreach (var key in available.Take(count)) { yield return key; }
        } else if (row.EffectiveDomain is StateDomain.Keys) {
            if (unknownSize && row.Capacity is { } capacity && capacity > cells.Count) {
                count = (int)rng.NextUInt32(0, (uint)(capacity - cells.Count));
            }
            for (var i = 0; count > 0; i++) {
                var key = CellName.Parse(FormattableString.Invariant($"unknown-{i}"));
                if (used.Add(key)) { count--; yield return key; }
            }
        }
    }

    private CellValue Sample(WorldStateRow row, CellName key) {
        var seed = GeneratorEngine.ComputeDrawSeed(m_job.DrawSeed, row.Name.Value, key.Value);
        if (row.Draw is { } draw && GeneratorEngine.TryResolveSource(m_definition.Generators, draw, out var generator, out _) &&
            GeneratorEngine.TryFire(generator, row.Kind, seed.State, seed.Stream, 0, null, out var fired, out _, draw.ForSearch(m_job.DrawSeed).Secret)) {
            return fired.Text is { } text ? CellValue.Text(text) : CellValue.FromNumber(row.Kind, fired.Numeric!.Value);
        }
        if (row.Kind == CellKind.Text) { return CellValue.Text(string.Empty); }
        if (row.Kind == CellKind.Vector) { return CellValue.Vector(new sbyte[WorldStateSpaces.Find(m_definition.Spaces, row.Space!)!.Identity.Dimensions]); }
        var symbols = StateEnum.Named(m_definition.StateRaw?.Enums, row.Enum);
        var low = row.Min ?? (symbols is not null || row.Kind == CellKind.Bool ? 0 : int.MinValue);
        var high = row.Max ?? (symbols is not null ? symbols.Count - 1L : row.Kind == CellKind.Bool ? 1 : int.MaxValue);
        if (row.ValuesFrom is { } name && WorldTopologyCompilation.Find(m_definition, name) is { } topology) {
            low = Math.Max(0, low);
            high = Math.Min(topology.CellCount - 1L, high);
        }
        if (row.Min is null) { low = Math.Min(low, high); }
        if (row.Max is null) { high = Math.Max(high, low); }
        var words = new DrawGeneratorWords<Pcg32XshRr>(Pcg32XshRr.Create(seed.State, seed.Stream));
        if (!BoundedRandomSampling.TrySampleWide(ref words, (UInt128)((Int128)high - low + 1), GeneratorCapacity.MaxBoundedAttempts,
            out var offset, out _, out var reason)) { throw new InvalidOperationException(reason); }
        return CellValue.FromNumber(row.Kind, (long)((Int128)low + (Int128)offset));
    }
}
