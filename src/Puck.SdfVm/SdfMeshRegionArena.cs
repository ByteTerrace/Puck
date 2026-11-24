using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// The lasting placement of a presentation's mesh region (<see cref="SdfMeshRegionStore"/>): each distinct mesh lies
/// whole, its vertices, triangle materials and indices together, at the place it was given when a name first asked for
/// it, however many residencies' draws name it. A mesh keeps that place for as long as the arena keeps it, so a draw
/// list that adds a mesh owes only the new mesh's words, and one that drops a mesh leaves it where it lies: a list that
/// names it again, in the same residency or another, owes nothing for it.
/// <para>A mesh is named by a count: one for every draw row that names it (<see cref="Name"/>), in any residency, and
/// one for every residency that names it ahead of its draws (<see cref="NameAhead"/>). The arena never passes its limit,
/// and it gives a forgotten mesh's words to the next mesh they hold (<see cref="GpuRangeAllocator"/>, the lowest free run
/// first), so meshes replaced one at a time, as a group changing its detail, move no other mesh. The meshes no name holds
/// any more are forgotten together once their words pass the retained slack, or sooner when a named mesh finds no free
/// run (<see cref="Forgotten"/>). A named mesh that still finds none is refused (<see cref="Refused"/>), never placed
/// past the limit: its draws wait until a later plan finds it room.</para>
/// <para>A plan (<see cref="Plan"/>) compacts the arena, moving only the meshes it must, when a named mesh finds no free
/// run although the named meshes packed would fit the limit, and, where free words cost device memory
/// (<see cref="FreeWordsCost"/>), when the free words under the last placed mesh pass both <see cref="PackedShare"/>
/// times the named meshes' words and the retained slack (<see cref="Compact"/>): the highest placed meshes each take the
/// lowest free run below where they lie, at most <see cref="MostCompactionWords"/> a plan, until the meshes it refused
/// fit. A moved mesh keeps its words where they were until the region has copied them and every reader has taken the new
/// address (<see cref="MovedFrom"/>, <see cref="ReleaseMoveSources"/>). The plan states what it owes: the meshes it
/// placed (<see cref="Placed"/>) and those it moved (<see cref="Moved"/>).</para>
/// <para>A mesh named ahead is placed and kept like a mesh a draw names, so its words can land while the draws still
/// read the mesh it replaces. One no free run holds is refused ahead (<see cref="AheadRefused"/>), which refuses no
/// draw. A mesh can be named both ways, and holds at most one placement either way: a placed mesh is in neither refusal
/// set, whichever name placed it.</para>
/// <para>Not thread-safe: the store serializes every call under its gate.</para>
/// </summary>
public sealed class SdfMeshRegionArena : IDisposable {
    /// <summary>The multiple of the named meshes' words the free words under the last placed mesh may reach before a
    /// plan compacts an arena whose free words cost device memory, where they also pass the retained slack.</summary>
    public const int PackedShare = 3;
    /// <summary>The words outside the named meshes (meshes no name holds any more) the arena keeps before a plan forgets
    /// them: 32 MB, so a body crossing between worlds finds its meshes where it left them rather than writing them
    /// again.</summary>
    public const int RetainedSlackWords = (1 << 23);
    /// <summary>The most words one plan's compaction moves (<see cref="Compact"/>): 32 MB, a copy of a fraction of a
    /// millisecond on the GPU; a compaction needing more goes on at the next plan.</summary>
    public const int MostCompactionWords = (1 << 23);

    // Each placed mesh's first word.
    private readonly Dictionary<SdfMesh, int> m_placements = new(comparer: ReferenceEqualityComparer.Instance);
    // Each mesh a draw row names, with how many rows of every residency name it.
    private readonly Dictionary<SdfMesh, int> m_named = new(comparer: ReferenceEqualityComparer.Instance);
    // Each mesh named ahead, with how many residencies name it ahead and how many of those name it as a floor.
    private readonly Dictionary<SdfMesh, (int Names, int Floors)> m_ahead = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_placed = [];
    private readonly HashSet<SdfMesh> m_moved = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_forgotten = [];
    private readonly HashSet<SdfMesh> m_refused = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_aheadRefused = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_scratch = [];
    // Each mesh the latest plan moved, with the word it lay at until then.
    private readonly Dictionary<SdfMesh, int> m_movedFrom = new(comparer: ReferenceEqualityComparer.Instance);
    // The words moved meshes left, counted in their chunks and kept out of the free runs until the store releases them
    // (ReleaseMoveSources): a draw record staged before the move still reads them.
    private readonly List<(long Word, long Words)> m_moveSources = [];
    // The placed meshes from the highest down, listed afresh for each compaction.
    private readonly List<(int Word, SdfMesh Mesh)> m_compactOrder = [];

