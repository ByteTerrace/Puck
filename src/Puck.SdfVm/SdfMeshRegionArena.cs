using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// A residency's lasting placement of its mesh region (<see cref="SdfMeshRegion"/>): a reserved run of draw records, then
/// an arena where each distinct mesh lies whole, its vertices, triangle materials and indices together, at the place it
/// was given when a draw list first named it. A mesh keeps that place for as long as the arena keeps it, so a list that
/// adds a mesh owes only the draw records and the new mesh's words, and a list that drops one leaves the mesh where it
/// lies: a list that names it again, as when a body that left the world returns, owes nothing for it.
/// <para>The arena never passes its limit, and it gives a forgotten mesh's words to the next mesh they hold
/// (<see cref="GpuRangeAllocator"/>, the lowest free run first), so a list whose meshes are replaced one at a time, as a
/// group changing its detail, moves no other mesh. The meshes no list names any more are forgotten together once their
/// words pass the retained slack, or sooner when a named mesh finds no free run (<see cref="Forgotten"/>). A named mesh
/// that still finds none is refused (<see cref="Refused"/>), never placed past the limit: its draws wait until a later
/// plan finds it room.</para>
/// <para>A list holding more draws than the reserve doubles it, and only the meshes lying in the words the reserve takes
/// move: each one the list names is placed again (<see cref="Moved"/>), and the rest are forgotten. A body straddling a
/// door draws twice, so a list can outgrow the reserve wherever a body stands; growing it once moves a mesh or two,
/// never every mesh the region holds.</para>
/// <para>A plan compacts the arena, moving only the meshes it must, when a named mesh finds no free run although the
/// named meshes packed would fit the limit, and, where free words cost device memory (<see cref="FreeWordsCost"/>), when
/// the free words under the last placed mesh pass both <see cref="PackedShare"/> times the named meshes' words and the
/// retained slack (<see cref="Compact"/>): the highest placed meshes each take the lowest free run below where they lie,
/// at most <see cref="MostCompactionWords"/> a plan, until the meshes it refused fit. A moved mesh keeps its words where
/// they were until the region has copied them (<see cref="MovedFrom"/>, <see cref="ReleaseMoveSources"/>). A plan relocates
/// every mesh a list names, packing the arena from the reserve's end in the list's order and forgetting every other mesh,
/// only on the first plan, when forced, or when a compaction can move no mesh at all although the named meshes packed
/// would fit. The plan states what it owes: <see cref="Relocated"/>, or the meshes it placed (<see cref="Placed"/>) and
/// those it moved (<see cref="Moved"/>), which held words a draw read until this plan.</para>
/// <para>A mesh the draws are about to name is named ahead of them (<see cref="PlanAhead"/>): it is placed and kept
/// like a mesh a draw names, so its words can land while the draws still read the mesh it replaces, and a list that
/// then names it finds it where it lies. One no free run holds is refused ahead (<see cref="AheadRefused"/>), which
/// refuses no draw and never relocates the arena.</para>
/// </summary>
public sealed class SdfMeshRegionArena : IDisposable {
    /// <summary>The fewest draw records the region reserves.</summary>
    public const int MinimumDrawCapacity = 256;
    /// <summary>The multiple of the named meshes' words the free words under the last placed mesh may reach before a
    /// plan compacts an arena whose free words cost device memory, where they also pass the retained slack.</summary>
    public const int PackedShare = 3;
    /// <summary>The words outside the named meshes (meshes no list names any more) the arena keeps before a plan forgets
    /// them: 32 MB, so a body crossing between worlds finds its meshes where it left them rather than writing them
    /// again.</summary>
    public const int RetainedSlackWords = (1 << 23);
    /// <summary>The most words one plan's compaction moves (<see cref="Compact"/>): 32 MB, a copy of a fraction of a
    /// millisecond on the GPU; a compaction needing more goes on at the next plan.</summary>
    public const int MostCompactionWords = (1 << 23);

    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_placements = new(comparer: ReferenceEqualityComparer.Instance);
    // Each mesh the latest list's draws name, with how many of its rows name it.
    private readonly Dictionary<SdfMesh, int> m_named = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_placed = [];
    private readonly HashSet<SdfMesh> m_moved = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_forgotten = [];
    private readonly HashSet<SdfMesh> m_refused = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_ahead = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_aheadRefused = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_scratch = [];
    // Each mesh the latest plan moved, with the placement it held until then.
    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_movedFrom = new(comparer: ReferenceEqualityComparer.Instance);
    // The words moved meshes left, counted in their chunks until the region has copied them (ReleaseMoveSources), so a
    // mapped region keeps them backed for the copy that reads them.
    private readonly List<(long Word, long Words)> m_moveSources = [];
    // The placed meshes from the highest down, listed afresh for each compaction, and the ranges it vacated.
    private readonly List<(int Word, SdfMesh Mesh)> m_compactOrder = [];
    private readonly List<ulong> m_vacated = [];

