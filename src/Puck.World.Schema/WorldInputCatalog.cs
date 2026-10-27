using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A finite input tuple with its precompiled live storage and family selections.</summary>
public sealed class WorldInputMapping {
    private readonly int[] m_addresses;
    private readonly ushort[] m_states;
    private readonly ushort[] m_live;

    /// <summary>Builds a mapping. Arrays are owned by the compiler and never mutated after publication.</summary>
    public WorldInputMapping(ushort id, string name, int[] addresses, ushort[] states, ushort[] live, WorldChannelTable channels) {
        Id = id;
        Name = name;
        m_addresses = addresses;
        m_states = states;
        m_live = live;
        Channels = channels;
        foreach (var address in addresses) {
            if (address >= 0 && address < ChannelLimits.MaxAxes) { NumericCount = (byte)Math.Max(NumericCount, address + 1); }
        }
    }

    /// <summary>The authority's tuple identifier.</summary>
    public ushort Id { get; }
    /// <summary>Canonical family/state spelling for named refusals and read-back.</summary>
    public string Name { get; }
    /// <summary>The selected storage table.</summary>
    public WorldChannelTable Channels { get; }
    /// <summary>Used numeric extent, including zero holes required by the mapping.</summary>
    public byte NumericCount { get; }
    /// <summary>Logical ids of live actions in ordinal name order.</summary>
    public ReadOnlySpan<ushort> Live => m_live;
    /// <summary>Selected state ordinals in canonical family order, including suppressed families.</summary>
    public ReadOnlySpan<ushort> States => m_states;
    /// <summary>Resolves a logical action to storage, or -1 when inactive.</summary>
    public int Address(int logicalId) => m_addresses[logicalId];
    /// <summary>Whether a logical action is eligible. Text is event-only and has address -2.</summary>
    public bool IsLive(int logicalId) => m_addresses[logicalId] != -1;
}

/// <summary>Immutable logical vocabulary, finite tuples and directed keep sets; compilation alone allocates.</summary>
public sealed class WorldInputCatalog {
    /// <summary>Compares the authored input contract during reload; ordinary state values and presentation edits do not reset input.</summary>
    public static bool SameContract(WorldDefinition left, WorldDefinition right) =>
        ReferenceEquals(left, right) || Same(left.Channels, right.Channels) && Same(left.InputModes, right.InputModes) &&
        Same(left.InputHistory, right.InputHistory) && Same(left.InputPatterns, right.InputPatterns) && Same(left.SeatModes, right.SeatModes);

    /// <summary>Compares one action's meaning and continuation policy without charging unrelated action edits to it.</summary>
    public static bool SameAction(WorldDefinition left, WorldDefinition right, string name) =>
        Same(left.Channels.FirstOrDefault(row => row.Name == name), right.Channels.FirstOrDefault(row => row.Name == name)) &&
        Same(ActionPolicy(left.InputModes, name), ActionPolicy(right.InputModes, name));

    private static WorldInputModes? ActionPolicy(WorldInputModes? modes, string name) => modes is null ? null : modes with {
        Global = modes.Global.Contains(name, StringComparer.Ordinal) ? [name] : [],
        Families = modes.Families.OrderBy(static row => row.Name, StringComparer.Ordinal).Select(family => family with {
            States = family.States.OrderBy(static row => row.Name, StringComparer.Ordinal).Select(state => state with {
                Channels = state.Channels.Contains(name, StringComparer.Ordinal) ? [name] : [],
            }).ToArray(),
            Transitions = (family.Transitions ?? []).OrderBy(static row => row.From, StringComparer.Ordinal)
                .ThenBy(static row => row.To, StringComparer.Ordinal).Select(transition => transition with {
                    Keep = transition.Keep.Contains(name, StringComparer.Ordinal) ? [name] : [],
                    ConditionalKeep = (transition.ConditionalKeep ?? []).Where(clause => clause.Keep.Contains(name, StringComparer.Ordinal))
                        .Select(clause => clause with { Keep = [name] }).ToArray(),
                }).ToArray(),
        }).ToArray(),
    };

