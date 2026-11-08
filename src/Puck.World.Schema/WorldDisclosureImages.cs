using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Assets;

namespace Puck.World;

/// <summary>One immutable observable row. Vector payloads are privately copied before content identity is computed.</summary>
public sealed class WorldRecipientImage {
    internal WorldRecipientImage(byte[] digest, StateCell[] cells, WorldObservedRow? observation, int bytes) {
        Digest = digest; Cells = Array.AsReadOnly(cells); Observation = observation; Bytes = bytes;
    }
    // Spelled on first read: most images (a row its audience reads whole) are compared by digest alone. A pin is one
    // reference, so a racing first read writes it whole and every writer writes the same text.
    private ContentPin m_identity;
    /// <summary>Content identity, including counts, placeholder order, traits and observations.</summary>
    public ContentPin Identity => (m_identity == default) ? (m_identity = ContentPin.FromDigest(Digest)) : m_identity;
    /// <summary>Readable cells in recipient order.</summary>
    public IReadOnlyList<StateCell> Cells { get; }
    /// <summary>The presentation image, or null when the row itself is withheld.</summary>
    public WorldObservedRow? Observation { get; }
    internal int Bytes { get; }
    // The SHA-256 digest Identity spells; never written after construction.
    internal byte[] Digest { get; }
}
internal sealed record WorldRecipientImageContent(StateCell[] Cells, WorldObservedRow? Observation);

/// <summary>World-owned, content-interned row images and the disclosed definitions composed from them. Compositions are
/// shared by ceiling, authority bypass and the policies admitting the reader. Current audience images keep their content
/// alive; replaced content is weakly retained.</summary>
public sealed class WorldDisclosureImages {
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
    private static readonly WorkKind[] WorkKindOrder = [RowCompositions, RowBytes, RowSkips, IndexRebuilds, DefinitionCompositions, DefinitionBytes];