    private readonly int m_retainedSlackWords;
    // The region's words up to the limit: the reserve is the range at zero, and each placed mesh a range of its own.
    private readonly GpuRangeAllocator m_ranges;

    private readonly HashSet<SdfMesh> m_toPlace = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_unused = new(comparer: ReferenceEqualityComparer.Instance);

    private long m_placedWords;
    private long m_unusedWords;
    private bool m_firstRows;
    private int m_namesChanged;
    private int m_drawCapacity;
    // Whether a trim forgot a mesh since the latest full plan, so a list naming a refused mesh is planned again.
    private bool m_trimmed;
    private bool m_forceRelocation;
    // One more for every placement given or taken back, and its value when a compaction last moved nothing.
    private long m_placementChanges;
    private long m_stuckAtChanges = -1L;
    private ulong? m_admissionBytes;

    /// <summary>Sets the diagnostic admission cap and requests a relocation so already placed meshes pass through
    /// the same placing function. Null restores the device limit.</summary>
    /// <param name="bytes">The cap in bytes, or null.</param>
    public void ForceLimit(ulong? bytes) { m_admissionBytes = bytes; ForceRelocation(); }
    /// <summary>Requests that the next plan pack its meshes regardless of the normal slack threshold.</summary>
    public void ForceRelocation() => m_forceRelocation = true;

    // Per chunk of a mapped region (TrackChunks): how many placed meshes' words and how much of the draw reserve lie in
    // it, and the chunks whose count left or reached zero since the region last took them (TakeCrossedChunks). The
    // invariant: each count is the number of placements plus the reserve whose words touch the chunk. TryPlace, Forget
    // and GrowReserve are the only writers of the placements and the reserve, and each counts what it changes here.
    private int[] m_chunkCounts = [];
    private long m_chunkWords;
    private readonly List<int> m_crossedChunks = [];
    private long m_countedReserveWords;

    /// <summary>Gets whether the counts were taken afresh (<see cref="TrackChunks"/>) since the region last took the
    /// crossed chunks, so every chunk's backing is to be fitted again rather than only the crossed ones.</summary>
    public bool ChunksRecounted { get; private set; }