    private readonly int m_retainedSlackWords;
    // The region's words up to the limit: each placed mesh a range of its own, and each word range a move left.
    private readonly GpuRangeAllocator m_ranges;

    private readonly HashSet<SdfMesh> m_toPlace = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_unused = new(comparer: ReferenceEqualityComparer.Instance);

    private long m_placedWords;
    private long m_unusedWords;
    // Whether room may have opened for a refused mesh since the latest plan (a trim, a raised limit), and whether the
    // set named ahead changed, so the next plan tries them again.
    private bool m_trimmed;
    private bool m_aheadChanged;
    // One more for every placement given or taken back, and its value when a compaction last moved nothing.
    private long m_placementChanges;
    private long m_stuckAtChanges = -1L;
    private ulong? m_admissionBytes;

    /// <summary>Sets the diagnostic admission cap: no mesh is placed whose words end past it, and the placed meshes
    /// that do are taken back as a region that could not grow takes them (<see cref="Clip"/>). Null restores the
    /// limit.</summary>
    /// <param name="bytes">The cap in bytes, or null.</param>
    public void ForceLimit(ulong? bytes) {
        m_admissionBytes = bytes;
        if (bytes is { } cap) { TakeBackPast(words: ((long)(cap / sizeof(uint)))); } else { m_trimmed = true; }
    }

    // Per chunk of a mapped region (TrackChunks): how many placed meshes' words and move sources lie in it, and the
    // chunks whose count left or reached zero since the region last took them (TakeCrossedChunks). The invariant: each
    // count is the number of placements and move sources whose words touch the chunk. TryPlace, Forget, Compact and
    // ReleaseMoveSources are the only writers of the placements and the sources, and each counts what it changes here.
    private int[] m_chunkCounts = [];
    private long m_chunkWords;
    private readonly List<int> m_crossedChunks = [];
    // How many chunks hold a count above zero: the chunks a mapped region backs for the arena. CountChunks, the counts'
    // one writer, keeps it.
    private long m_coveredChunks;
    private readonly List<(long Words, SdfMesh Mesh)> m_shedOrder = [];

    /// <summary>Gets whether the counts were taken afresh (<see cref="TrackChunks"/>) since the region last took the
    /// crossed chunks, so every chunk's backing is to be fitted again rather than only the crossed ones.</summary>
    public bool ChunksRecounted { get; private set; }

