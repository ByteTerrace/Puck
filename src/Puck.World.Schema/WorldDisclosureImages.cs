using System.Text.Json;
using Puck.Assets;

namespace Puck.World;

/// <summary>One immutable observable row. Vector payloads are privately copied before content identity is computed.</summary>
public sealed class WorldRecipientImage {
    internal WorldRecipientImage(ContentPin identity, StateCell[] cells, WorldObservedRow? observation, int bytes) {
        Identity = identity; Cells = Array.AsReadOnly(cells); Observation = observation; Bytes = bytes;
    }
    /// <summary>Content identity, including counts, placeholder order, traits and observations.</summary>
    public ContentPin Identity { get; }
    /// <summary>Readable cells in recipient order.</summary>
    public IReadOnlyList<StateCell> Cells { get; }
    /// <summary>The presentation image, or null when the row itself is withheld.</summary>
    public WorldObservedRow? Observation { get; }
    internal int Bytes { get; }
}
internal sealed record WorldRecipientImageContent(StateCell[] Cells, WorldObservedRow? Observation);

/// <summary>World-owned, content-interned row images. Compositions are shared by the set of policies admitting the
/// reader. Current audience images keep their content alive; replaced content is weakly retained.</summary>
public sealed class WorldDisclosureImages {
    private sealed record Entry(ulong Version, ulong Dependencies, WorldRecipientImage Image, int[] Keys);
    private StateArena? m_arena;
    private long m_policyRevision = -1;
    private StateVisibility[] m_policies = [];
    private int[][] m_dependencies = [];
    private int[][] m_dependents = [];
    private readonly Dictionary<(int Row, string Audience), Entry> m_rows = [];
    private readonly Dictionary<ContentPin, WeakReference<WorldRecipientImage>> m_content = [];
    private readonly Dictionary<string, (WorldDefinition Source, ContentPin[] Rows, WorldDefinition Disclosed, ContentPin Identity)> m_definitions = [];
    private ArenaChangedKeys? m_changes;
    private StateArena? m_windowArena;
    private ulong[] m_windowVersions = [];
    private WorldDefinition? m_presentedSource;
    private IReadOnlySet<string> m_presented = new HashSet<string>();
    internal IReadOnlySet<string> Presented(WorldDefinition source) {
        if (!ReferenceEquals(m_presentedSource, source)) {
            m_presentedSource = source;
            m_presented = WorldProjection.PresentedRows(source, WorldKeyedValues.AuthoredBy(source).Bound) ?? [];
        }
        return m_presented;
    }