    /// <summary>Counts, per chunk of a mapped region of <paramref name="chunkWords"/> words, the placed meshes and draw
    /// records that lie in it, keeping the counts as placements change. Counting again under another chunk size takes
    /// every count afresh (<see cref="ChunksRecounted"/>); zero stops counting.</summary>
    /// <param name="chunkWords">The words one chunk covers; zero for a region that is not mapped.</param>
    public void TrackChunks(long chunkWords) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: chunkWords);
        if (chunkWords == m_chunkWords) { return; }
        m_chunkWords = chunkWords;
        m_crossedChunks.Clear();
        m_countedReserveWords = 0L;
        m_chunkCounts = ((chunkWords == 0L) ? [] : new int[(int)(((long)m_ranges.Size).CeilingDivide(divisor: chunkWords))]);
        if (chunkWords == 0L) { return; }
        CountReserve();
        foreach (var (mesh, placement) in m_placements) { CountChunks(count: 1, word: placement.VertexWord, words: SdfMeshRegion.WordsOf(mesh: mesh)); }
        m_crossedChunks.Clear();
        ChunksRecounted = true;
    }
    /// <summary>Returns how many placed meshes and draw-record runs lie in a chunk (<see cref="TrackChunks"/>).</summary>
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
            if ((before == 0) != (m_chunkCounts[chunk] == 0)) { m_crossedChunks.Add(item: chunk); }
        }
    }
    // The reserve's draw records are counted as one run from word zero, moved whenever the reserve grows.
    private void CountReserve() {
        var reserve = (((long)m_drawCapacity) * SdfMeshRecord.DrawWords);

        if (reserve == m_countedReserveWords) { return; }
        CountChunks(count: -1, word: 0L, words: m_countedReserveWords);
        CountChunks(count: 1, word: 0L, words: reserve);
        m_countedReserveWords = reserve;
    }

    // The meshes the latest full plan's list named, in its order.
    private SdfMesh?[] m_latestMeshes = [];

    private int m_latestMeshCount;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshRegionArena"/> class.</summary>
    /// <param name="retainedSlackWords">The words outside the named meshes the arena keeps before a plan forgets them;
    /// <see cref="RetainedSlackWords"/> for a residency's tables.</param>
    /// <param name="limitBytes">The most bytes the arena places at first, the reserve included, rounded down to whole
    /// words; at least <see cref="MinimumDrawCapacity"/> draw records and at most
    /// <see cref="SdfWorldTables.MostMeshRegionBytes"/>. <see cref="SetLimit"/> moves it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainedSlackWords"/> is negative, or
    /// <paramref name="limitBytes"/> is outside its bounds.</exception>
    public SdfMeshRegionArena(int retainedSlackWords = RetainedSlackWords, ulong limitBytes = SdfWorldTables.MostMeshRegionBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: retainedSlackWords);
        ArgumentOutOfRangeException.ThrowIfLessThan(other: ((ulong)(MinimumDrawCapacity * SdfMeshRegion.DrawBytes)), value: limitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(other: SdfWorldTables.MostMeshRegionBytes, value: limitBytes);

        m_ranges = new GpuRangeAllocator(size: (SdfWorldTables.MostMeshRegionBytes / sizeof(uint)));
        m_retainedSlackWords = retainedSlackWords;
        LimitWords = ((long)(limitBytes / sizeof(uint)));
    }

    /// <summary>Gets each placed mesh's words, keyed by reference: every mesh the latest list names that is not
    /// <see cref="Refused"/>, and the meshes earlier lists named that the arena still keeps.</summary>
    public Dictionary<SdfMesh, SdfMeshRegionMesh> Placements => m_placements;
    /// <summary>Gets the meshes named ahead of the draws (<see cref="PlanAhead"/>), the same set object for the arena's
    /// lifetime.</summary>
    public IReadOnlySet<SdfMesh> Ahead => m_ahead;
    /// <summary>Gets the meshes named ahead that no free run under the limit holds, the same set object for the
    /// arena's lifetime: none has a placement unless a draw list placed it.</summary>
    public IReadOnlySet<SdfMesh> AheadRefused => m_aheadRefused;

    /// <summary>Returns whether the arena keeps a mesh's words for a reader: a draw of the latest list names it, or
    /// it is named ahead. Any other placed mesh is retained slack a plan may forget.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named.</returns>
    public bool Names(SdfMesh mesh) => (m_named.ContainsKey(key: mesh) || m_ahead.Contains(item: mesh));

    /// <summary>Gets the words of the meshes the latest list names, each counted once, placed or refused.</summary>
    public long NamedWords { get; private set; }
    /// <summary>Gets the draw records the region reserves ahead of the arena.</summary>
    public int DrawCapacity => m_drawCapacity;
    /// <summary>Gets the most words the arena places, the reserve included: no mesh is placed whose words end past it.
    /// The reserved draw records are held whatever it says.</summary>
    public long LimitWords { get; private set; }

    /// <summary>Moves the limit, before a plan. A mesh placed under an earlier, higher limit keeps its words: a lowered
    /// limit stops placements past it and takes nothing back. A raised limit lets the next plan try the meshes the
    /// latest one refused.</summary>
    /// <param name="limitBytes">The most bytes the arena places from now on, rounded down to whole words and held to
    /// <see cref="SdfWorldTables.MostMeshRegionBytes"/>.</param>
    public void SetLimit(ulong limitBytes) {
        var words = ((long)(Math.Min(val1: limitBytes, val2: SdfWorldTables.MostMeshRegionBytes) / sizeof(uint)));

        if ((words > LimitWords) && ((m_refused.Count > 0) || (m_aheadRefused.Count > 0))) { m_trimmed = true; }
        LimitWords = words;
    }
    /// <summary>Takes back every placement whose words end past a bound, for a region that could not grow to hold them:
    /// a mesh the latest list names or one named ahead is refused, as if no free run had held it, and any other is
    /// forgotten (<see cref="Forgotten"/> gains it). The limit falls to the bound, and never under the reserved draw
    /// records.</summary>
    /// <param name="limitBytes">The bytes the region holds.</param>
    public void Clip(ulong limitBytes) {
        var words = Math.Max(val1: ((long)(limitBytes / sizeof(uint))), val2: (((long)m_drawCapacity) * SdfMeshRecord.DrawWords));

        m_scratch.Clear();
        foreach (var (mesh, placement) in m_placements) {
            if ((placement.VertexWord + Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: mesh))) > words) { m_scratch.Add(item: mesh); }
        }
        foreach (var mesh in m_scratch) {
            _ = Forget(mesh: mesh);
            if (m_named.ContainsKey(key: mesh)) {
                _ = m_moved.Add(item: mesh);
                _ = m_refused.Add(item: mesh);
            } else if (m_ahead.Contains(item: mesh)) {
                _ = m_moved.Remove(item: mesh);
                _ = m_aheadRefused.Add(item: mesh);
            } else {
                m_forgotten.Add(item: mesh);
            }
        }
        // The clipped meshes leave the placed list in one pass: each has just lost its placement.
        if (m_scratch.Count > 0) { _ = m_placed.RemoveAll(match: mesh => !m_placements.ContainsKey(key: mesh)); }
        m_scratch.Clear();
        LimitWords = words;
    }

    /// <summary>Gets the region's words: the reserved draw records and the arena up to its last placed mesh.</summary>
    public int Words => checked((int)m_ranges.LiveEnd);
    /// <summary>Gets the meshes the latest plan placed, whose words it owes; every placed mesh when it
    /// <see cref="Relocated"/>.</summary>
    public IReadOnlyList<SdfMesh> Placed => m_placed;
    /// <summary>Gets the meshes the latest plan moved, out of the words a grown reserve took or by a compaction: the
    /// region drew them from their old words until this plan (<see cref="MovedFrom"/>), so it copies or writes each one
    /// placed again before the draws read it rather than letting it land over later frames. One no free run held is
    /// <see cref="Refused"/> instead of placed.</summary>
    public IReadOnlySet<SdfMesh> Moved => m_moved;
    /// <summary>Gets the meshes the latest plan forgot without relocating: placed until this plan, named by no list, and
    /// their words free for the next mesh they hold. Empty when the plan <see cref="Relocated"/>, which forgets every
    /// mesh its list does not name.</summary>
    public IReadOnlyList<SdfMesh> Forgotten => m_forgotten;
    /// <summary>Gets the meshes the latest list names that no free run under the limit holds, the same set object for
    /// the arena's lifetime: they have no placement, and a plan of a later list places each one it then has room
    /// for.</summary>
    public IReadOnlySet<SdfMesh> Refused => m_refused;
    /// <summary>Gets whether the latest plan moved every mesh, so the region owes every word.</summary>
    public bool Relocated { get; private set; }
    /// <summary>Gets how many plans relocated every mesh, the first plan included.</summary>
    public long Relocations { get; private set; }
    /// <summary>Gets why the latest relocating plan relocated, or <see langword="null"/> before the first.</summary>
    public string? RelocationReason { get; private set; }
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
    /// <summary>Gets whether words moved meshes left are still counted as held (<see cref="ReleaseMoveSources"/>): no plan
    /// compacts again until they are released.</summary>
    public bool HoldsMoveSources => (m_moveSources.Count > 0);

    /// <summary>Returns the placement a mesh the latest plan moved held until it (<see cref="Moved"/>): the words it was
    /// read from by every draw and copy recorded before the plan.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="placement">The placement it left.</param>
    /// <returns><see langword="false"/> when the latest plan did not move it.</returns>
    public bool MovedFrom(SdfMesh mesh, out SdfMeshRegionMesh placement) => m_movedFrom.TryGetValue(key: mesh, value: out placement);
    /// <summary>Stops counting the words moved meshes left as held, once the region has recorded and submitted every copy
    /// that reads them, so a mapped region's next fit may give their chunks back.</summary>
    public void ReleaseMoveSources() {
        foreach (var (word, words) in m_moveSources) { CountChunks(count: -1, word: word, words: words); }
        m_moveSources.Clear();
    }
    /// <summary>Gets whether the latest plan's list named the meshes the plan before it named, in the same order, so every
    /// placement and the reserve stand as they were and only the draws' own values can have moved.</summary>
    public bool Unchanged { get; private set; }

    /// <summary>Places the meshes a list of draws names: a mesh already placed keeps its words, and a new one takes the
    /// lowest free run that holds it, unless the plan relocates every mesh.</summary>
    /// <param name="draws">The draws.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The list holds more than <see cref="SdfMeshRegion.MaxDraws"/> draws.</exception>
    /// <exception cref="InvalidOperationException">The list's draw records alone pass the arena's limit.</exception>
    public void Plan(IReadOnlyList<SdfMeshDraw> draws) {
        ArgumentNullException.ThrowIfNull(draws);
        BeginRows(extent: draws.Count);
        for (var row = 0; (row < draws.Count); row++) { NameRow(index: row, mesh: draws[row].Mesh); }
        EndRows();
    }

    // Row transactions collect all unnames before placement, so their released slack is available to every new name.
    internal void BeginRows(int extent) {
        m_namesChanged = 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(extent, SdfMeshRegion.MaxDraws);
        m_placed.Clear(); m_moved.Clear(); m_movedFrom.Clear(); m_forgotten.Clear(); m_toPlace.Clear();
        Relocated = false; Unchanged = true;
        m_firstRows = ((m_drawCapacity == 0) && (extent > 0));
        ArrayCapacity.EnsureLength(ref m_latestMeshes, extent);
        for (var row = extent; (row < m_latestMeshCount); row++) { NameRow(index: row, mesh: null); }
        m_latestMeshCount = extent;
    }
    internal void NameRow(int index, SdfMesh? mesh) {
        var old = m_latestMeshes[index];

        if (ReferenceEquals(objA: old, objB: mesh)) { return; }
        Unchanged = false;
        m_latestMeshes[index] = mesh;
        if (old is not null) {
            var left = (m_named[old] - 1);

            if (left == 0) { m_named.Remove(key: old); m_refused.Remove(item: old); NamedWords -= SdfMeshRegion.WordsOf(mesh: old); } else { m_named[old] = left; }
            RefreshUnused(mesh: old);
        }
        if (mesh is null) { return; }
        SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.StagingPlanRecords);
        m_namesChanged++;
        ref var names = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_named, exists: out var named, key: mesh);

        names++;
        if (!named) { NamedWords += SdfMeshRegion.WordsOf(mesh: mesh); }
        RefreshUnused(mesh: mesh);
        if (!m_placements.ContainsKey(key: mesh)) { m_toPlace.Add(item: mesh); }
    }
    internal void EndRows() {
        SdfMeshHistory.ProcessWork.Raise(kind: SdfMeshHistory.PeakRowsPlanned, value: m_namesChanged);
        if (m_latestMeshCount > m_drawCapacity) {
            GrowReserve(drawCount: m_latestMeshCount);
            foreach (var mesh in m_moved) { m_toPlace.Add(item: mesh); }
            Unchanged = false;
        }
        if (m_unusedWords > m_retainedSlackWords) { _ = ForgetUnnamed(); Unchanged = false; }
        if (m_trimmed) { foreach (var mesh in m_refused) { m_toPlace.Add(item: mesh); } m_trimmed = false; }
        foreach (var mesh in m_toPlace) {
            if (!m_named.ContainsKey(key: mesh) || m_placements.ContainsKey(key: mesh)) { continue; }
            Unchanged = false;
            if (TryPlace(mesh: mesh)) { m_refused.Remove(item: mesh); continue; }
            _ = ForgetUnnamed();
            if (TryPlace(mesh: mesh)) { m_refused.Remove(item: mesh); } else { m_refused.Add(item: mesh); }
        }
        var reserveWords = (((long)m_drawCapacity) * SdfMeshRecord.DrawWords);
        var packedFits = ((m_refused.Count > 0) && ((reserveWords + NamedWords) <= LimitWords) && !Unchanged);
        var relocate = m_forceRelocation;

        // Compaction waits for the copies of the moves before it, whose words a new move must not take.
        // One that moved nothing is not tried again until a placement changes.
        if (!relocate && !m_firstRows && !HoldsMoveSources && (m_placementChanges != m_stuckAtChanges) &&
            (packedFits || (FreeWordsCost && Sparse(reserveWords: reserveWords)))) {
            if (Compact(fitRefused: packedFits)) {
                Unchanged = false;
            } else {
                m_stuckAtChanges = m_placementChanges;
                // A compaction that can move nothing while a named mesh still waits for room packs the arena.
                relocate = (packedFits && (m_refused.Count > 0));
            }
        }
        if (relocate) {
            RelocationReason = (m_forceRelocation ? "forced relocation" : "compaction stuck");
            m_forceRelocation = false;
            Pack();
        } else if (m_firstRows) { RelocationReason = "first plan"; }
        Relocated = (relocate || m_firstRows);
        if (Relocated) { Relocations++; Unchanged = false; }
    }

    /// <summary>Changes one row's mesh name using the same operation as a whole plan.</summary>
    public void Rename(int index, SdfMesh mesh) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, m_latestMeshCount);
        NameRow(index: index, mesh: mesh);
    }

    // Whether the free words under the last placed mesh pass both the packed share of the named meshes' words and the
    // retained slack.
    private bool Sparse(long reserveWords) {
        var free = ((Words - reserveWords) - m_placedWords);

        return ((free > (PackedShare * NamedWords)) && (free > m_retainedSlackWords));
    }
    /// <summary>Compacts the arena by moving the fewest meshes it can: from the highest placed mesh down, each takes the
    /// lowest free run that ends at or below where it begins, until the meshes the plan refused fit (with
    /// <paramref name="fitRefused"/>) or the arena is no longer sparse, at most <see cref="MostCompactionWords"/> words and
    /// one dispatch's moves (<see cref="GpuRegion.MaxMoves"/>) a plan. A mesh no lower run holds stays where it lies.
    /// <para>Invariant: no move's new words overlap the words any move of the same plan, or of a plan whose copies the
    /// region has not recorded, left (<see cref="HoldsMoveSources"/> keeps a plan from compacting until they are
    /// released), so the region's moves read no word another of them writes. The words a move left are free to a mesh
    /// placed afterwards, which lands only through copies recorded after the move.</para></summary>
    /// <param name="fitRefused">Whether the compaction makes room for the refused named meshes, which it then places;
    /// otherwise it lowers the last placed mesh while the arena is sparse.</param>
    /// <returns>Whether it moved or placed a mesh.</returns>
    private bool Compact(bool fitRefused) {
        var need = 0L;

        if (fitRefused) { foreach (var mesh in m_refused) { need += Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: mesh)); } }
        m_compactOrder.Clear();
        foreach (var (mesh, placement) in m_placements) { m_compactOrder.Add(item: (placement.VertexWord, mesh)); }
        m_compactOrder.Sort(comparison: static (left, right) => right.Word.CompareTo(value: left.Word));

        var reserveWords = (((long)m_drawCapacity) * SdfMeshRecord.DrawWords);
        var movedWords = 0L;
        // The highest word a placement the compaction left standing or gave a lower run ends at.
        var highest = 0L;

        m_vacated.Clear();
        for (var index = 0; (index < m_compactOrder.Count); index++) {
            var (word, mesh) = m_compactOrder[index];
            var words = Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: mesh));
            var below = ((index + 1) < m_compactOrder.Count) ? (((long)m_compactOrder[index + 1].Word) + Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: m_compactOrder[index + 1].Mesh))) : reserveWords;
            var end = Math.Max(val1: highest, val2: below);
            // The words under the highest placement left standing, were this mesh to stay where it lies.
            var standing = Math.Max(val1: end, val2: (((long)word) + words));
            var done = (fitRefused
                ? ((LimitWords - standing) >= need)
                : (((standing - reserveWords) - m_placedWords) <= Math.Max(val1: (PackedShare * NamedWords), val2: m_retainedSlackWords)));

            if (done || (m_vacated.Count >= (GpuRegion.MaxMoves - 1)) || ((movedWords + words) > MostCompactionWords)) {
                highest = Math.Max(val1: highest, val2: (word + words));
                break;
            }
            if (!m_ranges.TryAllocate(count: ((ulong)words), start: out var start)) {
                highest = Math.Max(val1: highest, val2: (word + words));
                continue;
            }
            if ((((long)start) + words) > word) {
                _ = m_ranges.Free(start: start);
                highest = Math.Max(val1: highest, val2: (word + words));
                continue;
            }

            var from = m_placements[mesh];

            m_movedFrom[mesh] = from;
            _ = m_moved.Add(item: mesh);
            m_placements[mesh] = PlacementAt(mesh: mesh, vertexWord: ((int)start));
            CountChunks(count: 1, word: ((long)start), words: words);
            m_moveSources.Add(item: (word, words));
            m_vacated.Add(item: ((ulong)word));
            m_placementChanges++;
            movedWords += words;
            highest = Math.Max(val1: highest, val2: (((long)start) + words));
        }
        // The words the moves left are free once every move has taken its new words.
        foreach (var start in m_vacated) { _ = m_ranges.Free(start: start); }

        var placed = 0;

        if (fitRefused) {
            m_scratch.Clear();
            m_scratch.AddRange(collection: m_refused);
            foreach (var mesh in m_scratch) {
                if (TryPlace(mesh: mesh)) { _ = m_refused.Remove(item: mesh); placed++; }
            }
            m_scratch.Clear();
        }
        if ((m_vacated.Count == 0) && (placed == 0)) { return false; }
        Compactions++;
        CompactionMoves += m_vacated.Count;
        CompactionWords += movedWords;
        m_vacated.Clear();

        return true;
    }
    // A placement of a mesh's words from a first word: its vertices, triangle materials, indices and skin, in that order.
    private static SdfMeshRegionMesh PlacementAt(SdfMesh mesh, int vertexWord) {
        var materialWord = (vertexWord + (mesh.Positions.Length * SdfMeshRecord.VertexWords));
        var indexWord = (materialWord + SdfMeshRegion.MaterialWordsOf(mesh: mesh));
        var skinWord = (mesh.VertexMembers.IsEmpty ? 0 : (indexWord + SdfMeshRegion.IndexWordsOf(mesh: mesh)));

        return new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: indexWord, MaterialWord: materialWord, VertexWord: vertexWord, SkinWord: skinWord);
    }
    private void RefreshUnused(SdfMesh mesh) {
        if (m_placements.ContainsKey(key: mesh) && !Names(mesh: mesh)) {
            if (m_unused.Add(item: mesh)) { m_unusedWords += SdfMeshRegion.WordsOf(mesh: mesh); }
        } else if (m_unused.Remove(item: mesh)) { m_unusedWords -= SdfMeshRegion.WordsOf(mesh: mesh); }
    }

    /// <summary>Names the meshes the draws are about to name, in place of those named ahead before: each holding no
    /// words takes the lowest free run that holds it (<see cref="Placed"/>, which the region owes), the meshes no list
    /// names giving their words up first (<see cref="Forgotten"/>), and one that still finds none is refused ahead
    /// (<see cref="AheadRefused"/>). A mesh that leaves the set and no draw names is retained slack, and the meshes
    /// no list names are forgotten once their words pass the retained slack, as a list's plan forgets them. No
    /// named mesh moves, so the draws' records stand. Before the first list of draws it only records the set, which
    /// that list's plan places.</summary>
    /// <param name="meshes">The meshes named ahead; a mesh listed twice is named once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is <see langword="null"/>.</exception>
    public void PlanAhead(IReadOnlyList<SdfMesh> meshes) {
        ArgumentNullException.ThrowIfNull(meshes);

        m_forgotten.Clear();
        m_placed.Clear();
        m_scratch.Clear(); m_scratch.AddRange(collection: m_ahead);
        m_ahead.Clear();
        foreach (var mesh in m_scratch) { RefreshUnused(mesh: mesh); }
        m_scratch.Clear();
        for (var index = 0; (index < meshes.Count); index++) { m_ahead.Add(item: meshes[index]); RefreshUnused(mesh: meshes[index]); }
        if (m_unusedWords > m_retainedSlackWords) { _ = ForgetUnnamed(); }
        _ = PlaceAhead();
    }

    // Places each mesh named ahead that holds no words, once the reserve stands; the words it placed less those it
    // forgot to make room.
    private long PlaceAhead() {
        var words = 0L;

        m_aheadRefused.Clear();
        if (m_drawCapacity == 0) { return words; }
        foreach (var mesh in m_ahead) {
            if (m_placements.ContainsKey(key: mesh)) { continue; }
            if (!TryPlace(mesh: mesh)) {
                // The set names meshes without claiming them, so a mesh its owner has since released is not placed.
                if (mesh.IsReleased) { continue; }
                words -= ForgetUnnamed();
                if (!TryPlace(mesh: mesh)) {
                    _ = m_aheadRefused.Add(item: mesh);
                    continue;
                }
            }
            words += SdfMeshRegion.WordsOf(mesh: mesh);
        }

        return words;
    }
    // Places a mesh at the lowest free run that holds it under a placement claim of its own; false when no free run
    // under the limit holds it, or when the mesh's storage was already released.
    private bool TryPlace(SdfMesh mesh) {
        // A range is at least one word, so a mesh without words still has a place of its own.
        var words = ((ulong)Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: mesh)));

        if (!m_ranges.TryAllocate(count: words, start: out var start)) { return false; }
        // Both the presentation grant and the diagnostic cap constrain the same lowest-run admission.
        if (((start + words) > ((ulong)LimitWords)) ||
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

        var vertexWord = ((int)start);

        m_placements.Add(key: mesh, value: PlacementAt(mesh: mesh, vertexWord: vertexWord));
        m_placed.Add(item: mesh);
        m_placementChanges++;
        m_placedWords += SdfMeshRegion.WordsOf(mesh: mesh);
        CountChunks(count: 1, word: vertexWord, words: SdfMeshRegion.WordsOf(mesh: mesh));
        RefreshUnused(mesh: mesh);

        return true;
    }
    // Frees a placed mesh's words and lets its placement claim go; its words.
    private long Forget(SdfMesh mesh) {
        _ = m_placements.Remove(key: mesh, value: out var placement);
        m_placementChanges++;
        m_placedWords -= SdfMeshRegion.WordsOf(mesh: mesh);
        CountChunks(count: -1, word: placement.VertexWord, words: SdfMeshRegion.WordsOf(mesh: mesh));
        RefreshUnused(mesh: mesh);
        mesh.Release(cached: true);

        return ((long)m_ranges.Free(start: ((ulong)placement.VertexWord)));
    }
    // Forgets every placed mesh the latest list does not name; their words.
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
    // Relocates every mesh the list names: forgets every placed mesh and places the named ones again from the reserve's
    // end in the list's order, refusing those the limit still cannot hold.
    private void Pack() {
        m_scratch.Clear();
        m_scratch.AddRange(collection: m_placements.Keys);
        foreach (var mesh in m_scratch) { _ = Forget(mesh: mesh); }
        m_scratch.Clear();
        m_forgotten.Clear();
        m_moved.Clear();
        m_movedFrom.Clear();
        m_placed.Clear();
        m_refused.Clear();
        for (var row = 0; (row < m_latestMeshCount); row++) {
            if (m_latestMeshes[row] is not { } mesh) { continue; }

            if (m_placements.ContainsKey(key: mesh) || m_refused.Contains(item: mesh)) { continue; }
            if (!TryPlace(mesh: mesh)) { _ = m_refused.Add(item: mesh); }
        }
        _ = PlaceAhead();
    }
    // The reserve a list of drawCount draws takes: the current one while it holds the list, otherwise doubled, at least the
    // list and MinimumDrawCapacity, at most SdfMeshRegion.MaxDraws.
    private int GrownCapacity(int drawCount) => ((int)GpuCapacity.Grown(ceiling: SdfMeshRegion.MaxDraws, current: m_drawCapacity,
        divisor: 1, minimum: MinimumDrawCapacity, required: drawCount));
    // Grows the reserve for a list of drawCount draws: every mesh whose words begin inside the grown reserve leaves its
    // placement, a named one to be placed again (Moved) and any other forgotten, and the reserve takes the words from zero.
    private void GrowReserve(int drawCount) {
        var held = (m_drawCapacity > 0);

        m_drawCapacity = GrownCapacity(drawCount: drawCount);

        var reserveEnd = (((long)m_drawCapacity) * SdfMeshRecord.DrawWords);

        // The draw records are what a residency cannot draw without: the limit bounds meshes, never the reserve.
        if (reserveEnd > ((long)m_ranges.Size)) {
            throw new InvalidOperationException(message: $"A list of {drawCount} draws reserves {(reserveEnd * sizeof(uint))} bytes of draw records, past the {(((long)m_ranges.Size) * sizeof(uint))} a mesh region holds.");
        }

        m_scratch.Clear();
        foreach (var (mesh, placement) in m_placements) {
            if (placement.VertexWord < reserveEnd) { m_scratch.Add(item: mesh); }
        }
        foreach (var mesh in m_scratch) {
            var from = m_placements[mesh];

            _ = Forget(mesh: mesh);
            if (Names(mesh: mesh)) {
                var words = Math.Max(val1: 1, val2: SdfMeshRegion.WordsOf(mesh: mesh));

                _ = m_moved.Add(item: mesh);
                m_movedFrom[mesh] = from;
                CountChunks(count: 1, word: from.VertexWord, words: words);
                m_moveSources.Add(item: (from.VertexWord, words));
            } else {
                m_forgotten.Add(item: mesh);
            }
        }
        m_scratch.Clear();
        if (held) { _ = m_ranges.Free(start: 0UL); }

        // Every range that began under the reserve's end is free, so the lowest run that holds the reserve begins at zero.
        _ = m_ranges.Allocate(count: ((ulong)reserveEnd));
        CountReserve();
    }

    /// <summary>Releases the arena's CPU source claims after all uploads and staged draw readers finish.</summary>
    public void Dispose() {
        foreach (var mesh in m_placements.Keys) { mesh.Release(cached: true); }
        m_placements.Clear();
    }
    /// <summary>Forgets the placed meshes no current draw names, freeing their words and CPU source claims and keeping
    /// every current mesh address standing.</summary>
    public void TrimUnused() {
        var before = m_forgotten.Count;

        _ = ForgetUnnamed();
        m_trimmed |= (m_forgotten.Count > before);
        m_forgotten.RemoveRange(count: (m_forgotten.Count - before), index: before);
    }
}