    /// <summary>Resolves another catalog's named tuple, independently of family/state ordinal changes.</summary>
    public bool TryMapTuple(WorldInputCatalog source, WorldInputMapping mapping, out WorldInputMapping? result) {
        if (source.Families.Length != Families.Length) { result = null; return false; }
        var states = new ushort[Families.Length];
        for (var family = 0; family < Families.Length; family++) {
            var from = source.Families[family];
            var target = Families[family];
            if (from.Name != target.Name) { result = null; return false; }
            var name = from.States[mapping.States[family]].Name;
            var found = false;
            for (var state = 0; state < target.States.Count; state++) {
                if (target.States[state].Name != name) { continue; }
                states[family] = (ushort)state;
                found = true;
                break;
            }
            if (!found) { result = null; return false; }
        }
        return TryGetMapping(states, out result);
    }

    private static bool Same<T>(T left, T right) {
        if (ReferenceEquals(left, right)) { return true; }
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WorldJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(left, info).AsSpan().SequenceEqual(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(right, info));
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldDefinition, WorldInputCatalog> Catalogs = new();
    /// <summary>Admission bound independent of active storage.</summary>
    public const int MaximumLogicalChannels = 4096;
    /// <summary>Maximum registered source contributions, independently of the logical action vocabulary.</summary>
    public const int MaximumSources = ushort.MaxValue;
    /// <summary>Maximum allowed tuples, checked before expanding the Cartesian product.</summary>
    public const int MaximumTuples = 4096;
    private readonly Dictionary<string, ushort> m_logicalIds;
    private readonly WorldChannel[] m_channels;
    private readonly WorldInputModeFamily[] m_families;
    private readonly WorldInputMapping[] m_mappings;
    private readonly int m_cancellationReserve;
    private sealed record KeepClause(HashSet<ushort> Channels, (int Family, ushort State)[] From, (int Family, ushort State)[] To) {
        public static bool Matches(WorldInputMapping mapping, (int Family, ushort State)[] conditions) {
            foreach (var condition in conditions) { if (mapping.States[condition.Family] != condition.State) { return false; } }
            return true;
        }
    }
    private readonly Dictionary<(int Family, ushort From, ushort To), KeepClause[]> m_keep;
    private WorldInputSelector[] m_selectors = [];
    private CompiledWorldInputPattern[] m_patterns = [];

    /// <summary>Compiles one document's catalog and finite selector proofs once.</summary>
    public static WorldInputCatalog For(WorldDefinition definition) => Catalogs.GetValue(definition, static row => {
        var catalog = Compile(row.Channels, row.InputModes);
        row.InputHistory?.Validate();
        foreach (var crossing in row.InputHistory?.Crossings ?? []) {
            if (crossing.FacingState is { } facing &&
                !row.BodyState.Concat(row.IdentityState).Any(state => state.Name == facing && state.Kind == Puck.Physics.Motion.ActionStateKind.Counter)) {
                throw Invalid($"inputHistory crossing '{crossing.Name}': facingState '{facing}' must name an authoritative body counter");
            }
        }
        catalog.m_selectors = catalog.m_families.Select(family => WorldInputSelector.Compile(family, row)).ToArray();
        catalog.m_patterns = CompiledWorldInputPattern.Compile(catalog, row.InputHistory ?? new(), row.InputPatterns ?? []);
        var defaults = catalog.m_selectors.Select(static selector => selector.Default).ToArray();
        if (!catalog.TryGetMapping(defaults, out var mapping)) { throw Invalid("inputModes: the default tuple is forbidden by combinations"); }
        catalog.DefaultMapping = mapping!;
        return catalog;
    });

    private WorldInputCatalog(WorldChannel[] channels, WorldInputModeFamily[] families, WorldInputMapping[] mappings,
        Dictionary<string, ushort> logicalIds, Dictionary<(int Family, ushort From, ushort To), KeepClause[]> keep) {
        m_channels = channels;
        m_families = families;
        m_mappings = mappings;
        m_logicalIds = logicalIds;
        m_keep = keep;
        DefaultMapping = mappings[0];
        foreach (var mapping in mappings) {
            var buttons = 0;
            foreach (var channel in mapping.Live) { if (channels[channel].Shape == ChannelShape.Binary) { buttons++; } }
            m_cancellationReserve = Math.Max(m_cancellationReserve, buttons);
        }
    }

    /// <summary>The logical declarations in ordinal name order.</summary>
    public ReadOnlySpan<WorldChannel> Channels => m_channels;
    /// <summary>Canonical family order.</summary>
    public ReadOnlySpan<WorldInputModeFamily> Families => m_families;
    /// <summary>Proven selector partitions, available on a document-bound catalog.</summary>
    public ReadOnlySpan<WorldInputSelector> Selectors => m_selectors;
    /// <summary>Precompiled history programs and shared-group priorities.</summary>
    public IReadOnlyList<CompiledWorldInputPattern> Patterns => m_patterns;
    /// <summary>Bounded worst-case history work charged at each rule read or consumption.</summary>
    public long PatternWork(int index, int capacity) => CompiledWorldInputPattern.Work(m_patterns, index, HistoryCapacity(capacity));

    /// <summary>Reserves terminal cancellations separately from the authored ingress ring, at most one per live held button.</summary>
    public int HistoryCapacity(int ingressCapacity) => checked(ingressCapacity + m_cancellationReserve);
    /// <summary>Reports declared vocabulary separately from each tuple's active storage charge.</summary>
    public string DescribeBudget(WorldInputHistory? history = null) {
        var numeric = 0;
        var buttons = 0;
        foreach (var mapping in m_mappings) {
            var tupleNumeric = 0;
            var tupleButtons = 0;
            foreach (var id in mapping.Live) {
                if (m_channels[id].Shape == ChannelShape.Binary) { tupleButtons++; }
                else if (m_channels[id].Shape != ChannelShape.Text) { tupleNumeric++; }
            }
            numeric = Math.Max(numeric, tupleNumeric);
            buttons = Math.Max(buttons, tupleButtons);
        }
        var capacity = history?.EventCapacity ?? 256;
        return $"input {m_channels.Length}/{MaximumLogicalChannels} logical actions, {m_mappings.Length}/{MaximumTuples} tuples, active maxima {numeric}/{ChannelLimits.MaxAxes} numeric and {buttons}/{ChannelLimits.MaxButtons} buttons; history {capacity} ingress + {m_cancellationReserve} terminal records, {capacity} boundaries with at most {numeric + buttons} action decisions each";
    }
    /// <summary>Every admitted tuple.</summary>
    public ReadOnlySpan<WorldInputMapping> Mappings => m_mappings;
    /// <summary>The admitted tuple for owners without selector subjects.</summary>
    public WorldInputMapping DefaultMapping { get; private set; }
    /// <summary>Resolves semantic identity independently of the current storage layout.</summary>
    public bool TryGetLogicalId(string name, out ushort id) => m_logicalIds.TryGetValue(name, out id);
    /// <summary>Finds an admitted tuple without allocating; unknown or forbidden selections refuse.</summary>
    public bool TryGetMapping(ReadOnlySpan<ushort> states, out WorldInputMapping? mapping) {
        foreach (var candidate in m_mappings) {
            if (candidate.States.SequenceEqual(states)) { mapping = candidate; return true; }
        }
        mapping = null;
        return false;
    }
    /// <summary>Intersects every changed family's directed keep set. Dispatch/grant checks further narrow it at admission.</summary>
    public bool Keeps(WorldInputMapping from, WorldInputMapping to, ushort logicalId) {
        if ((from.Address(logicalId) < 0) || (to.Address(logicalId) < 0) ||
            (m_channels[logicalId].Shape is ChannelShape.Relative or ChannelShape.Text)) { return false; }
        for (var family = 0; family < m_families.Length; family++) {
            var oldState = from.States[family];
            var newState = to.States[family];
            if (oldState == newState) { continue; }
            if (!m_keep.TryGetValue((family, oldState, newState), out var clauses)) { return false; }
            var admitted = false;
            foreach (var clause in clauses) {
                if (clause.Channels.Contains(logicalId) && KeepClause.Matches(from, clause.From) && KeepClause.Matches(to, clause.To)) { admitted = true; break; }
            }
            if (!admitted) { return false; }
        }
        return true;
    }

    /// <summary>Compiles the finite catalog, refusing invalid names, combinations, budgets and continuity by name.</summary>
    public static WorldInputCatalog Compile(IReadOnlyList<WorldChannel> declarations, WorldInputModes? modes) {
        if (declarations.Count > MaximumLogicalChannels) { throw Invalid($"channels requires {declarations.Count} logical names; maximum {MaximumLogicalChannels}"); }
        var channels = declarations.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray();
        var logicalIds = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (var id = 0; id < channels.Length; id++) {
            if (string.IsNullOrWhiteSpace(channels[id].Name) || !logicalIds.TryAdd(channels[id].Name, (ushort)id)) {
                throw Invalid($"channels: missing or duplicate name '{channels[id].Name}'");
            }
        }
        var families = (modes?.Families ?? []).OrderBy(static row => row.Name, StringComparer.Ordinal)
            .Select(static row => row with { States = row.States.OrderBy(static state => state.Name, StringComparer.Ordinal).ToArray() }).ToArray();
        var familyIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var stateIds = new Dictionary<string, ushort>[families.Length];
        var global = Resolve(modes?.Global ?? channels.Select(static row => row.Name).ToArray(), "inputModes.global", logicalIds);
        long product = 1;
        for (var family = 0; family < families.Length; family++) {
            var row = families[family];
            if (string.IsNullOrWhiteSpace(row.Name) || !familyIds.TryAdd(row.Name, family)) { throw Invalid($"inputModes: missing or duplicate family '{row.Name}'"); }
            if ((row.States.Count == 0) || (row.States.Count > MaximumTuples)) { throw Invalid($"inputModes family '{row.Name}': state count {row.States.Count} outside 1..{MaximumTuples}"); }
            var states = stateIds[family] = new Dictionary<string, ushort>(StringComparer.Ordinal);
            for (var state = 0; state < row.States.Count; state++) {
                if (string.IsNullOrWhiteSpace(row.States[state].Name) || !states.TryAdd(row.States[state].Name, (ushort)state)) {
                    throw Invalid($"inputModes family '{row.Name}': missing or duplicate state '{row.States[state].Name}'");
                }
                _ = Resolve(row.States[state].Channels, $"inputModes {row.Name}/{row.States[state].Name}", logicalIds);
            }
            if (!states.ContainsKey(row.Default)) { throw Invalid($"inputModes family '{row.Name}': unknown default '{row.Default}'"); }
            if (modes?.Combinations is null) {
                product *= row.States.Count;
                if (product > MaximumTuples) { throw Invalid($"inputModes family '{row.Name}' cardinality {row.States.Count} expands tuples to {product}; maximum {MaximumTuples}"); }
            }
        }
        for (var family = 0; family < families.Length; family++) {
            foreach (var suppression in families[family].SuspendedBy ?? []) {
                if (!familyIds.TryGetValue(suppression.Family, out var other) || (other == family)) {
                    throw Invalid($"inputModes family '{families[family].Name}': invalid suspendedBy family '{suppression.Family}'");
                }
                foreach (var state in suppression.States) {
                    if (!stateIds[other].ContainsKey(state)) { throw Invalid($"inputModes family '{families[family].Name}': unknown suspendedBy state '{suppression.Family}/{state}'"); }
                }
            }
        }
        var tuples = new List<ushort[]>();
        if (modes?.Combinations is { } combinations) {
            if ((combinations.Count == 0) || (combinations.Count > MaximumTuples)) { throw Invalid($"inputModes.combinations count must be 1..{MaximumTuples}"); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var combination in combinations) {
                if (combination.Count != families.Length) { throw Invalid("inputModes.combinations requires exactly one state per family"); }
                var tuple = new ushort[families.Length];
                for (var family = 0; family < families.Length; family++) {
                    if (!combination.TryGetValue(families[family].Name, out var state) || !stateIds[family].TryGetValue(state, out tuple[family])) {
                        throw Invalid($"inputModes.combinations: unknown selection for '{families[family].Name}'");
                    }
                }
                if (!seen.Add(string.Join(',', tuple))) { throw Invalid("inputModes.combinations repeats a tuple"); }
                tuples.Add(tuple);
            }
            tuples.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
        } else {
            for (var index = 0; index < product; index++) {
                var tuple = new ushort[families.Length];
                var remaining = index;
                for (var family = families.Length - 1; family >= 0; family--) {
                    tuple[family] = (ushort)(remaining % families[family].States.Count);
                    remaining /= families[family].States.Count;
                }
                tuples.Add(tuple);
            }
        }
        var liveSets = new ushort[tuples.Count][];
        var names = new string[tuples.Count];
        var interference = new HashSet<ushort>[channels.Length];
        for (var id = 0; id < channels.Length; id++) { interference[id] = []; }
        for (var index = 0; index < tuples.Count; index++) {
            var tuple = tuples[index];
            names[index] = string.Join(", ", families.Select((row, family) => $"{row.Name}={row.States[tuple[family]].Name}"));
            var live = new HashSet<ushort>(global);
            for (var family = 0; family < families.Length; family++) {
                var row = families[family];
                var suppressed = (row.SuspendedBy ?? []).Any(s => s.States.Contains(families[familyIds[s.Family]].States[tuple[familyIds[s.Family]]].Name, StringComparer.Ordinal));
                if (!suppressed) { live.UnionWith(Resolve(row.States[tuple[family]].Channels, row.Name, logicalIds)); }
            }
            liveSets[index] = live.Order().ToArray();
            foreach (var binary in new[] { false, true }) {
                var bank = liveSets[index].Where(id => channels[id].Shape != ChannelShape.Text && (channels[id].Shape == ChannelShape.Binary) == binary).ToArray();
                var ceiling = binary ? ChannelLimits.MaxButtons : ChannelLimits.MaxAxes;
                if (bank.Length > ceiling) { throw Invalid($"inputModes tuple [{names[index]}] {(binary ? "button" : "numeric")} bank requires {bank.Length}; maximum {ceiling}: {string.Join(", ", bank.Select(id => channels[id].Name))}"); }
                foreach (var id in bank) { interference[id].UnionWith(bank.Where(other => other != id)); }
            }
            var roles = new HashSet<ChannelRole>();
            foreach (var id in live) {
                if ((channels[id].Role is { } role) && !roles.Add(role)) { throw Invalid($"inputModes tuple [{names[index]}]: role '{role}' has multiple live actions"); }
            }
        }
        var preferred = new int[channels.Length];
        Array.Fill(preferred, -1);
        foreach (var id in Enumerable.Range(0, channels.Length).OrderByDescending(id => interference[id].Count).ThenBy(id => channels[id].Name, StringComparer.Ordinal)) {
            var used = interference[id].Select(other => preferred[other]).ToHashSet();
            var slot = 0;
            while (used.Contains(slot)) { slot++; }
            preferred[id] = slot;
        }
        var mappings = new WorldInputMapping[tuples.Count];
        var tables = new Dictionary<string, WorldChannelTable>(StringComparer.Ordinal);
        for (var index = 0; index < tuples.Count; index++) {
            if (liveSets[index].Count(id => channels[id].Shape == ChannelShape.Text) > 1) {
                throw Invalid($"input tuple {names[index]}: more than one Text action owns committed text focus");
            }
            var addresses = new int[channels.Length];
            Array.Fill(addresses, -1);
            var used = new bool[ChannelLimits.MaxChannels];
            var order = global.Order().Concat(liveSets[index].Where(id => !global.Contains(id)));
            foreach (var id in order) {
                if (channels[id].Shape == ChannelShape.Text) { addresses[id] = -2; continue; }
                var binary = channels[id].Shape == ChannelShape.Binary;
                var start = binary ? ChannelLimits.MaxAxes : 0;
                var count = binary ? ChannelLimits.MaxButtons : ChannelLimits.MaxAxes;
                var slot = global.Contains(id) ? 0 : preferred[id];
                if ((slot >= count) || used[start + slot]) { slot = 0; }
                while (used[start + slot]) { slot++; }
                addresses[id] = start + slot;
                used[start + slot] = true;
            }
            var key = string.Join(',', addresses);
            if (!tables.TryGetValue(key, out var table)) {
                table = WorldChannelTable.CompileMapping(channels, addresses);
                tables.Add(key, table);
            }
            mappings[index] = new WorldInputMapping((ushort)index, names[index], addresses, tuples[index], liveSets[index], table);
        }
        var keep = new Dictionary<(int, ushort, ushort), KeepClause[]>();
        for (var family = 0; family < families.Length; family++) {
            foreach (var row in families[family].Transitions ?? []) {
                var label = $"inputModes {families[family].Name} {row.From} -> {row.To}";
                if (!stateIds[family].TryGetValue(row.From, out var from) || !stateIds[family].TryGetValue(row.To, out var to) || (from == to)) { throw Invalid($"{label}: unknown or identical states"); }
                var clauses = new List<KeepClause> { new(Resolve(row.Keep, label + " keep", logicalIds), [], []) };
                foreach (var conditional in row.ConditionalKeep ?? []) {
                    clauses.Add(new(Resolve(conditional.Keep, label + " conditionalKeep", logicalIds),
                        Conditions(conditional.From), Conditions(conditional.To)));
                }
                if (!keep.TryAdd((family, from, to), clauses.ToArray())) { throw Invalid($"{label}: duplicate transition"); }
                foreach (var clause in clauses) {
                  foreach (var id in clause.Channels) {
                    if (channels[id].Shape is ChannelShape.Relative or ChannelShape.Text) { throw Invalid($"{label}: keep {channels[id].Name}: {channels[id].Shape} cannot continue"); }
                    var sourceLive = false;
                    var destinationLive = false;
                    foreach (var mapping in mappings) {
                        if (mapping.Address(id) < 0) { continue; }
                        sourceLive |= mapping.States[family] == from && KeepClause.Matches(mapping, clause.From);
                        destinationLive |= mapping.States[family] == to && KeepClause.Matches(mapping, clause.To);
                    }
                    if (!sourceLive || !destinationLive) { throw Invalid($"{label}: keep {channels[id].Name}: no eligible tuple pair"); }
                  }
                }
                (int, ushort)[] Conditions(IReadOnlyDictionary<string, string>? conditions) {
                    if (conditions is null) { return []; }
                    var result = new List<(int, ushort)>();
                    foreach (var condition in conditions.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
                        if (!familyIds.TryGetValue(condition.Key, out var other) || !stateIds[other].TryGetValue(condition.Value, out var state)) {
                            throw Invalid($"{label}: unknown conditionalKeep selection {condition.Key}/{condition.Value}");
                        }
                        result.Add((other, state));
                    }
                    return result.ToArray();
                }
            }
        }
        return new WorldInputCatalog(channels, families, mappings, logicalIds, keep);
    }

    private static HashSet<ushort> Resolve(IEnumerable<string> names, string path, Dictionary<string, ushort> ids) {
        var result = new HashSet<ushort>();
        foreach (var name in names) {
            if (!ids.TryGetValue(name, out var id)) { throw Invalid($"{path}: unknown channel '{name}'"); }
            if (!result.Add(id)) { throw Invalid($"{path}: duplicate channel '{name}'"); }
        }
        return result;
    }
    private static ArgumentException Invalid(string message) => new(message);
}