    /// <summary>Counts, per chunk of a mapped region of <paramref name="chunkWords"/> words, the placed meshes that lie
    /// in it, keeping the counts as placements change. Counting again under another chunk size takes every count afresh
    /// (<see cref="ChunksRecounted"/>); zero stops counting.</summary>
    /// <param name="chunkWords">The words one chunk covers; zero for a region that is not mapped.</param>
    public void TrackChunks(long chunkWords) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: chunkWords);
        if (chunkWords == m_chunkWords) { return; }
        m_chunkWords = chunkWords;
        m_crossedChunks.Clear();
        m_coveredChunks = 0L;
        m_chunkCounts = ((chunkWords == 0L) ? [] : new int[(int)(((long)m_ranges.Size).CeilingDivide(divisor: chunkWords))]);
        if (chunkWords == 0L) { return; }
        foreach (var (mesh, word) in m_placements) { CountChunks(count: 1, word: word, words: SdfMeshRegion.PlacedWordsOf(mesh: mesh)); }
        foreach (var (word, words) in m_moveSources) { CountChunks(count: 1, word: word, words: words); }
        m_crossedChunks.Clear();
        ChunksRecounted = true;
    }
    /// <summary>Returns how many placed meshes lie in a chunk (<see cref="TrackChunks"/>).</summary>
    /// <param name="chunk">The chunk's index.</param>
    /// <returns>The count; zero for a chunk past the counted range or while nothing is counted.</returns>
    public int ChunkCount(int chunk) => (((uint)chunk < (uint)m_chunkCounts.Length) ? m_chunkCounts[chunk] : 0);
    /// <summary>Moves the chunks whose count left or reached zero since the last take into a list, a chunk that crossed
    /// more than once listed more than once, and clears <see cref="ChunksRecounted"/>.</summary>
    /// <param name="crossed">The list the chunks are appended to.</param>
    public void TakeCrossedChunks(List<int> crossed) {
        ArgumentNullException.ThrowIfNull(argument: crossed);
        crossed.AddRange(collection: m_crossedChunks);
        m_crossedChunks.Clear();
        ChunksRecounted = false;
    }
    private void CountChunks(long word, long words, int count) {
        if ((m_chunkWords == 0L) || (words <= 0L)) { return; }
        var last = (int)Math.Min(val1: (m_chunkCounts.Length - 1), val2: ((word + words) - 1L) / m_chunkWords);

        for (var chunk = (int)(word / m_chunkWords); (chunk <= last); chunk++) {
            var before = m_chunkCounts[chunk];

            m_chunkCounts[chunk] = (before + count);
            if ((before == 0) != (m_chunkCounts[chunk] == 0)) {
                m_crossedChunks.Add(item: chunk);
                m_coveredChunks += ((before == 0) ? 1L : -1L);
            }
        }
    }
    // How many chunks words [word, word + words) touch hold exactly a count: zero counts the chunks a placement there
    // would newly cover, and one the chunks a placement there alone keeps covered. Zero while nothing is counted.
    private long ChunksCounting(long word, long words, int count) {
        if ((m_chunkWords == 0L) || (words <= 0L)) { return 0L; }

        var last = (int)Math.Min(val1: (m_chunkCounts.Length - 1), val2: ((word + words) - 1L) / m_chunkWords);
        var chunks = 0L;

        for (var chunk = (int)(word / m_chunkWords); (chunk <= last); chunk++) {
            if (m_chunkCounts[chunk] == count) { chunks++; }
        }
        return chunks;
    }

    /// <summary>Initializes a new instance of the <see cref="SdfMeshRegionArena"/> class.</summary>
    /// <param name="retainedSlackWords">The words outside the named meshes the arena keeps before a plan forgets them;
    /// <see cref="RetainedSlackWords"/> for a presentation's store.</param>
    /// <param name="limitBytes">The most bytes the arena places at first, rounded down to whole words; at most
    /// <see cref="SdfMeshRegionStore.MostBytes"/>. <see cref="SetLimit"/> moves it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainedSlackWords"/> is negative, or
    /// <paramref name="limitBytes"/> is past its bound.</exception>
    public SdfMeshRegionArena(int retainedSlackWords = RetainedSlackWords, ulong limitBytes = SdfMeshRegionStore.MostBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: retainedSlackWords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(other: SdfMeshRegionStore.MostBytes, value: limitBytes);

        m_ranges = new GpuRangeAllocator(size: (SdfMeshRegionStore.MostBytes / sizeof(uint)));
        m_retainedSlackWords = retainedSlackWords;
        LimitWords = ((long)(limitBytes / sizeof(uint)));
    }

    /// <summary>Gets each placed mesh's first word, keyed by reference: every named mesh that is not refused, and the
    /// meshes earlier names held that the arena still keeps.</summary>
    public IReadOnlyDictionary<SdfMesh, int> Placements => m_placements;
    /// <summary>Gets the meshes named ahead that no free run under the limit holds, the same set object for the
    /// arena's lifetime: none has a placement, and a mesh a later plan places leaves it.</summary>
    public IReadOnlySet<SdfMesh> AheadRefused => m_aheadRefused;
    /// <summary>Gets how many meshes are named ahead.</summary>
    public int AheadCount => m_ahead.Count;

    /// <summary>Returns whether the arena keeps a mesh's words for a reader: a draw row names it, or a residency names
    /// it ahead. Any other placed mesh is retained slack a plan may forget.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named.</returns>
    public bool Names(SdfMesh mesh) => (m_named.ContainsKey(key: mesh) || m_ahead.ContainsKey(key: mesh));
    /// <summary>Returns whether a draw row of any residency names a mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when a draw names the mesh.</returns>
    public bool NamesDraw(SdfMesh mesh) => m_named.ContainsKey(key: mesh);
    /// <summary>Returns whether a mesh is named ahead by any residency.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named ahead.</returns>
    public bool NamesAhead(SdfMesh mesh) => m_ahead.ContainsKey(key: mesh);
    /// <summary>Lists the meshes named ahead into a list.</summary>
    /// <param name="meshes">The list the meshes are appended to.</param>
    public void ListAhead(List<SdfMesh> meshes) {
        ArgumentNullException.ThrowIfNull(argument: meshes);
        meshes.AddRange(collection: m_ahead.Keys);
    }

    /// <summary>Gets the words of the meshes draws name, each counted once, placed or refused.</summary>
    public long NamedWords { get; private set; }
    /// <summary>Gets the most words the arena places: no mesh is placed whose words end past it.</summary>
    public long LimitWords { get; private set; }

    /// <summary>Gets the words the region holds in device memory for the arena: for a mapped region
    /// (<see cref="TrackChunks"/>) the chunks a placed mesh or a moved mesh's old words lie in, and otherwise the words
    /// up to the last placed mesh (<see cref="Words"/>).</summary>
    public long HeldWords => ((m_chunkWords > 0L) ? (m_coveredChunks * m_chunkWords) : Words);
    /// <summary>Gets the words of every placed mesh, named by a draw, named ahead or kept as slack; what
    /// <see cref="HeldWords"/> holds past them is free words inside the chunks they lie in.</summary>
    public long PlacedWords => m_placedWords;
    /// <summary>Gets the most words the region may hold for the arena (<see cref="HeldWords"/>), as
    /// <see cref="SetLimit"/> last set it; <see cref="long.MaxValue"/> for none.</summary>
    public long HeldLimitWords { get; private set; } = long.MaxValue;
    /// <summary>Gets how many meshes plans shed under a lowered held limit, over the arena's life
    /// (<see cref="SetLimit"/>).</summary>
    public long Shed { get; private set; }

    /// <summary>Moves the limits, before a plan. A raised limit lets the next plan try the meshes the latest one refused.
    /// <para>Invariant: the storage the draws can do without never takes what the region holds
    /// (<see cref="HeldWords"/>) past <paramref name="heldBytes"/>. That storage is the meshes no name holds and the
    /// meshes named ahead only as products, which replace a drawn mesh and wait beside it (<see cref="NameAhead"/>). No
    /// such product is placed where it would pass the limit, which <c>TryPlace</c>, the one placing door, enforces, and
    /// a held limit lowered under what the region already holds takes that storage back at the next plan, the unnamed
    /// meshes first and then the products, largest first, which become refused ahead and take their draws without
    /// waiting beside the mesh they replace. A mesh a draw names and a floor named ahead are placed and kept whatever
    /// the held limit says, so it never takes a drawn mesh or its fallback out of a frame: the residencies' budgets
    /// coarsen those (<see cref="SdfResidencyGrant.Budget"/>), and until they have the region holds more than its limit.
    /// The address limit takes nothing back.</para></summary>
    /// <param name="limitBytes">The most bytes of address range the arena places meshes in from now on, rounded down to
    /// whole words and held to <see cref="SdfMeshRegionStore.MostBytes"/>.</param>
    /// <param name="heldBytes">The most bytes the region may hold for the arena; <see langword="null"/> for none.</param>
    public void SetLimit(ulong limitBytes, ulong? heldBytes = null) {
        var words = ((long)(Math.Min(val1: limitBytes, val2: SdfMeshRegionStore.MostBytes) / sizeof(uint)));
        var held = ((heldBytes is { } bytes) ? ((long)(Math.Min(val1: bytes, val2: SdfMeshRegionStore.MostBytes) / sizeof(uint))) : long.MaxValue);

        if (((words > LimitWords) || (held > HeldLimitWords)) && ((m_refused.Count > 0) || (m_aheadRefused.Count > 0))) { m_trimmed = true; }
        LimitWords = words;
        HeldLimitWords = held;
    }
    // Whether the region would hold no more than its held limit with words [start, start + words) placed.
    private bool FitsHeld(ulong start, ulong words) {
        if (HeldLimitWords == long.MaxValue) { return true; }

        return ((m_chunkWords > 0L)
            ? (((m_coveredChunks + ChunksCounting(count: 0, word: ((long)start), words: ((long)words))) * m_chunkWords) <= HeldLimitWords)
            : (((long)(start + words)) <= HeldLimitWords));
    }
    // Whether a mesh is storage the draws can do without: named ahead as a product alone, by no draw and as no floor.
    private bool IsProduct(SdfMesh mesh) => (m_ahead.TryGetValue(key: mesh, value: out var ahead) && (ahead.Floors == 0) && !m_named.ContainsKey(key: mesh));
    // Takes back what the draws can do without while the region holds more than its held limit, down to the limit less a
    // sixteenth so a product placed again at once does not pass it: the meshes no name holds, then the products named
    // ahead that no draw names, largest first.
    private void ShedOverLimit() {
        if (HeldWords <= HeldLimitWords) { return; }

        var forgotten = m_forgotten.Count;
        var target = (HeldLimitWords - (HeldLimitWords / 16L));

        _ = ForgetUnnamed();
        if (HeldWords > target) {
            m_shedOrder.Clear();
            foreach (var mesh in m_ahead.Keys) {
                if (m_placements.ContainsKey(key: mesh) && IsProduct(mesh: mesh)) { m_shedOrder.Add(item: (SdfMeshRegion.PlacedWordsOf(mesh: mesh), mesh)); }
            }
            m_shedOrder.Sort(comparison: static (left, right) => right.Words.CompareTo(value: left.Words));
            foreach (var (words, mesh) in m_shedOrder) {
                if (HeldWords <= target) { break; }
                // A product of a mapped region lying only in chunks other meshes keep covered gives nothing back.
                if ((m_chunkWords > 0L) && (ChunksCounting(count: 1, word: m_placements[mesh], words: words) == 0L)) { continue; }
                _ = Forget(mesh: mesh);
                _ = m_moved.Remove(item: mesh);
                m_forgotten.Add(item: mesh);
                _ = m_aheadRefused.Add(item: mesh);
            }
            m_shedOrder.Clear();
        }
        Shed += (m_forgotten.Count - forgotten);
    }
    /// <summary>Takes back every placement whose words end past a bound, for a region that could not grow to hold them:
    /// a mesh a draw names or one named ahead is refused, as if no free run had held it, and any other is forgotten
    /// (<see cref="Forgotten"/> gains it). The limit falls to the bound.</summary>
    /// <param name="limitBytes">The bytes the region holds.</param>
    public void Clip(ulong limitBytes) {
        var words = ((long)(limitBytes / sizeof(uint)));

        TakeBackPast(words: words);
        LimitWords = words;
    }
    private void TakeBackPast(long words) {
        m_scratch.Clear();
        foreach (var (mesh, word) in m_placements) {
            if ((word + Math.Max(val1: 1, val2: SdfMeshRegion.PlacedWordsOf(mesh: mesh))) > words) { m_scratch.Add(item: mesh); }
        }
        foreach (var mesh in m_scratch) {
            _ = Forget(mesh: mesh);
            _ = m_moved.Remove(item: mesh);
            m_forgotten.Add(item: mesh);
            if (m_named.ContainsKey(key: mesh)) {
                _ = m_refused.Add(item: mesh);
            } else if (m_ahead.ContainsKey(key: mesh)) {
                _ = m_aheadRefused.Add(item: mesh);
            }
        }
        // The clipped meshes leave the placed list in one pass: each has just lost its placement.
        if (m_scratch.Count > 0) { _ = m_placed.RemoveAll(match: mesh => !m_placements.ContainsKey(key: mesh)); }
        m_scratch.Clear();
    }

    /// <summary>Gets the region's words: the arena up to its last placed mesh or move source.</summary>
    public long Words => ((long)m_ranges.LiveEnd);
    /// <summary>Gets the meshes the latest plan placed, whose words the region owes.</summary>
    public IReadOnlyList<SdfMesh> Placed => m_placed;
    /// <summary>Gets the meshes the latest plan moved by a compaction: every reader read them at their old words until
    /// this plan (<see cref="MovedFrom"/>).</summary>
    public IReadOnlySet<SdfMesh> Moved => m_moved;
    /// <summary>Gets the meshes that lost their placement since the latest plan began: placed until then, and their words
    /// free for the next mesh they hold. A mesh a name still holds among them is in a refusal set.</summary>
    public IReadOnlyList<SdfMesh> Forgotten => m_forgotten;
    /// <summary>Gets the meshes draws name that no free run under the limit holds, the same set object for the arena's
    /// lifetime: they have no placement, and a later plan places each one it then has room for.</summary>
    public IReadOnlySet<SdfMesh> Refused => m_refused;
    /// <summary>Gets how many plans compacted the arena (<see cref="Compact"/>).</summary>
    public long Compactions { get; private set; }
    /// <summary>Gets how many meshes compactions moved, over the arena's life.</summary>
    public long CompactionMoves { get; private set; }
    /// <summary>Gets how many words compactions moved, over the arena's life.</summary>
    public long CompactionWords { get; private set; }
    /// <summary>Gets or sets whether the arena's free words below its last placed mesh cost device memory, as an unmapped
    /// region's do, so a plan compacts the arena once they pass the packed share; a mapped region gives a free chunk back
    /// wherever it lies, and never compacts for its free words alone. True until set.</summary>
    public bool FreeWordsCost { get; set; } = true;
    /// <summary>Gets whether words moved meshes left are still kept (<see cref="ReleaseMoveSources"/>): no plan
    /// compacts again until they are released.</summary>
    public bool HoldsMoveSources => (m_moveSources.Count > 0);
    /// <summary>Gets whether the latest plan placed, moved or forgot a mesh.</summary>
    public bool Changed { get; private set; }

    /// <summary>Returns the word a mesh the latest plan moved lay at until it (<see cref="Moved"/>): the words it was
    /// read from by every draw and copy recorded before the plan.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="word">The word it left.</param>
    /// <returns><see langword="false"/> when the latest plan did not move it.</returns>
    public bool MovedFrom(SdfMesh mesh, out int word) => m_movedFrom.TryGetValue(key: mesh, value: out word);
    /// <summary>Frees the words moved meshes left, once the region has recorded every copy that reads them and no
    /// reader's draw record still names them, so a later mesh may take them and a mapped region's next fit may give
    /// their chunks back.</summary>
    public void ReleaseMoveSources() {
        foreach (var (word, words) in m_moveSources) {
            CountChunks(count: -1, word: word, words: words);
            _ = m_ranges.Free(start: ((ulong)word));
        }
        m_moveSources.Clear();
    }

    /// <summary>Names a mesh for one more draw row.</summary>
    /// <param name="mesh">The mesh.</param>
    public void Name(SdfMesh mesh) {
        ref var names = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_named, exists: out var named, key: mesh);

        names++;
        if (named) { return; }
        NamedWords += SdfMeshRegion.PlacedWordsOf(mesh: mesh);
        RefreshUnused(mesh: mesh);
        if (!m_placements.ContainsKey(key: mesh)) { _ = m_toPlace.Add(item: mesh); }
    }
    /// <summary>Lets go of one draw row's name of a mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    public void Unname(SdfMesh mesh) {
        var left = (m_named[mesh] - 1);

        if (left != 0) { m_named[mesh] = left; return; }
        _ = m_named.Remove(key: mesh);
        _ = m_refused.Remove(item: mesh);
        NamedWords -= SdfMeshRegion.PlacedWordsOf(mesh: mesh);
        RefreshUnused(mesh: mesh);
    }
    /// <summary>Names a mesh ahead of the draws for one more residency: it is placed and kept like a mesh a draw names,
    /// so its words can land while the draws still read the mesh it replaces. A product replaces a drawn mesh and waits
    /// beside it: storage the draws can do without, which a held limit lowered under what the region holds takes back
    /// (<see cref="SetLimit"/>) unless a draw or a floor's name also holds it. A floor, which a fallen-back draw reads,
    /// keeps its words.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="product">Whether the residency names it as a product; otherwise as a floor.</param>
    public void NameAhead(SdfMesh mesh, bool product) {
        ref var ahead = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_ahead, exists: out _, key: mesh);

        ahead = ((ahead.Names + 1), (ahead.Floors + (product ? 0 : 1)));
        RefreshUnused(mesh: mesh);
        m_aheadChanged = true;
    }
    /// <summary>Lets go of one residency's name ahead of a mesh, given as it was taken.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="product">Whether the residency named it as a product.</param>
    public void UnnameAhead(SdfMesh mesh, bool product) {
        var ahead = m_ahead[mesh];

        if (ahead.Names == 1) {
            _ = m_ahead.Remove(key: mesh);
            _ = m_aheadRefused.Remove(item: mesh);
        } else {
            m_ahead[mesh] = ((ahead.Names - 1), (ahead.Floors - (product ? 0 : 1)));
        }
        RefreshUnused(mesh: mesh);
        m_aheadChanged = true;
    }

    /// <summary>Places what the names ask: a mesh already placed keeps its words, and a newly named one takes the lowest
    /// free run that holds it; the meshes no name holds are forgotten once they pass the retained slack or a named mesh
    /// needs their words; a held limit the region passes takes back what the draws can do without; and the arena
    /// compacts where that makes room (<see cref="Compact"/>). The results stand until the next plan.</summary>
    public void Plan() {
        var changes = m_placementChanges;

        m_placed.Clear(); m_moved.Clear(); m_movedFrom.Clear(); m_forgotten.Clear();
        if (m_unusedWords > m_retainedSlackWords) { _ = ForgetUnnamed(); }
        ShedOverLimit();

        var placeAhead = (m_trimmed || m_aheadChanged);

        if (m_trimmed) { foreach (var mesh in m_refused) { _ = m_toPlace.Add(item: mesh); } m_trimmed = false; }
        m_aheadChanged = false;
        foreach (var mesh in m_toPlace) {
            if (m_placements.ContainsKey(key: mesh) || !m_named.ContainsKey(key: mesh)) { continue; }
            if (TryPlace(mesh: mesh)) { continue; }
            _ = ForgetUnnamed();
            if (!TryPlace(mesh: mesh)) { _ = m_refused.Add(item: mesh); }
        }
        m_toPlace.Clear();
        if (placeAhead) { PlaceAhead(); }

        var packedFits = ((m_refused.Count > 0) && (NamedWords <= LimitWords) && (m_placementChanges != changes));

        // Compaction waits for the copies of the moves before it, whose words a new move must not take.
        // One that moved nothing is not tried again until a placement changes.
        if (!HoldsMoveSources && (m_placementChanges != m_stuckAtChanges) && (packedFits || (FreeWordsCost && Sparse())) && !Compact(fitRefused: packedFits)) {
            m_stuckAtChanges = m_placementChanges;
        }
        Changed = (m_placementChanges != changes);
    }

    // Whether the free words under the last placed mesh pass both the packed share of the named meshes' words and the
    // retained slack.
    private bool Sparse() {
        var free = (Words - m_placedWords);

        return ((free > (PackedShare * NamedWords)) && (free > m_retainedSlackWords));
    }
    /// <summary>Compacts the arena by moving the fewest meshes it can: from the highest placed mesh down, each takes the
    /// lowest free run that ends at or below where it begins, until the meshes the plan refused fit (with
    /// <paramref name="fitRefused"/>) or the arena is no longer sparse, at most <see cref="MostCompactionWords"/> words and
    /// one dispatch's moves (<see cref="GpuRegion.MaxMoves"/>) a plan. A mesh no lower run holds stays where it lies.
    /// <para>Invariant: no move's new words overlap the words any move of the same plan, or of a plan whose sources the
    /// store has not released, left: a move source stays allocated until <see cref="ReleaseMoveSources"/>, and
    /// <see cref="HoldsMoveSources"/> keeps a plan from compacting until then, so the region's moves read no word another
    /// of them writes and no mesh lands in words a reader may still read the moved mesh from.</para></summary>
    /// <param name="fitRefused">Whether the compaction makes room for the refused named meshes, which a later plan
    /// places once the sources are released; otherwise it lowers the last placed mesh while the arena is sparse.</param>
    /// <returns>Whether it moved a mesh.</returns>
    private bool Compact(bool fitRefused) {
        var need = 0L;

        if (fitRefused) { foreach (var mesh in m_refused) { need += Math.Max(val1: 1, val2: SdfMeshRegion.PlacedWordsOf(mesh: mesh)); } }
        m_compactOrder.Clear();
        foreach (var (mesh, word) in m_placements) { m_compactOrder.Add(item: (word, mesh)); }
        m_compactOrder.Sort(comparison: static (left, right) => right.Word.CompareTo(value: left.Word));

        var movedWords = 0L;
        var moves = 0;
        // The highest word a placement the compaction left standing or gave a lower run ends at.
        var highest = 0L;

        for (var index = 0; (index < m_compactOrder.Count); index++) {
            var (word, mesh) = m_compactOrder[index];
            var words = Math.Max(val1: 1, val2: SdfMeshRegion.PlacedWordsOf(mesh: mesh));
            var below = (((index + 1) < m_compactOrder.Count) ? (((long)m_compactOrder[index + 1].Word) + Math.Max(val1: 1, val2: SdfMeshRegion.PlacedWordsOf(mesh: m_compactOrder[index + 1].Mesh))) : 0L);
            var end = Math.Max(val1: highest, val2: below);
            // The words under the highest placement left standing, were this mesh to stay where it lies.
            var standing = Math.Max(val1: end, val2: (((long)word) + words));
            var done = (fitRefused
                ? ((LimitWords - standing) >= need)
                : ((standing - m_placedWords) <= Math.Max(val1: (PackedShare * NamedWords), val2: m_retainedSlackWords)));

            if (done || (moves >= (GpuRegion.MaxMoves - 1)) || ((movedWords + words) > MostCompactionWords)) { break; }
            if (!m_ranges.TryAllocate(count: ((ulong)words), start: out var start)) {
                highest = Math.Max(val1: highest, val2: (word + words));
                continue;
            }
            if ((((long)start) + words) > word) {
                _ = m_ranges.Free(start: start);
                highest = Math.Max(val1: highest, val2: (word + words));
                continue;
            }

            m_movedFrom[mesh] = word;
            _ = m_moved.Add(item: mesh);
            m_placements[mesh] = ((int)start);
            CountChunks(count: 1, word: ((long)start), words: words);
            // The words the mesh left stay allocated and counted as a source until the store releases them.
            m_moveSources.Add(item: (word, words));
            m_placementChanges++;
            movedWords += words;
            moves++;
            highest = Math.Max(val1: highest, val2: (((long)start) + words));
        }
        if (moves == 0) { return false; }
        Compactions++;
        CompactionMoves += moves;
        CompactionWords += movedWords;
        // A refused mesh takes the room once the sources are released.
        if (fitRefused) { m_trimmed = true; }

        return true;
    }
    private void RefreshUnused(SdfMesh mesh) {
        if (m_placements.ContainsKey(key: mesh) && !Names(mesh: mesh)) {
            if (m_unused.Add(item: mesh)) { m_unusedWords += SdfMeshRegion.PlacedWordsOf(mesh: mesh); }
        } else if (m_unused.Remove(item: mesh)) { m_unusedWords -= SdfMeshRegion.PlacedWordsOf(mesh: mesh); }
    }

    // Places each mesh named ahead that holds no words.
    private void PlaceAhead() {
        m_aheadRefused.Clear();
        foreach (var mesh in m_ahead.Keys) {
            // A product no draw names yet is storage the draws can do without: the held limit refuses it.
            var product = IsProduct(mesh: mesh);

            if (m_placements.ContainsKey(key: mesh) || TryPlace(mesh: mesh, product: product)) { continue; }
            // The set names meshes without claiming them, so a mesh its owner has since released is not placed.
            if (mesh.IsReleased) { continue; }
            _ = ForgetUnnamed();
            if (!TryPlace(mesh: mesh, product: product)) { _ = m_aheadRefused.Add(item: mesh); }
        }
    }
    // Places a mesh at the lowest free run that holds it under a placement claim of its own; false when no free run
    // under the limit holds it, or when the mesh's storage was already released.
    // The one door that gives a placement, so it keeps the invariant: a mesh holds at most one placement, and a mesh
    // that holds one is in neither refusal set (Refused, AheadRefused). A placed mesh leaves both here, whichever name
    // placed it, and a refusal is recorded only after a TryPlace that failed or a Forget.
    private bool TryPlace(SdfMesh mesh, bool product = false) {
        // A range is at least one word, so a mesh without words still has a place of its own.
        var words = ((ulong)Math.Max(val1: 1, val2: SdfMeshRegion.PlacedWordsOf(mesh: mesh)));

        if (!m_ranges.TryAllocate(count: words, start: out var start)) { return false; }
        // The grant's address and held limits and the diagnostic cap constrain the same lowest-run admission.
        if (((start + words) > ((ulong)LimitWords)) || (product && !FitsHeld(start: start, words: words)) ||
            ((m_admissionBytes is { } cap) && ((start + words) > (cap / sizeof(uint))))) {
            _ = m_ranges.Free(start: start);
            return false;
        }
        // The claim comes before the placement is recorded, so every recorded placement holds exactly one claim that
        // Forget and Dispose release.
        if (!mesh.TryAcquire(cached: true)) {
            _ = m_ranges.Free(start: start);
            return false;
        }

        m_placements.Add(key: mesh, value: ((int)start));
        _ = m_refused.Remove(item: mesh);
        _ = m_aheadRefused.Remove(item: mesh);
        m_placed.Add(item: mesh);
        m_placementChanges++;
        m_placedWords += SdfMeshRegion.PlacedWordsOf(mesh: mesh);
        CountChunks(count: 1, word: ((long)start), words: SdfMeshRegion.PlacedWordsOf(mesh: mesh));
        RefreshUnused(mesh: mesh);

        return true;
    }
    // Frees a placed mesh's words and lets its placement claim go; its words.
    private long Forget(SdfMesh mesh) {
        _ = m_placements.Remove(key: mesh, value: out var word);
        m_placementChanges++;
        m_placedWords -= SdfMeshRegion.PlacedWordsOf(mesh: mesh);
        CountChunks(count: -1, word: word, words: SdfMeshRegion.PlacedWordsOf(mesh: mesh));
        RefreshUnused(mesh: mesh);
        mesh.Release(cached: true);

        return ((long)m_ranges.Free(start: ((ulong)word)));
    }
    // Forgets every placed mesh no name holds; their words.
    private long ForgetUnnamed() {
        var words = 0L;

        m_scratch.Clear();
        m_scratch.AddRange(collection: m_unused);
        foreach (var mesh in m_scratch) {
            words += Forget(mesh: mesh);
            m_forgotten.Add(item: mesh);
        }
        m_scratch.Clear();

        return words;
    }

    /// <summary>Releases the arena's CPU source claims after all uploads and readers finish.</summary>
    public void Dispose() {
        foreach (var mesh in m_placements.Keys) { mesh.Release(cached: true); }
        m_placements.Clear();
    }
    /// <summary>Forgets the placed meshes no name holds, freeing their words and CPU source claims and keeping every
    /// named mesh's address standing; <see cref="Forgotten"/> gains them.</summary>
    /// <returns>Whether a mesh was forgotten.</returns>
    public bool TrimUnused() {
        var before = m_forgotten.Count;

        _ = ForgetUnnamed();
        m_trimmed |= (m_forgotten.Count > before);

        return (m_forgotten.Count > before);
    }
}