    /// <summary>Row compositions during the current tick, independent of recipient count.</summary>
    public long Compositions { get; private set; }
    /// <summary>Canonical image bytes composed during the current tick.</summary>
    public long ComposedBytes { get; private set; }
    /// <summary>Key changes proven irrelevant to a cached image during this tick.</summary>
    public long Skipped { get; private set; }
    /// <summary>Starts the world's next disclosure window. The carrier accumulates its committed writes.</summary>
    public void BeginTick(ArenaChangedKeys changes) {
        m_changes = changes; m_windowArena = m_arena; Compositions = 0; ComposedBytes = 0; Skipped = 0;
        // Capture each row once, independently of audience count. Only an image at least this current is covered
        // by the new window's key proof; a throttled stream may have missed a write whose keys were just cleared.
        if (m_windowArena is { } arena) {
            if (m_windowVersions.Length != arena.Rows.Count) { m_windowVersions = new ulong[arena.Rows.Count]; }
            for (var row = 0; row < m_windowVersions.Length; row++) { m_windowVersions[row] = arena.RowVersion(row); }
        }
        foreach (var pin in m_content.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) { m_content.Remove(pin); }
    }
    /// <summary>Reports live shared image storage and current tick work, for the operator's budget.</summary>
    public string Describe() {
        var held = m_content.Values.Select(reference => reference.TryGetTarget(out var image) ? image : null).OfType<WorldRecipientImage>().ToArray();
        return $"disclosure images {held.Length}, bytes {held.Sum(image => (long)image.Bytes)}, tick compositions {Compositions}, composed bytes {ComposedBytes}, skipped {Skipped}";
    }
    private void Prepare(WorldRecipientView view) {
        var arena = view.Arena;
        if (ReferenceEquals(m_arena, arena) && m_policyRevision == arena.DisclosureRevision) { return; }
        if (!ReferenceEquals(m_arena, arena)) { m_rows.Clear(); }
        m_arena = arena; m_policyRevision = arena.DisclosureRevision;
        var policies = new HashSet<StateVisibility>();
        var rowPolicies = new HashSet<StateVisibility>[arena.Rows.Count];
        for (var row = 0; row < arena.Rows.Count; row++) {
            var set = rowPolicies[row] = [];
            if (arena.Rows[row].Visibility is { } policy) { set.Add(policy); }
            if (!arena.HasCellVisibility(row)) { continue; }
            var cursor = 0;
            while (arena.TryNextCell(row, ref cursor, out var key)) { if (arena.Visibility(row, key) is { } cell) { set.Add(cell); } }
        }
        m_dependencies = new int[arena.Rows.Count][];
        for (var row = 0; row < arena.Rows.Count; row++) {
            var dependencies = new HashSet<int>();
            var set = new HashSet<StateVisibility>(rowPolicies[row]);
            foreach (var zone in arena.Catalog.ContainingZones(row)) { dependencies.Add(zone); set.UnionWith(rowPolicies[zone]); }
            foreach (var policy in set) {
                policies.Add(policy);
                if (policy.ReadersFrom is { } readers && arena.Catalog.TryResolve(StateLane.Document, readers, out var source)) { dependencies.Add(source.Ordinal); }
            }
            m_dependencies[row] = dependencies.Order().ToArray();
        }
        m_policies = policies.ToArray();
        m_dependents = Enumerable.Range(0, arena.Rows.Count).Select(source => Enumerable.Range(0, arena.Rows.Count)
            .Where(row => m_dependencies[row].Contains(source)).ToArray()).ToArray();
        // Policy bit positions may have changed; class keys from the previous index have no meaning now.
        m_rows.Clear(); m_definitions.Clear();
    }
    internal int[] Affected(WorldRecipientView view, ReadOnlySpan<int> rows) {
        Prepare(view);
        var affected = new HashSet<int>();
        foreach (var row in rows) {
            if ((uint)row >= (uint)view.Source.AuthoredState.Count) { continue; }
            affected.Add(row);
            foreach (var dependent in m_dependents[row]) {
                if (dependent < view.Source.AuthoredState.Count) { affected.Add(dependent); }
            }
        }
        return affected.Order().ToArray();
    }
    internal string Audience(WorldRecipientView view) {
        Prepare(view);
        var observer = view.Observer;
        return string.Create(m_policies.Length + 1, (m_policies, observer, view.Observes, view.Ceiling), static (span, state) => {
            span[0] = state.Observes ? (char)('1' + (int)state.Ceiling) : '0';
            for (var index = 0; index < state.m_policies.Length; index++) { span[index + 1] = state.observer.Allows(state.m_policies[index]) ? '1' : '0'; }
        });
    }
    internal WorldRecipientImage Image(WorldRecipientView view, int row) => Image(view, row, Audience(view));
    internal WorldRecipientImage Image(WorldRecipientView view, int row, string audience) {
        var arena = view.Arena;
        var version = arena.RowVersion(row);
        // Dependency versions are compared as an exact tuple below through a monotone sum: versions only increase
        // within this arena, whose replacement clears the table.
        var dependencies = 0UL;
        foreach (var dependency in m_dependencies[row]) { dependencies = checked(dependencies + arena.RowVersion(dependency)); }
        var key = (row, audience);
        if (m_rows.TryGetValue(key, out var entry) && entry.Dependencies == dependencies) {
            if (entry.Version == version) { return entry.Image; }
            if (ReferenceEquals(arena, m_windowArena) && entry.Version >= m_windowVersions[row] &&
                m_changes is not null && !m_changes.Overlaps(row, entry.Keys) &&
                !arena.ChangedKeys.Overlaps(row, entry.Keys)) {
                Skipped++;
                m_rows[key] = entry with { Version = version };
                return entry.Image;
            }
        }
        var observer = view.Observer;
        var exported = arena.ToRow(row);
        var cells = (exported.Cells ?? []).Where(cell => view.Includes(row) && observer.Discloses(row, cell.Key.Value))
            .Select(cell => cell.Value.Kind == CellKind.Vector ? cell with { Value = CellValue.Vector(cell.Value.AsVector.ToArray()) } : cell).ToArray();
        var observation = view.Includes(row) ? WorldStateDisclosure.Compose(view.Source, arena, view.Recipient, view.Unrestricted,
            presented: new HashSet<string> { exported.Name.Value }, onlyRow: row)?.SingleOrDefault() : null;
        if (observation is not null) { observation = observation with { Cells = Array.AsReadOnly(observation.Cells.ToArray()) }; }
        // The observation and replica cells together cover all observable state, including cell traits the replica carries.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WorldRecipientImageContent(cells, observation), WorldJsonContext.Default.Options);
        var pin = ContentPin.Compute(bytes);
        if (!m_content.TryGetValue(pin, out var reference) || !reference.TryGetTarget(out var image)) {
            image = new WorldRecipientImage(pin, cells, observation, bytes.Length);
            m_content[pin] = new WeakReference<WorldRecipientImage>(image);
        }
        Compositions++; ComposedBytes += bytes.Length;
        m_rows[key] = new Entry(version, dependencies, image,
            cells.Select(cell => arena.Keys.TryResolve(cell.Key, out var interned) ? interned.Ordinal : -1).ToArray());
        return image;
    }
    internal WorldDefinition Definition(WorldRecipientView view) {
        var audience = Audience(view);
        var source = view.Source;
        var images = new WorldRecipientImage[source.AuthoredState.Count];
        for (var row = 0; row < images.Length; row++) { images[row] = Image(view, row, audience); }
        var identities = images.Select(image => image.Identity).ToArray();
        var had = m_definitions.TryGetValue(audience, out var held);
        if (had && ReferenceEquals(source, held.Source) && identities.SequenceEqual(held.Rows)) { return held.Disclosed; }
        var disclosed = WorldStateDisclosure.Disclose(source, view.Arena, view.Recipient, view.Unrestricted).Definition;
        if (disclosed.StateRaw is { } state) {
            disclosed = disclosed with { StateRaw = state with { World = disclosed.AuthoredState.Select((row, ordinal) =>
                row with { Cells = images[ordinal].Cells }).ToArray() } };
        }
        var identity = ContentPin.Compute(WorldDefinitionSerialization.SerializeCompact(disclosed));
        if (had && identity == held.Identity) { disclosed = held.Disclosed; }
        m_definitions[audience] = (source, identities, disclosed, identity);
        return disclosed;
    }
    internal (WorldDefinition Definition, ContentPin Identity) DefinitionImage(WorldRecipientView view) {
        var definition = Definition(view);
        return (definition, m_definitions[Audience(view)].Identity);
    }
}