    private sealed record Entry(ulong Version, ulong Dependencies, WorldRecipientImage Image, int[] Keys);
    // One audience's disclosed definition. Between whole compositions it changes only row by row: a keyed write changes
    // a row's cells, which its image carries, and every other write moves the arena's DisclosureRevision, after which a
    // changed row's skeleton (its declaration and storage, cells removed) is derived again. Everything else in the
    // document (the frame) moves only with the source, the audience or a withheld row the placements read, and each of
    // those composes the whole definition again. Identity digests the frame and every row's image and skeleton digest.
    private sealed class AudienceDefinition(WorldDefinition source, WorldDefinition composed, WorldStateDisclosure.DisclosedFields? fields,
        bool[] frameRows, WorldStateRow[] rows, WorldRecipientImage[] images, ulong[] versions, ulong[] dependencies, long revision, byte[] digests) {
        public WorldDefinition Source { get; } = source;
        public WorldDefinition Composed { get; } = composed;
        public WorldStateDisclosure.DisclosedFields? Fields { get; } = fields;
        public bool[] FrameRows { get; } = frameRows;
        // The ordinals FrameRows marks, in row order: the only rows whose images can move the frame.
        public int[] FrameRowOrdinals { get; } = [.. Enumerable.Range(0, frameRows.Length).Where(row => frameRows[row])];
        public WorldStateRow[] Rows { get; } = rows;
        public WorldRecipientImage[] Images { get; } = images;
        public ulong[] Versions { get; } = versions;
        public ulong[] Dependencies { get; } = dependencies;
        public long Revision { get; set; } = revision;
        public byte[] Digests { get; } = digests;
        public ContentPin Identity { get; private set; } = ContentPin.Compute(digests);
        // The frame slot is written once, at composition: rows refresh only their own slots.
        public ContentPin Frame { get; } = ContentPin.FromDigest(digests.AsSpan(0, SHA256.HashSizeInBytes));
        public WorldDefinition? Disclosed { get; set; }
        public bool Dirty { get; set; }

        public static int ImageSlot(int row) => (1 + (2 * row)) * SHA256.HashSizeInBytes;
        public static int SkeletonSlot(int row) => (2 + (2 * row)) * SHA256.HashSizeInBytes;
        public void Store(int slot, ReadOnlySpan<byte> digest) {
            var target = Digests.AsSpan(slot, SHA256.HashSizeInBytes);
            if (target.SequenceEqual(digest)) { return; }
            digest.CopyTo(target);
            Dirty = true;
        }
        public void Settle() {
            if (!Dirty) { return; }
            Identity = ContentPin.Compute(Digests); Disclosed = null; Dirty = false;
        }
    }
    private readonly WorkCounterSet m_work = new(name: SourceName, kinds: WorkKindOrder);
    private readonly System.Buffers.ArrayBufferWriter<byte> m_imageBytes = new();
    private Utf8JsonWriter? m_imageWriter;
    private readonly List<StateCell> m_cellScratch = [];
    private StateArena? m_exportArena;
    private StateRow?[] m_exports = [];
    /// <summary>Gets the table's monotonic work totals, an operator measurement like <see cref="Describe"/>.</summary>
    public IWorkCounterSource Work => m_work;
    private StateArena? m_arena;
    private long m_policyRevision = -1;
    private StateVisibility[] m_policies = [];
    private int[][] m_dependencies = [];
    // Prepare's scratch, refilled on every revision: each row's own policies, and one row's policies and dependencies.
    private HashSet<StateVisibility>?[] m_rowPolicies = [];
    private readonly HashSet<StateVisibility> m_policyScratch = [];
    private readonly HashSet<StateVisibility> m_rowPolicyScratch = [];
    private readonly List<int> m_dependencyScratch = [];
    private int[][] m_dependents = [];
    private readonly Dictionary<(int Row, string Audience), Entry> m_rows = [];
    private readonly Dictionary<ImageDigest, WeakReference<WorldRecipientImage>> m_content = [];
    private readonly List<ImageDigest> m_reclaimed = [];
    private readonly Dictionary<string, AudienceDefinition> m_definitions = [];
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
        // Capture each row once, independently of audience count. Only an image at least this current is covered
        // by the new window's key proof; a throttled stream may have missed a write whose keys were just cleared.
        if (m_windowArena is { } arena) {
            if (m_windowVersions.Length != arena.Rows.Count) { m_windowVersions = new ulong[arena.Rows.Count]; }
            for (var row = 0; row < m_windowVersions.Length; row++) { m_windowVersions[row] = arena.RowVersion(row); }
        }
        foreach (var (pin, reference) in m_content) { if (!reference.TryGetTarget(out _)) { m_reclaimed.Add(pin); } }
        foreach (var pin in m_reclaimed) { m_content.Remove(pin); }
        m_reclaimed.Clear();
    }
    /// <summary>Reports live shared image storage and current tick work, for the operator's budget.</summary>
    /// <returns>The one-line report.</returns>
    public string Describe() {
        var held = m_content.Values.Select(reference => reference.TryGetTarget(out var image) ? image : null).OfType<WorldRecipientImage>().ToArray();
        return $"disclosure images {held.Length}, bytes {held.Sum(image => (long)image.Bytes)}, tick compositions {Compositions}, composed bytes {ComposedBytes}, skipped {Skipped}, definitions {DefinitionsComposed}";
    }
    private void Prepare(WorldRecipientView view) {
        var arena = view.Arena;
        if (ReferenceEquals(m_arena, arena) && m_policyRevision == arena.DisclosureRevision) { return; }
        var sameArena = ReferenceEquals(m_arena, arena);
        m_arena = arena; m_policyRevision = arena.DisclosureRevision;
        var count = arena.Rows.Count;
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
            for (var index = 0; index < zones.Count; index++) {
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
        m_dependencies = dependencies;
        var dependents = new List<int>[arena.Rows.Count];
        for (var row = 0; row < dependents.Length; row++) { dependents[row] = []; }
        for (var row = 0; row < dependencies.Length; row++) { foreach (var source in dependencies[row]) { dependents[source].Add(row); } }
        m_dependents = [.. dependents.Select(rows => rows.ToArray())];
        m_rows.Clear(); m_definitions.Clear();
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
    internal void Affected(WorldRecipientView view, ReadOnlySpan<int> rows, HashSet<int> affected) {
        Prepare(view);
        var authored = view.Source.AuthoredState.Count;
        foreach (var row in rows) {
            if ((uint)row >= (uint)authored) { continue; }
            affected.Add(row);
            foreach (var dependent in m_dependents[row]) {
                if (dependent < authored) { affected.Add(dependent); }
            }
        }
    }
    internal string Audience(WorldRecipientView view) {
        Prepare(view);
        var observer = view.Observer;
        var length = (m_policies.Length + 2);
        var key = ((length <= 512) ? stackalloc char[length] : new char[length]);
        key[0] = view.Observes ? (char)('1' + (int)view.Ceiling) : '0';
        // An authority reads storage bookkeeping even on a declaration that can withhold cells. A recipient
        // admitted by every current policy still does not, so their definition caches cannot share a class.
        key[1] = view.Unrestricted ? '1' : '0';
        for (var index = 0; index < m_policies.Length; index++) { key[index + 2] = observer.Allows(m_policies[index]) ? '1' : '0'; }
        // The key is a function of the answers alone, so a view whose answers stand keeps the string it was handed.
        if ((view.AudienceKey is { } held) && key.SequenceEqual(held)) { return held; }
        return view.AudienceKey = new string(key);
    }
    internal WorldRecipientImage Image(WorldRecipientView view, int row) => Image(view, row, Audience(view));
    internal WorldRecipientImage Image(WorldRecipientView view, int row, string audience) {
        var arena = view.Arena;
        var version = arena.RowVersion(row);
        var dependencies = DependencyVersions(arena, row);
        var key = (row, audience);
        if (m_rows.TryGetValue(key, out var entry) && entry.Dependencies == dependencies) {
            if (entry.Version == version) { return entry.Image; }
            if (ReferenceEquals(arena, m_windowArena) && entry.Version >= m_windowVersions[row] &&
                m_changes is not null && !m_changes.Overlaps(row, entry.Keys) &&
                !arena.ChangedKeys.Overlaps(row, entry.Keys)) {
                Skipped++;
                m_work.Count(RowSkips);
                m_rows[key] = entry with { Version = version };
                return entry.Image;
            }
        }
        var observer = view.Observer;
        var includes = view.Includes(row);
        m_cellScratch.Clear();
        if (includes && (Export(arena, row).Cells is { } exported)) {
            foreach (var cell in exported) {
                if (!observer.Discloses(row, cell.Key.Value)) { continue; }
                m_cellScratch.Add((cell.Value.Kind == CellKind.Vector) ? cell with { Value = CellValue.Vector(cell.Value.AsVector.ToArray()) } : cell);
            }
        }
        var cells = m_cellScratch.ToArray();
        // The row itself is presented, so it crosses as an observation whatever policy it declares.
        var declaration = arena.Rows[row];
        var observation = (includes && !declaration.HostOwned && observer.TryRow(declaration, out var ordinal) && (ordinal == row))
            ? WorldStateDisclosure.ComposeRow(arena, ref observer, declaration, row, presented: true) : null;
        if (observation is not null) { observation = observation with { Cells = Array.AsReadOnly(observation.Cells.ToArray()) }; }
        // The observation and replica cells together cover all observable state, including cell traits the replica carries.
        var bytes = Serialize(new WorldRecipientImageContent(cells, observation));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        Compositions++; ComposedBytes += bytes.Length;
        m_work.Count(RowCompositions); m_work.Add(RowBytes, bytes.Length);
        // A write that leaves the row's disclosed content as it was keeps the image it had.
        var image = ((entry is not null) && digest.SequenceEqual(entry.Image.Digest)) ? entry.Image : Intern(digest, cells, observation, bytes.Length);
        var keys = ((entry is not null) && ReferenceEquals(image, entry.Image)) ? entry.Keys : new int[cells.Length];
        if (!ReferenceEquals(keys, entry?.Keys)) {
            for (var index = 0; index < cells.Length; index++) { keys[index] = arena.Keys.TryResolve(cells[index].Key, out var interned) ? interned.Ordinal : -1; }
        }
        m_rows[key] = new Entry(version, dependencies, image, keys);
        return image;
    }
    // A row's live export, sharing every cell its previous export from the same arena still holds unchanged.
    private StateRow Export(StateArena arena, int row) {
        if (!ReferenceEquals(m_exportArena, arena) || (m_exports.Length != arena.Rows.Count)) {
            m_exportArena = arena;
            m_exports = new StateRow?[arena.Rows.Count];
        }
        return m_exports[row] = arena.ToRow(row, previous: m_exports[row]);
    }
    // The shared image of one disclosed content: the live one with this digest, or a new one the table holds weakly.
    private WorldRecipientImage Intern(ReadOnlySpan<byte> digest, StateCell[] cells, WorldObservedRow? observation, int bytes) {
        var key = ImageDigest.Of(digest);
        if (!m_content.TryGetValue(key, out var reference) || !reference.TryGetTarget(out var image)) {
            image = new WorldRecipientImage(digest.ToArray(), cells, observation, bytes);
            m_content[key] = new WeakReference<WorldRecipientImage>(image);
        }
        return image;
    }
    // A SHA-256 digest as a value, so interning an image spells no pin.
    private readonly record struct ImageDigest(ulong A, ulong B, ulong C, ulong D) {
        public static ImageDigest Of(ReadOnlySpan<byte> digest) => new(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }
    // Serializes an image's content into the table's reused buffer, valid until the next call: the bytes are digested and
    // measured, never kept.
    private ReadOnlySpan<byte> Serialize(WorldRecipientImageContent content) {
        m_imageBytes.ResetWrittenCount();
        if (m_imageWriter is null) {
            var options = WorldJsonContext.Default.Options;
            m_imageWriter = new Utf8JsonWriter(m_imageBytes, new JsonWriterOptions { Encoder = options.Encoder, Indented = options.WriteIndented, MaxDepth = options.MaxDepth });
        } else {
            m_imageWriter.Reset(m_imageBytes);
        }
        JsonSerializer.Serialize(m_imageWriter, content, WorldJsonContext.Default.For<WorldRecipientImageContent>());
        m_imageWriter.Flush();
        return m_imageBytes.WrittenSpan;
    }
    // The content identity of the recipient's disclosed definition. Two documents that disclose the same frame, rows and
    // row observations share it; work follows the rows that changed since the audience's last read.
    internal ContentPin DefinitionIdentity(WorldRecipientView view) => Current(view).Identity;
    // The content identity of the recipient's disclosed document less its state rows: placements and every other
    // section a row snapshot does not carry. A value-only delivery owes the document again exactly when it moved.
    // Only the source, the audience and the images of the rows the placements read (FrameRows) move it, so only those
    // rows are imaged here: every other changed row waits for the definition's own identity (Current), which a session
    // sink's per-tick check never asks for.
    internal ContentPin FrameIdentity(WorldRecipientView view) {
        var audience = Audience(view);
        if (!m_definitions.TryGetValue(audience, out var entry) || !ReferenceEquals(entry.Source, view.Source)) { return Compose(view, audience).Frame; }
        var arena = view.Arena;
        foreach (var row in entry.FrameRowOrdinals) {
            // The entry's row record changes only in Current, which refreshes skeletons with it; here a row whose image
            // stands is left to Current, and the image table answers its repeated reads from its own cache.
            if ((arena.RowVersion(row) == entry.Versions[row]) && (DependencyVersions(arena, row) == entry.Dependencies[row])) { continue; }
            if (!Image(view, row, audience).Digest.AsSpan().SequenceEqual(entry.Images[row].Digest)) { return Compose(view, audience).Frame; }
        }
        return entry.Frame;
    }
    // The recipient's disclosed definition, assembled from its current rows only after its identity moved.
    internal WorldDefinition Definition(WorldRecipientView view) {
        var entry = Current(view);
        if (entry.Disclosed is { } disclosed) { return disclosed; }
        return entry.Disclosed = (entry.Composed.StateRaw is { } state)
            ? entry.Composed with { StateRaw = state with { World = entry.Rows.Select((row, ordinal) => row with { Cells = entry.Images[ordinal].Cells }).ToArray() } }
            : entry.Composed;
    }
    // Brings one audience's definition current: rows whose version and dependency versions stand are skipped without a
    // lookup, a changed row refreshes its image digest and, after a revision move, its skeleton digest.
    private AudienceDefinition Current(WorldRecipientView view) {
        var audience = Audience(view);
        var source = view.Source;
        if (!m_definitions.TryGetValue(audience, out var entry) || !ReferenceEquals(entry.Source, source)) { return Compose(view, audience); }
        var arena = view.Arena;
        var skeletons = (entry.Revision != arena.DisclosureRevision);
        for (var row = 0; row < entry.Images.Length; row++) {
            var version = arena.RowVersion(row);
            var dependencies = DependencyVersions(arena, row);
            if ((version == entry.Versions[row]) && (dependencies == entry.Dependencies[row])) { continue; }
            var image = Image(view, row, audience);
            if (entry.FrameRows[row] && !image.Digest.AsSpan().SequenceEqual(entry.Images[row].Digest)) { return Compose(view, audience); }
            entry.Versions[row] = version; entry.Dependencies[row] = dependencies;
            entry.Images[row] = image;
            entry.Store(AudienceDefinition.ImageSlot(row), image.Digest);
            if (skeletons) {
                var observer = view.Observer;
                var skeleton = WorldStateDisclosure.DiscloseRow(arena, row, ref observer, entry.Fields, out _) with { Cells = null };
                entry.Rows[row] = skeleton;
                entry.Store(AudienceDefinition.SkeletonSlot(row), DigestOf(skeleton));
            }
        }
        entry.Revision = arena.DisclosureRevision;
        entry.Settle();
        return entry;
    }
    private AudienceDefinition Compose(WorldRecipientView view, string audience) {
        var source = view.Source;
        var arena = view.Arena;
        var count = source.AuthoredState.Count;
        var images = new WorldRecipientImage[count];
        var versions = new ulong[count];
        var dependencies = new ulong[count];
        for (var row = 0; row < count; row++) {
            versions[row] = arena.RowVersion(row);
            dependencies[row] = DependencyVersions(arena, row);
            images[row] = Image(view, row, audience);
        }
        var fields = WorldStateDisclosure.DisclosedFields.For(source, arena, view.Recipient, view.Unrestricted);
        var disclosed = WorldStateDisclosure.DiscloseDocument(source, arena, view.Recipient, view.Unrestricted, fields);
        var composed = disclosed.Definition;
        var rows = composed.AuthoredState.Select(row => row with { Cells = null }).ToArray();
        var digests = new byte[AudienceDefinition.ImageSlot(count)];
        var frame = (composed.StateRaw is { } state) ? composed with { StateRaw = state with { World = [] } } : composed;
        var frameBytes = WorldDefinitionSerialization.SerializeCompact(frame);
        m_work.Add(DefinitionBytes, frameBytes.Length);
        SHA256.HashData(frameBytes, digests.AsSpan(0, SHA256.HashSizeInBytes));
        for (var row = 0; row < count; row++) {
            images[row].Digest.CopyTo(digests.AsSpan(AudienceDefinition.ImageSlot(row), SHA256.HashSizeInBytes));
            DigestOf(rows[row]).CopyTo(digests.AsSpan(AudienceDefinition.SkeletonSlot(row), SHA256.HashSizeInBytes));
        }
        var frameRows = rows.Select(row => disclosed.PlacementReads.Contains(row.Name.Value)).ToArray();
        DefinitionsComposed++;
        m_work.Count(DefinitionCompositions);
        return m_definitions[audience] = new AudienceDefinition(source, composed, fields, frameRows, rows, images, versions, dependencies,
            arena.DisclosureRevision, digests);
    }
    private byte[] DigestOf(WorldStateRow skeleton) {
        var bytes = CanonicalJsonDocument.SerializeCompact(skeleton, WorldJsonContext.Default.WorldStateRow);
        m_work.Add(DefinitionBytes, bytes.Length);
        return SHA256.HashData(bytes);
    }
}
