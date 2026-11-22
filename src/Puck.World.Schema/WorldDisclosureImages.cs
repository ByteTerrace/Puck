using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Puck.Abstractions;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Assets;

namespace Puck.World;

/// <summary>World-owned row images and the disclosed definitions composed from them.
/// <para>What an audience is disclosed of a row is read from the store and one <see cref="RowMask"/>, the audience's
/// answers about that row, and from nothing else about the audience: one pass (<see cref="Pass"/>) writes the
/// canonical bytes of what the mask discloses and builds the image they describe, over one walk of the row
/// (<see cref="WorldStateDisclosure.VisitRow"/>). Only this table can make a mask. It asks an audience for one in
/// <see cref="Reading"/> and shares a row's composition between two audiences only when their masks for the row are
/// equal, byte for byte. A composition is the bytes' digest, which costs no allocation and is all a steady tick asks
/// for, and the image, built from the store only when a delivery carries it and dropped whenever the composition is
/// written again. An image is never found by a digest: an audience is handed the one built from the store under its
/// own mask, and a digest decides only whether a stream is sent a row again.</para>
/// <para>A row is read through an <see cref="AudienceKey"/>, which names the view it was taken for, so no caller
/// pairs one view with another's audience; a key taken before the store moved is taken again where it is used, which
/// also brings the table current to a replaced arena or policy index. While a journal scope is open nothing the
/// table keeps is read or written (<see cref="Unsettled"/>). A row every audience reads whole has one
/// composition.</para></summary>
public sealed partial class WorldDisclosureImages {
    /// <summary>The name a counters report heads this table's monotonic work with.</summary>
    public const string SourceName = "world.disclosure";
    /// <summary>Gets the kind counting row images composed.</summary>
    public static WorkKind RowCompositions { get; } = new(name: "world.disclosure.row-compositions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the canonical bytes of the row images composed.</summary>
    public static WorkKind RowBytes { get; } = new(name: "world.disclosure.row-bytes", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting cached row images a key proof kept across a write.</summary>
    public static WorkKind RowSkips { get; } = new(name: "world.disclosure.row-skips", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting rebuilds of the policy dependency index, each of which drops every cached image.</summary>
    public static WorkKind IndexRebuilds { get; } = new(name: "world.disclosure.index-rebuilds", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting disclosed definitions composed from the whole document.</summary>
    public static WorkKind DefinitionCompositions { get; } = new(name: "world.disclosure.definition-compositions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bytes serialized to identify disclosed definitions: a whole composition's
    /// document less its rows, and each row's cell-free declaration and storage.</summary>
    public static WorkKind DefinitionBytes { get; } = new(name: "world.disclosure.definition-bytes", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the rows whose versions a pass read to find the ones that changed: each window's
    /// start, and each audience's definition and frame check.</summary>
    public static WorkKind RowVisits { get; } = new(name: "world.disclosure.row-visits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting row images built from the store for a delivery to carry: a composition whose
    /// content stands builds none.</summary>
    public static WorkKind RowImages { get; } = new(name: "world.disclosure.row-images", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the passes that checked every row of an audience's definition because the arena
    /// could not name the rows that moved since the audience was last brought current: every version was reset at
    /// once, or the definition was held against another arena.</summary>
    public static WorkKind FullScans { get; } = new(name: "world.disclosure.full-scans", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting placement rows adopted through a proven holding-only move.</summary>
    public static WorkKind HoldingRows { get; } = new(name: "world.disclosure.holding-rows", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting placement projections composed for a view's door (<see cref="WorldRecipientView.Placements"/>);
    /// a reading whose inputs stand is answered from the table and counts nothing.</summary>
    public static WorkKind PlacementReadings { get; } = new(name: "world.disclosure.placement-readings", unit: "count", workClass: WorkClass.Pacing);
    private static readonly WorkKind[] WorkKindOrder = [RowCompositions, RowBytes, RowSkips, IndexRebuilds, DefinitionCompositions, DefinitionBytes, RowVisits, RowImages, FullScans, HoldingRows, PlacementReadings];

    // One audience's disclosed definition. Between whole compositions it changes only row by row: a keyed write changes
    // a row's cells, which its image carries, and every other write moves the arena's DisclosureRevision, after which a
    // changed row's skeleton (its declaration and storage, cells removed) is derived again. Everything else in the
    // document (the frame) moves with the source, the audience or a withheld row the placements read. A holding-only
    // source change can keep the current projection. Identity digests the frame and every row's image and skeleton.
    // FrameOf reads canonical bytes only when an identity is first asked for, so a console read of a section through
    // a seat's view composes rows and skeletons without serializing the frame. It keeps each row's digest and no
    // image: the document's cells are built when the document is asked for (Definition).
    private sealed class AudienceDefinition(WorldDefinition source, WorldDefinition composed, WorldDefinition frame,
        WorldStateDisclosure.DisclosedFields? fields, bool[] frameRows, WorldStateRow[] rows, ulong[] versions,
        ulong[] dependencies, long revision, byte[] digests) {
        public WorldDefinition Source { get; private set; } = source;
        public WorldDefinition Composed { get; private set; } = composed;
        private WorldStateSection? FrameState { get; } = frame.StateRaw;
        // The composed document less its rows, whose digest fills the frame slot once (FrameOf); released once digested.
        public WorldDefinition? FrameDocument { get; private set; } = frame;
        public WorldStateDisclosure.DisclosedFields? Fields { get; } = fields;
        public bool[] FrameRows { get; } = frameRows;
        // The ordinals FrameRows marks, in row order: the only rows whose images can move the frame.
        public int[] FrameRowOrdinals { get; } = [.. Enumerable.Range(0, frameRows.Length).Where(row => frameRows[row])];
        public WorldStateRow[] Rows { get; } = rows;
        public ulong[] Versions { get; } = versions;
        public ulong[] Dependencies { get; } = dependencies;
        // Each row's structure version when its skeleton was last derived.
        public ulong[] Structures { get; init; } = [];
        public long Revision { get; set; } = revision;
        // The arena whose rows Versions and Dependencies describe, and the place in its version moves they are current
        // to: a row that has not moved since then, and that depends on none that has, stands as recorded.
        public StateArena? Arena { get; set; }
        public long MovesRead { get; set; } = -1L;
        public byte[] Digests { get; } = digests;
        private Sha256Digest m_identity;
        private bool m_identityStale = true;
        // Identity and Frame read the frame slot, so only FrameOf, which fills it first, hands either out. The
        // identity is a value: a stream takes it at every delivery, and a pin would cost a string each time.
        public Sha256Digest Identity {
            get {
                if (FrameDocument is not null) { throw new InvalidOperationException("The frame digest is taken before the identity is read."); }
                if (m_identityStale) { m_identity = Sha256Digest.Compute(content: Digests); m_identityStale = false; }
                return m_identity;
            }
        }
        // The frame slot is written once, by FillFrame: rows refresh only their own slots.
        public ContentPin Frame { get; private set; }
        public ContentPin? BeforeAppearance { get; private set; }
        public Sha256Digest? BeforeAppearanceIdentity { get; private set; }
        public WorldDefinition? Disclosed { get; set; }
        public bool Dirty { get; set; }
        private Dictionary<string, int>? m_placementOrdinals;

        public int PlacementOrdinal(WorldPlacement previous, int sourceOrdinal) {
            if (ReferenceEquals(Composed.PlacementsRaw, Source.PlacementsRaw)) { return sourceOrdinal; }
            if (m_placementOrdinals is null) {
                m_placementOrdinals = new(Composed.Placements.Count, StringComparer.Ordinal);
                for (var ordinal = 0; ordinal < Composed.Placements.Count; ordinal++) {
                    var id = Composed.Placements[ordinal].Id;
                    if (!m_placementOrdinals.TryAdd(id, ordinal)) { m_placementOrdinals[id] = -1; }
                }
            }
            return m_placementOrdinals.GetValueOrDefault(previous.Id, -1);
        }

        public void AdoptHoldings(WorldDefinition next, IReadOnlyList<(int Row, int Holding)> projected) {
            BeforeAppearance = null;
            Composed = ReferenceEquals(Composed.PlacementsRaw, Source.PlacementsRaw)
                ? Composed with { PlacementsRaw = next.PlacementsRaw }
                : Composed.WithPlacementHoldings(projected);
            Source = next;
            FrameDocument = Composed with { StateRaw = FrameState };
            Disclosed = null;
        }

        public void AdoptAppearance(WorldDefinition next, ContentPin before) {
            BeforeAppearance = before;
            BeforeAppearanceIdentity = Identity;
            Source = next;
            Composed = Composed with { CreationsRaw = next.CreationsRaw, ShadesRaw = next.ShadesRaw };
            FrameDocument = Composed with { StateRaw = FrameState };
            if (Disclosed is { } held) {
                Disclosed = held with { CreationsRaw = next.CreationsRaw, ShadesRaw = next.ShadesRaw };
            }
        }
        public void AdoptValues(WorldDefinition next) => Source = next;

        // The row each disclosed document carried last, with the digest of the skeleton and the image cells it was built
        // from, carried over when the audience's definition is composed again (Inherit). A row whose skeleton digest and
        // image stand is handed out as the same instance, so a reader reloads only the rows that moved
        // (WorldDocumentStateView keeps its arena for a section whose other rows are the instances it holds).
        private WorldStateRow?[] m_disclosedRows = [];
        private Sha256Digest[] m_disclosedSkeletons = [];
        private object?[] m_disclosedCells = [];

        public void Inherit(AudienceDefinition? previous) {
            if ((previous is null) || (previous.m_disclosedRows.Length != Rows.Length)) { return; }
            m_disclosedRows = previous.m_disclosedRows;
            m_disclosedSkeletons = previous.m_disclosedSkeletons;
            m_disclosedCells = previous.m_disclosedCells;
        }
        public WorldStateRow DisclosedRow(int row, IReadOnlyList<StateCell> cells) {
            var skeleton = Sha256Digest.Of(digest: Digests.AsSpan(SkeletonSlot(row), SHA256.HashSizeInBytes));

            if (m_disclosedRows.Length != Rows.Length) {
                m_disclosedRows = new WorldStateRow?[Rows.Length];
                m_disclosedSkeletons = new Sha256Digest[Rows.Length];
                m_disclosedCells = new object?[Rows.Length];
            }
            if ((m_disclosedRows[row] is { } held) && (m_disclosedSkeletons[row] == skeleton) && ReferenceEquals(m_disclosedCells[row], cells)) {
                return held;
            }

            var built = (Rows[row] with { Cells = cells });

            m_disclosedRows[row] = built;
            m_disclosedSkeletons[row] = skeleton;
            m_disclosedCells[row] = cells;

            return built;
        }

        public static int ImageSlot(int row) => (1 + (2 * row)) * SHA256.HashSizeInBytes;
        public static int SkeletonSlot(int row) => (2 + (2 * row)) * SHA256.HashSizeInBytes;
        public bool Holds(int slot, Sha256Digest digest) => (Sha256Digest.Of(digest: Digests.AsSpan(slot, SHA256.HashSizeInBytes)) == digest);
        public void Store(int slot, Sha256Digest digest) {
            if (Holds(slot: slot, digest: digest)) { return; }
            BeforeAppearanceIdentity = null;
            digest.CopyTo(destination: Digests.AsSpan(slot, SHA256.HashSizeInBytes));
            Dirty = true;
        }
        public void FillFrame(ReadOnlySpan<byte> digest) {
            digest.CopyTo(Digests.AsSpan(0, SHA256.HashSizeInBytes));
            Frame = ContentPin.FromDigest(Digests.AsSpan(0, SHA256.HashSizeInBytes));
            FrameDocument = null;
            m_identityStale = true;
        }
        public void Settle() {
            if (!Dirty) { return; }
            m_identityStale = true; Disclosed = null; Dirty = false;
        }
    }
    private readonly WorkCounterSet m_work = new(name: SourceName, kinds: WorkKindOrder);
    private readonly System.Buffers.ArrayBufferWriter<byte> m_imageBytes = new();
    private readonly List<int> m_keyScratch = [];
    private byte[] m_maskScratch = [];
    private readonly List<int> m_movedScratch = [];
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_affectedScratch = new();
    private StateArena? m_exportArena;
    private StateRow?[] m_exports = [];
    /// <summary>Gets the table's monotonic work totals, an operator measurement like <see cref="Describe"/>.</summary>
    public IWorkCounterSource Work => m_work;
    private StateArena? m_arena;
    private long m_policyRevision = -1;
    private long m_policyGeneration;
    private StateVisibility[] m_policies = [];
    private int[] m_policyReaderRows = [];
    private int[][] m_dependencies = [];
    // Prepare's scratch, refilled on every revision: each row's own policies, and one row's policies and dependencies.
    private HashSet<StateVisibility>?[] m_rowPolicies = [];
    private readonly HashSet<StateVisibility> m_policyScratch = [];
    private readonly HashSet<StateVisibility> m_rowPolicyScratch = [];
    private readonly List<int> m_dependencyScratch = [];
    private int[][] m_dependents = [];
    private readonly Dictionary<(int Row, string Audience), Entry> m_rows = [];
    // Per row of the arena the table is current to: the composition every whole reader shares, the compositions of the
    // masks its cell-by-cell readers answer, and the keys it holds, as of the structure version they were read at.
    private Composition?[] m_whole = [];
    private List<Composition>?[] m_masked = [];
    private WorldRowMembers?[] m_members = [];
    private ulong[] m_memberStructures = [];
    private long m_window;
    private readonly Dictionary<string, AudienceDefinition> m_definitions = [];
    [SideTable(reason: "records consecutive accepted value installs on the live authority")]
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldDefinition, object> m_valueSources = new();

    /// <summary>Registers two installed definitions whose state values alone differ.</summary>
    public void ContinueValues(WorldDefinition before, WorldDefinition after) =>
        m_valueSources.AddOrUpdate(after, m_valueSources.GetValue(before, static _ => new object()));
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

    /// <summary>Gets the row compositions during the current tick, independent of recipient count.</summary>
    public long Compositions { get; private set; }
    /// <summary>Gets the canonical image bytes composed during the current tick.</summary>
    public long ComposedBytes { get; private set; }
    /// <summary>Gets the key changes proven irrelevant to a cached image during this tick.</summary>
    public long Skipped { get; private set; }
    /// <summary>Gets the whole disclosed definitions composed during the current tick, independent of recipient count.</summary>
    public long DefinitionsComposed { get; private set; }
    /// <summary>Starts the world's next disclosure window. The carrier accumulates its committed writes.</summary>
    /// <param name="changes">The carrier of the window's committed key changes.</param>
    public void BeginTick(ArenaChangedKeys changes) {
        m_changes = changes; m_windowArena = m_arena; Compositions = 0; ComposedBytes = 0; Skipped = 0; DefinitionsComposed = 0;
        m_window++;
        // Capture each row once, independently of audience count. Only an image at least this current is covered
        // by the new window's key proof; a throttled stream may have missed a write whose keys were just cleared.
        if (m_windowArena is { } arena) {
            if (m_windowVersions.Length != arena.Rows.Count) { m_windowVersions = new ulong[arena.Rows.Count]; }
            for (var row = 0; row < m_windowVersions.Length; row++) { m_windowVersions[row] = arena.RowVersion(row); }
            m_work.Add(RowVisits, m_windowVersions.Length);
        }
    }
    /// <summary>Reports live shared image storage and current tick work, for the operator's budget.</summary>
    /// <returns>The one-line report.</returns>
    public string Describe() {
        var images = 0;
        var bytes = 0L;

        void Count(Composition? composition) {
            if (composition?.Image is { } image) { images++; bytes += image.Bytes; }
        }
        for (var row = 0; row < m_whole.Length; row++) {
            Count(composition: m_whole[row]);
            foreach (var composition in (m_masked[row] ?? [])) { Count(composition: composition); }
        }
        return $"disclosure images {images}, bytes {bytes}, tick compositions {Compositions}, composed bytes {ComposedBytes}, skipped {Skipped}, definitions {DefinitionsComposed}";
    }
    // Whether no journal scope is open on a store. Row versions and the policy revision settle when the outermost
    // scope commits, so while one is open the table reads the store as it stands and keeps nothing: no mask, digest,
    // image, definition, scene or placement reading it holds can be told current, and none is written, so a rewind
    // leaves the table as the scope found it.
    private static bool Settled(StateArena arena) => (arena.Journal.Scopes == 0);
    private void Prepare(WorldRecipientView view) {
        PrepareIndex(view);
        // An index read inside an open scope describes policies a rewind may take back: the next read builds it again.
        if (!Settled(view.Arena)) { m_policyRevision = -1; }
    }
    private void PrepareIndex(WorldRecipientView view) {
        var arena = view.Arena;
        if (ReferenceEquals(m_arena, arena) && m_policyRevision == arena.DisclosureRevision) { return; }
        var sameArena = ReferenceEquals(m_arena, arena);
        m_arena = arena; m_policyRevision = arena.DisclosureRevision;
        var count = arena.Rows.Count;
        // What the table holds per row describes one arena's rows: another arena, or another number of rows, starts it
        // empty. A relayout that keeps the count restarts every version, which every per-row record is checked against.
        if (!sameArena || (m_whole.Length != count)) {
            m_whole = new Composition?[count];
            m_masked = new List<Composition>?[count];
            m_members = new WorldRowMembers?[count];
            m_memberStructures = new ulong[count];
        }
        // Each row's own policies, its declaration's and every cell's, in sets this table keeps and refills.
        if (m_rowPolicies.Length != count) { m_rowPolicies = new HashSet<StateVisibility>?[count]; }
        for (var row = 0; row < count; row++) {
            var set = m_rowPolicies[row];
            set?.Clear();
            if (arena.Rows[row].Visibility is { } policy) { (set ??= m_rowPolicies[row] = []).Add(policy); }
            if (!arena.HasCellVisibility(row)) { continue; }
            var cursor = 0;
            while (arena.TryNextCell(row, ref cursor, out var key)) { if (arena.Visibility(row, key) is { } cell) { (set ??= m_rowPolicies[row] = []).Add(cell); } }
        }
        // The revision moves on every membership, order, cursor and audience write. Cached images and definitions are
        // keyed by policy bit positions and checked against row and dependency versions, so they hold while the index
        // they were built under is the same; a row whose dependencies stand keeps its array, so a revision that changes
        // no policy allocates nothing.
        var policies = m_policyScratch;
        policies.Clear();
        var dependencies = (sameArena && (m_dependencies.Length == count)) ? m_dependencies : new int[count][];
        for (var row = 0; row < count; row++) {
            var rowSet = m_rowPolicyScratch;
            var rowDependencies = m_dependencyScratch;
            rowSet.Clear(); rowDependencies.Clear();
            if (m_rowPolicies[row] is { } own) { foreach (var policy in own) { rowSet.Add(policy); } }
            var zones = arena.Catalog.ContainingZones(row);
            for (var index = 0; index < zones.Length; index++) {
                var zone = zones[index];
                if (!rowDependencies.Contains(zone)) { rowDependencies.Add(zone); }
                if (m_rowPolicies[zone] is { } zonePolicies) { foreach (var policy in zonePolicies) { rowSet.Add(policy); } }
            }
            foreach (var policy in rowSet) {
                policies.Add(policy);
                if (policy.ReadersFrom is { } readers && arena.Catalog.TryResolve(StateLane.Document, readers, out var source) &&
                    !rowDependencies.Contains(source.Ordinal)) { rowDependencies.Add(source.Ordinal); }
            }
            rowDependencies.Sort();
            if (ReferenceEquals(dependencies, m_dependencies) && CollectionsMarshal.AsSpan(rowDependencies).SequenceEqual(m_dependencies[row])) { continue; }
            if (ReferenceEquals(dependencies, m_dependencies)) { dependencies = [.. m_dependencies]; }
            dependencies[row] = [.. rowDependencies];
        }
        var samePolicies = sameArena && (policies.Count == m_policies.Length);
        if (samePolicies) {
            var index = 0;
            foreach (var policy in policies) { if (!policy.Equals(m_policies[index++])) { samePolicies = false; break; } }
        }
        if (samePolicies && ReferenceEquals(dependencies, m_dependencies)) { return; }
        m_policies = [.. policies];
        m_policyGeneration++;
        var readerRows = new SortedSet<int>();
        foreach (var policy in m_policies) {
            if (policy.ReadersFrom is { } readers && arena.Catalog.TryResolve(StateLane.Document, readers, out var source)) { readerRows.Add(source.Ordinal); }
        }
        m_policyReaderRows = [.. readerRows];
        m_dependencies = dependencies;
        var dependents = new List<int>[arena.Rows.Count];
        for (var row = 0; row < dependents.Length; row++) { dependents[row] = []; }
        for (var row = 0; row < dependencies.Length; row++) { foreach (var source in dependencies[row]) { dependents[source].Add(row); } }
        m_dependents = [.. dependents.Select(rows => rows.ToArray())];
        m_rows.Clear(); m_definitions.Clear(); m_placementReadings.Clear(); m_scenes.Clear();
        m_work.Count(IndexRebuilds);
    }
    // Dependency versions are compared as an exact tuple through a monotone sum: versions only increase within this
    // arena, whose replacement clears the table.
    private ulong DependencyVersions(StateArena arena, int row) {
        var dependencies = 0UL;
        foreach (var dependency in m_dependencies[row]) { dependencies = checked(dependencies + arena.RowVersion(dependency)); }
        return dependencies;
    }
    // Adds the authored rows whose image the changed rows can move (each row and the rows whose policies read it) to
    // affected, unordered.
    internal void Affected(WorldRecipientView view, ReadOnlySpan<int> rows, Puck.Abstractions.Collections.MarkedIndexSet affected) {
        Prepare(view);
        AddAffected(rows, view.Source.AuthoredState.Count, affected);
    }
    // Each changed row that is authored, and every authored row whose policies read a changed row (authored or not),
    // each once: the set's marks are what keep a row many changes name from being visited once per change.
    private void AddAffected(ReadOnlySpan<int> rows, int authored, Puck.Abstractions.Collections.MarkedIndexSet affected) {
        foreach (var row in rows) {
            if ((uint)row < (uint)authored) { affected.Add(row); }
            if ((uint)row >= (uint)m_dependents.Length) { continue; }
            foreach (var dependent in m_dependents[row]) {
                if (dependent < authored) { affected.Add(dependent); }
            }
        }
    }
    private string AudienceText(WorldRecipientView view) {
        Prepare(view);
        var arena = view.Arena;
        var settled = Settled(arena);
        var unrestricted = view.Unrestricted;
        var ceilingKey = view.Observes ? (char)('1' + (int)view.Ceiling) : '0';
        var readerVersions = 0UL;

        foreach (var row in m_policyReaderRows) { readerVersions = checked(readerVersions + arena.RowVersion(row)); }
        if (settled && (view.AudienceKey is { } cached) && ReferenceEquals(view.AudienceArena, arena) &&
            (view.AudiencePolicyGeneration == m_policyGeneration) && (view.AudienceReaderVersions == readerVersions) &&
            (view.AudienceCeilingKey == ceilingKey) && (view.AudienceUnrestricted == unrestricted)) {
            return cached;
        }
        var observer = view.ObserverAs(unrestricted);
        var length = (m_policies.Length + 2);
        var key = ((length <= 512) ? stackalloc char[length] : new char[length]);
        key[0] = ceilingKey;
        // An authority reads storage bookkeeping even on a declaration that can withhold cells. A recipient
        // admitted by every current policy still does not, so their definition caches cannot share a class.
        key[1] = unrestricted ? '1' : '0';
        for (var index = 0; index < m_policies.Length; index++) { key[index + 2] = observer.Allows(m_policies[index]) ? '1' : '0'; }
        // The key is a function of the answers alone, so a view whose answers stand keeps the string it was handed.
        var text = ((view.AudienceKey is { } held) && key.SequenceEqual(held)) ? held : new string(key);
        if (settled) {
            view.AudienceKey = text;
            view.AudienceArena = arena;
            view.AudiencePolicyGeneration = m_policyGeneration;
            view.AudienceReaderVersions = readerVersions;
            view.AudienceCeilingKey = ceilingKey;
            view.AudienceUnrestricted = unrestricted;
        }
        return text;
    }
    // The content identity of the recipient's disclosed definition. Two documents that disclose the same frame, rows and
    // row observations share it; work follows the rows that changed since the audience's last read.
    internal Sha256Digest DefinitionIdentity(WorldRecipientView view) {
        var entry = Current(view);
        _ = FrameOf(entry);
        return entry.Identity;
    }
    // The entry's frame identity, digesting the composed document less its rows the first time any identity is asked for.
    // A frame is digested in three parts: its placements section, its state section (rows already removed) and everything
    // else. A composition differs from its source only in those two sections (DiscloseDocument re-deals placements and
    // projects field lattices, nothing more). The rest shares its digest across holding moves and audiences. The
    // sections have their own digests, and the placement section reuses the canonical digest of each retained row.
    private ContentPin FrameOf(AudienceDefinition entry) {
        if (entry.FrameDocument is { } frame) {
            var basis = frame with { PlacementsRaw = null, StateRaw = null, CreationsRaw = null, ShadesRaw = null };
            if (!basis.Equals(m_frameBaseOf)) {
                m_frameBase = Digest(WorldDefinitionSerialization.SerializeCompact(basis));
                m_frameBaseOf = basis;
            }
            Span<byte> parts = stackalloc byte[5 * SHA256.HashSizeInBytes];
            m_frameBase.CopyTo(parts);
            PlacementDigest(frame.PlacementsRaw).CopyTo(parts[SHA256.HashSizeInBytes..]);
            StateDigest(frame.StateRaw).CopyTo(parts[(2 * SHA256.HashSizeInBytes)..]);
            RowsDigest(frame.CreationsRaw, m_creationDigests, m_creationLists).CopyTo(parts[(3 * SHA256.HashSizeInBytes)..]);
            ShadeDigest(frame.ShadesRaw).CopyTo(parts[(4 * SHA256.HashSizeInBytes)..]);
            entry.FillFrame(SHA256.HashData(parts));
        }
        return entry.Frame;
    }
    private static readonly byte[] AbsentSection = SHA256.HashData(ReadOnlySpan<byte>.Empty);
    private readonly Puck.Abstractions.CodeDerivedTable<WorldPlacement, byte[]> m_placementDigests = new();
    private readonly Puck.Abstractions.CodeDerivedTable<WorldPlacementsSection, byte[]> m_placementSections = new();
    private readonly Puck.Abstractions.CodeDerivedTable<WorldStateSection, byte[]> m_stateSections = new();
    private readonly Puck.Abstractions.CodeDerivedTable<WorldPrototype, byte[]> m_creationDigests = new();
    private readonly Puck.Abstractions.CodeDerivedTable<IReadOnlyList<WorldPrototype>, byte[]> m_creationLists = new();
    private readonly Puck.Abstractions.CodeDerivedTable<Puck.SignedDistance.SdfShadeDefinition, byte[]> m_shadeDigests = new();
    private readonly Puck.Abstractions.CodeDerivedTable<IReadOnlyDictionary<string, Puck.SignedDistance.SdfShadeDefinition>, byte[]> m_shadeTables = new();
    private readonly System.Buffers.ArrayBufferWriter<byte> m_frameLeaves = new();

    private byte[] PlacementDigest(WorldPlacementsSection? section) {
        if (section is null) { return AbsentSection; }
        if (m_placementSections.TryGetValue(section, out var stored)) { return stored; }
        // The header and ordered, fixed-width leaves distinguish absent and empty row lists and preserve row order.
        // An immutable row reused across a holding patch or a disclosure never serializes its bytes again.
        var header = section with { Rows = (section.Rows is null ? null : []) };
        m_frameLeaves.ResetWrittenCount();
        m_frameLeaves.Write(DigestSection(header));
        WriteRowDigests(section.Rows, m_placementDigests);
        stored = SHA256.HashData(m_frameLeaves.WrittenSpan);
        m_placementSections.AddOrUpdate(section, stored);
        return stored;
    }

    private byte[] RowsDigest<T>(IReadOnlyList<T>? rows, Puck.Abstractions.CodeDerivedTable<T, byte[]> digests,
        Puck.Abstractions.CodeDerivedTable<IReadOnlyList<T>, byte[]> lists) where T : class {
        if (rows is null) { return AbsentSection; }
        if (lists.TryGetValue(rows, out var digest)) { return digest; }
        m_frameLeaves.ResetWrittenCount();
        WriteRowDigests(rows, digests);
        digest = SHA256.HashData(m_frameLeaves.WrittenSpan.Length == 0 ? [0] : m_frameLeaves.WrittenSpan);
        lists.AddOrUpdate(rows, digest);
        return digest;
    }

    private void WriteRowDigests<T>(IReadOnlyList<T>? rows, Puck.Abstractions.CodeDerivedTable<T, byte[]> digests) where T : class {
        if (rows is null) { return; }
        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            if (!digests.TryGetValue(row, out var digest)) {
                digest = DigestSection(row);
                digests.AddOrUpdate(row, digest);
            }
            m_frameLeaves.Write(digest);
        }
    }

    private byte[] ShadeDigest(IReadOnlyDictionary<string, Puck.SignedDistance.SdfShadeDefinition>? shades) {
        if (shades is null) { return AbsentSection; }
        if (m_shadeTables.TryGetValue(shades, out var digest)) { return digest; }
        m_frameLeaves.ResetWrittenCount();
        m_frameLeaves.Write<byte>([0]);
        foreach (var (name, shade) in shades.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
            m_frameLeaves.Write(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
            if (!m_shadeDigests.TryGetValue(shade, out var leaf)) {
                leaf = DigestSection(shade);
                m_shadeDigests.AddOrUpdate(shade, leaf);
            }
            m_frameLeaves.Write(leaf);
        }
        digest = SHA256.HashData(m_frameLeaves.WrittenSpan);
        m_shadeTables.AddOrUpdate(shades, digest);
        return digest;
    }

    private byte[] StateDigest(WorldStateSection? section) {
        if (section is null) { return AbsentSection; }
        if (!m_stateSections.TryGetValue(section, out var digest)) {
            digest = DigestSection(section);
            m_stateSections.AddOrUpdate(section, digest);
        }
        return digest;
    }
    // An absent section digests no bytes, which no serialized section is.
    private byte[] DigestSection<T>(T? section) where T : class => (section is null)
        ? SHA256.HashData(ReadOnlySpan<byte>.Empty)
        : Digest(CanonicalJsonDocument.SerializeCompact(section, WorldJsonContext.Default.For<T>()));
    private byte[] Digest(byte[] bytes) {
        m_work.Add(DefinitionBytes, bytes.Length);
        return SHA256.HashData(bytes);
    }
    // The last frame with both variable sections removed.
    private WorldDefinition? m_frameBaseOf;
    private byte[] m_frameBase = [];
    // The content identity of the recipient's disclosed document less its state rows: placements and every other
    // section a row snapshot does not carry. A value-only delivery owes the document again exactly when it moved.
    // Only the source, the audience and the images of the rows the placements read (FrameRows) move it, so only those
    // rows are imaged here: every other changed row waits for the definition's own identity (Current), which a session
    // sink's per-tick check never asks for.
    internal ContentPin FrameIdentity(WorldRecipientView view) => Framed(Audience(view)).Frame;
    // The audience's definition with its frame current and digested.
    private AudienceDefinition Framed(AudienceKey audience) {
        var view = audience.View;
        var entry = DefinitionFor(audience);

        if (FrameRowsMoved(audience, entry)) { entry = Compose(audience); }
        _ = FrameOf(entry);
        return entry;
    }
    internal bool TryAdvanceAppearance(WorldRecipientView view, ContentPin? held, Sha256Digest? heldIdentity, out ContentPin next, out Sha256Digest identity) {
        var entry = Current(view);
        next = FrameOf(entry);
        identity = entry.Identity;
        return (held == next && heldIdentity == identity) ||
            (held is not null && held == entry.BeforeAppearance && heldIdentity == entry.BeforeAppearanceIdentity);
    }
    private bool FrameRowsMoved(AudienceKey audience, AudienceDefinition entry) {
        var view = audience.View;
        var arena = view.Arena;
        m_work.Add(RowVisits, entry.FrameRowOrdinals.Length);
        foreach (var row in entry.FrameRowOrdinals) {
            // The entry's row record changes only in Current, which refreshes skeletons with it; here a row whose image
            // stands is left to Current, and the image table answers its repeated reads from its own cache.
            if ((arena.RowVersion(row) == entry.Versions[row]) && (DependencyVersions(arena, row) == entry.Dependencies[row])) { continue; }
            if (!entry.Holds(AudienceDefinition.ImageSlot(row), RowDigest(audience, row))) { return true; }
        }
        return false;
    }
    // The recipient's disclosed definition, assembled only after its identity moved. Its rows' cells are built here,
    // from the store the entry was just brought current to: a tick that only identifies the definition builds none.
    internal WorldDefinition Definition(WorldRecipientView view) {
        var entry = Current(view);
        if (entry.Disclosed is { } disclosed) { return disclosed; }
        if (entry.Composed.StateRaw is not { } state) { return entry.Disclosed = entry.Composed; }
        var audience = Audience(view);
        var rows = new WorldStateRow[entry.Rows.Length];

        for (var row = 0; row < rows.Length; row++) { rows[row] = entry.DisclosedRow(row: row, cells: Image(audience, row).Cells); }
        // The disclosed section keeps the composed one's shape, so its catalog and the other compiled products that
        // read state carry across (WithStateSection) and a reader keeps the arena it shaped for that catalog.
        return entry.Disclosed = entry.Composed.WithStateSection(state: state with { World = rows });
    }
    // Brings one audience's definition current: rows whose version and dependency versions stand are skipped without a
    // lookup, a changed row refreshes its image digest and, when its structure moved, its skeleton digest.
    private AudienceDefinition Current(WorldRecipientView view) {
        var audience = Audience(view);
        var entry = DefinitionFor(audience);
        var arena = view.Arena;
        // Inside an open scope the definition was composed from the store as it stands and is kept nowhere.
        if (!Settled(arena)) { return entry; }
        var moved = m_movedScratch;
        moved.Clear();
        // A row's version and its dependencies' versions move only through the arena's version moves, so the rows it
        // names since this entry was last brought current, and the rows whose policies read them, are the only ones
        // that can differ from the record. Each is visited once, so a pass costs the distinct rows that changed and
        // their readers, never the number of writes. An entry the arena cannot serve (another arena, or a wholesale
        // reset since) checks every row.
        var place = entry.MovesRead;
        // Another arena's place means nothing here: the scan below reads every row, from the place read before it.
        var named = ReferenceEquals(entry.Arena, arena) ? arena.TryReadMoves(ref place, moved) : NoPlace(arena, out place);
        if (named) {
            var affected = m_affectedScratch;
            affected.Clear();
            AddAffected(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(moved), entry.Rows.Length, affected);
            foreach (var row in affected.Members) {
                if (!Refresh(entry, audience, row)) { return Compose(audience); }
            }
        } else {
            m_work.Count(FullScans);
            for (var row = 0; row < entry.Rows.Length; row++) {
                if (!Refresh(entry, audience, row)) { return Compose(audience); }
            }
        }
        entry.Arena = arena;
        entry.MovesRead = place;
        entry.Revision = arena.DisclosureRevision;
        entry.Settle();
        return entry;
    }

    private readonly List<(int Row, int Holding)> m_holdingRows = [];

    private AudienceDefinition DefinitionFor(AudienceKey audience) {
        var view = audience.View;
        if (!Settled(view.Arena) || !m_definitions.TryGetValue(audience.Text, out var entry)) { return Compose(audience); }
        var source = view.Source;
        if (ReferenceEquals(entry.Source, source)) { return entry; }

        if (ReferenceEquals(entry.Arena, view.Arena) && entry.Revision == view.Arena.DisclosureRevision &&
            m_valueSources.TryGetValue(source, out var values) && m_valueSources.TryGetValue(entry.Source, out var heldValues) &&
            ReferenceEquals(values, heldValues)) {
            entry.AdoptValues(source);
            return entry;
        }

        if (ReferenceEquals(entry.Arena, view.Arena) && entry.Revision == view.Arena.DisclosureRevision &&
            WorldDocumentInputs.Changed(entry.Source, source).Except(WorldPresentationRows.Parts).IsEmpty &&
            !FrameRowsMoved(audience, entry)) {
            entry.AdoptAppearance(source, FrameOf(entry));
            return entry;
        }

        var changes = source.HoldingsFrom(entry.Source);
        if (changes.IsEmpty ||
            !ReferenceEquals(entry.Arena, view.Arena) ||
            entry.Revision != view.Arena.DisclosureRevision ||
            FrameRowsMoved(audience, entry)) {
            return Compose(audience);
        }
        return AdoptHoldings(audience, entry, source, changes);
    }
    // A direct row can be patched even if the audience re-deals other placements. Reuse the existing composer for
    // derived children, transformed or omitted rows, and newly active bits that read restricted state. Apart from
    // DefinitionFor, which every tick calls, so the test below captures the view only when holdings moved.
    private AudienceDefinition AdoptHoldings(AudienceKey audience, AudienceDefinition entry, WorldDefinition source,
        ReadOnlySpan<(int Row, int Holding)> changes) {
        var view = audience.View;
        bool Restricted(string name, string? _) => !view.ReadsWhole(name);
        m_holdingRows.Clear();
        foreach (var (row, holding) in changes) {
            var previous = entry.Source.Placements[row];
            var ordinal = entry.PlacementOrdinal(previous, row);
            if (ordinal < 0 || previous != entry.Composed.Placements[ordinal] ||
                (previous.Parent is not null && WorldPlacementDeal.TrySplitChildId(previous.Id, out _, out _))) {
                return Compose(audience);
            }
            var placement = source.Placements[row];
            if (!ReferenceEquals(placement, WorldPlacementResponse.Disclosed(placement, Restricted))) {
                return Compose(audience);
            }
            m_holdingRows.Add((ordinal, holding));
        }
        entry.AdoptHoldings(source, m_holdingRows);
        m_work.Add(HoldingRows, changes.Length);
        return entry;
    }
    private static bool NoPlace(StateArena arena, out long place) { place = arena.MoveSequence; return false; }
    // Brings one row of an audience's definition current, reporting false when the frame moved with it and the whole
    // definition must be composed again. A row beyond the authored rows, or one whose version and dependency versions
    // stand as recorded, is left as it is.
    private bool Refresh(AudienceDefinition entry, AudienceKey audience, int row) {
        var view = audience.View;
        if ((uint)row >= (uint)entry.Rows.Length) { return true; }
        var arena = view.Arena;
        m_work.Count(RowVisits);
        var version = arena.RowVersion(row);
        var dependencies = DependencyVersions(arena, row);
        if ((version == entry.Versions[row]) && (dependencies == entry.Dependencies[row])) { return true; }
        var digest = RowDigest(audience, row);
        if (entry.FrameRows[row] && !entry.Holds(AudienceDefinition.ImageSlot(row), digest)) { return false; }
        entry.Versions[row] = version; entry.Dependencies[row] = dependencies;
        entry.Store(AudienceDefinition.ImageSlot(row), digest);
        // A skeleton carries no cell, so only a write that moved the row's structure can have changed it: a value,
        // clock, behaviour or stamp write leaves it, and so does a move of a row this one only depends on.
        var structure = arena.RowStructureVersion(row);

        if (structure != entry.Structures[row]) {
            var observer = view.Observer;
            var skeleton = WorldStateDisclosure.DiscloseRow(arena, row, ref observer, entry.Fields, out _) with { Cells = null };
            entry.Structures[row] = structure;
            entry.Rows[row] = skeleton;
            entry.Store(AudienceDefinition.SkeletonSlot(row), Sha256Digest.Of(DigestOf(skeleton)));
        }
        return true;
    }
    private AudienceDefinition Compose(AudienceKey audience) {
        var view = audience.View;
        var source = view.Source;
        var arena = view.Arena;
        var count = source.AuthoredState.Count;
        // Read before any row: a row that moved after this place is one the next pass visits.
        var moves = arena.MoveSequence;
        var versions = new ulong[count];
        var dependencies = new ulong[count];
        var structures = new ulong[count];
        var digests = new byte[AudienceDefinition.ImageSlot(count)];
        for (var row = 0; row < count; row++) {
            versions[row] = arena.RowVersion(row);
            dependencies[row] = DependencyVersions(arena, row);
            structures[row] = arena.RowStructureVersion(row);
            RowDigest(audience, row).CopyTo(digests.AsSpan(AudienceDefinition.ImageSlot(row), SHA256.HashSizeInBytes));
        }
        var fields = WorldStateDisclosure.DisclosedFields.For(source, arena, view.Recipient, view.Unrestricted);
        var disclosed = WorldStateDisclosure.DiscloseDocument(source, arena, view.Recipient, view.Unrestricted, fields);
        var composed = disclosed.Definition;
        var rows = composed.AuthoredState.Select(row => row with { Cells = null }).ToArray();
        var frame = (composed.StateRaw is { } state) ? composed with { StateRaw = state with { World = [] } } : composed;
        // FrameOf shares the source's frame less these two sections with every composition, so a disclosure that changed
        // any other section would be digested as its source: refused here instead.
        if (!(composed with { PlacementsRaw = source.PlacementsRaw, StateRaw = source.StateRaw }).Equals(source)) {
            throw new InvalidOperationException("A disclosed document differs from its source beyond its placements and state sections.");
        }
        for (var row = 0; row < count; row++) {
            DigestOf(rows[row]).CopyTo(digests.AsSpan(AudienceDefinition.SkeletonSlot(row), SHA256.HashSizeInBytes));
        }
        var frameRows = rows.Select(row => disclosed.PlacementReads.Contains(row.Name.Value)).ToArray();
        DefinitionsComposed++;
        m_work.Count(DefinitionCompositions);
        var entry = new AudienceDefinition(source, composed, frame, fields, frameRows, rows, versions, dependencies,
            arena.DisclosureRevision, digests) { Arena = arena, MovesRead = moves, Structures = structures };

        if (Settled(arena)) {
            entry.Inherit(previous: m_definitions.GetValueOrDefault(audience.Text));
            m_definitions[audience.Text] = entry;
        }
        return entry;
    }
    private byte[] DigestOf(WorldStateRow skeleton) => Digest(CanonicalJsonDocument.SerializeCompact(skeleton, WorldJsonContext.Default.WorldStateRow));
}
