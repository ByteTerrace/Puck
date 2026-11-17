using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

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
/// <para>A plan relocates every mesh a list names, packing the arena from the reserve's end in the list's order and
/// forgetting every other mesh, only on the first plan, or when a named mesh finds no free run although the named meshes
/// packed would fit the limit, or when the free words under the last placed mesh pass both <see cref="PackedShare"/>
/// times the named meshes' words and the retained slack. The plan states what it owes: <see cref="Relocated"/>, or the
/// meshes it placed (<see cref="Placed"/>), of which the <see cref="Moved"/> ones held words a draw read until this
/// plan.</para>
/// <para>A mesh the draws are about to name is named ahead of them (<see cref="PlanAhead"/>): it is placed and kept
/// like a mesh a draw names, so its words can land while the draws still read the mesh it replaces, and a list that
/// then names it finds it where it lies. One no free run holds is refused ahead (<see cref="AheadRefused"/>), which
/// refuses no draw and never relocates the arena.</para>
/// </summary>
public sealed class SdfMeshRegionArena : IDisposable {
    /// <summary>The fewest draw records the region reserves.</summary>
    public const int MinimumDrawCapacity = 256;
    /// <summary>The multiple of the named meshes' words the free words under the last placed mesh may reach before a
    /// plan packs the arena, where they also pass the retained slack.</summary>
    public const int PackedShare = 3;
    /// <summary>The words outside the named meshes (meshes no list names any more) the arena keeps before a plan forgets
    /// them: 32 MB, so a body crossing between worlds finds its meshes where it left them rather than writing them
    /// again.</summary>
    public const int RetainedSlackWords = (1 << 23);

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

    private readonly int m_retainedSlackWords;
    // The region's words up to the limit: the reserve is the range at zero, and each placed mesh a range of its own.
    private readonly GpuRangeAllocator m_ranges;

    private readonly HashSet<SdfMesh> m_toPlace = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_unused = new(ReferenceEqualityComparer.Instance);
    private long m_placedWords;
    private long m_unusedWords;
    private bool m_firstRows;
    private int m_drawCapacity;
    // Whether a trim forgot a mesh since the latest full plan, so a list naming a refused mesh is planned again.
    private bool m_trimmed;
    private bool m_forceRelocation;
    private ulong? m_admissionBytes;

    /// <summary>Sets the diagnostic admission cap and requests a relocation so already placed meshes pass through
    /// the same placing function. Null restores the device limit.</summary>
    /// <param name="bytes">The cap in bytes, or null.</param>
    public void ForceLimit(ulong? bytes) { m_admissionBytes = bytes; ForceRelocation(); }
    /// <summary>Requests that the next plan pack its meshes regardless of the normal slack threshold.</summary>
    public void ForceRelocation() => m_forceRelocation = true;

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
            _ = m_placed.Remove(item: mesh);
            if (m_named.ContainsKey(key: mesh)) {
                _ = m_moved.Add(mesh);
                _ = m_refused.Add(item: mesh);
            } else if (m_ahead.Contains(item: mesh)) {
                _ = m_moved.Remove(mesh);
                _ = m_aheadRefused.Add(item: mesh);
            } else {
                m_forgotten.Add(item: mesh);
            }
        }
        m_scratch.Clear();
        LimitWords = words;
    }

    /// <summary>Gets the region's words: the reserved draw records and the arena up to its last placed mesh.</summary>
    public int Words => checked((int)m_ranges.LiveEnd);
    /// <summary>Gets the meshes the latest plan placed, whose words it owes; every placed mesh when it
    /// <see cref="Relocated"/>.</summary>
    public IReadOnlyList<SdfMesh> Placed => m_placed;
    /// <summary>Gets the meshes the latest plan moved out of the words a grown reserve took: the region drew them from
    /// their old words until this plan, so it writes each one placed again before the draws read it rather than letting
    /// it land over later frames. One no free run held is <see cref="Refused"/> instead of placed.</summary>
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
        BeginRows(draws.Count);
        for (var row = 0; row < draws.Count; row++) { NameRow(row, draws[row].Mesh); }
        EndRows();
    }
    // Row transactions collect all unnames before placement, so their released slack is available to every new name.
    internal void BeginRows(int extent) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(extent, SdfMeshRegion.MaxDraws);
        m_placed.Clear(); m_moved.Clear(); m_forgotten.Clear(); m_toPlace.Clear();
        Relocated = false; Unchanged = true;
        m_firstRows = m_drawCapacity == 0 && extent > 0;
        if (m_latestMeshes.Length < extent) { Array.Resize(ref m_latestMeshes, Math.Max(extent, 2 * m_latestMeshes.Length)); }
        for (var row = extent; row < m_latestMeshCount; row++) { NameRow(row, null); }
        m_latestMeshCount = extent;
    }
    internal void NameRow(int index, SdfMesh? mesh) {
        var old = m_latestMeshes[index];
        if (ReferenceEquals(old, mesh)) { return; }
        Unchanged = false;
        m_latestMeshes[index] = mesh;
        if (old is not null) {
            var left = m_named[old] - 1;
            if (left == 0) { m_named.Remove(old); m_refused.Remove(old); NamedWords -= SdfMeshRegion.WordsOf(old); }
            else { m_named[old] = left; }
            RefreshUnused(old);
        }
        if (mesh is null) { return; }
        SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.StagingPlanRecords);
        ref var names = ref CollectionsMarshal.GetValueRefOrAddDefault(m_named, mesh, out var named);
        names++;
        if (!named) { NamedWords += SdfMeshRegion.WordsOf(mesh); }
        RefreshUnused(mesh);
        if (!m_placements.ContainsKey(mesh)) { m_toPlace.Add(mesh); }
    }
    internal void EndRows() {
        if (m_latestMeshCount > m_drawCapacity) {
            GrowReserve(m_latestMeshCount);
            foreach (var mesh in m_moved) { m_toPlace.Add(mesh); }
            Unchanged = false;
        }
        if (m_unusedWords > m_retainedSlackWords) { _ = ForgetUnnamed(); Unchanged = false; }
        if (m_trimmed) { foreach (var mesh in m_refused) { m_toPlace.Add(mesh); } m_trimmed = false; }
        foreach (var mesh in m_toPlace) {
            if (!m_named.ContainsKey(mesh) || m_placements.ContainsKey(mesh)) { continue; }
            Unchanged = false;
            if (TryPlace(mesh)) { m_refused.Remove(mesh); continue; }
            _ = ForgetUnnamed();
            if (TryPlace(mesh)) { m_refused.Remove(mesh); } else { m_refused.Add(mesh); }
        }
        var reserveWords = (long)m_drawCapacity * SdfMeshRecord.DrawWords;
        var free = Words - reserveWords - m_placedWords;
        var relocate = m_forceRelocation || (m_refused.Count > 0 && reserveWords + NamedWords <= LimitWords && !Unchanged) ||
            (free > PackedShare * NamedWords && free > m_retainedSlackWords);
        if (relocate) {
            RelocationReason = m_forceRelocation ? "forced relocation" : m_refused.Count > 0 ? "named meshes fit packed" : "free words exceed packed share";
            m_forceRelocation = false;
            Pack();
        } else if (m_firstRows) { RelocationReason = "first plan"; }
        Relocated = relocate || m_firstRows;
        if (Relocated) { Relocations++; Unchanged = false; }
    }
    /// <summary>Changes one row's mesh name using the same operation as a whole plan.</summary>
    public void Rename(int index, SdfMesh mesh) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, m_latestMeshCount);
        NameRow(index, mesh);
    }
    private void RefreshUnused(SdfMesh mesh) {
        if (m_placements.ContainsKey(mesh) && !Names(mesh)) {
            if (m_unused.Add(mesh)) { m_unusedWords += SdfMeshRegion.WordsOf(mesh); }
        } else if (m_unused.Remove(mesh)) { m_unusedWords -= SdfMeshRegion.WordsOf(mesh); }
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
        m_scratch.Clear(); m_scratch.AddRange(m_ahead);
        m_ahead.Clear();
        foreach (var mesh in m_scratch) { RefreshUnused(mesh); }
        m_scratch.Clear();
        for (var index = 0; index < meshes.Count; index++) { m_ahead.Add(meshes[index]); RefreshUnused(meshes[index]); }
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
    // Places a mesh at the lowest free run that holds it; false when no free run under the limit holds it.
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

        var vertexWord = ((int)start);
        var materialWord = (vertexWord + (mesh.Positions.Length * SdfMeshRecord.VertexWords));
        var indexWord = (materialWord + SdfMeshRegion.MaterialWordsOf(mesh: mesh));
        var skinWord = (mesh.VertexMembers.IsEmpty ? 0 : (indexWord + SdfMeshRegion.IndexWordsOf(mesh: mesh)));

        m_placements.Add(key: mesh, value: new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: indexWord, MaterialWord: materialWord, VertexWord: vertexWord, SkinWord: skinWord));
        mesh.Acquire(cached: true);
        m_placed.Add(item: mesh);
        m_placedWords += SdfMeshRegion.WordsOf(mesh);
        RefreshUnused(mesh);

        return true;
    }
    // Frees a placed mesh's words and lets its placement claim go; its words.
    private long Forget(SdfMesh mesh) {
        _ = m_placements.Remove(key: mesh, value: out var placement);
        m_placedWords -= SdfMeshRegion.WordsOf(mesh);
        RefreshUnused(mesh);
        mesh.Release(cached: true);

        return ((long)m_ranges.Free(start: ((ulong)placement.VertexWord)));
    }
    // Forgets every placed mesh the latest list does not name; their words.
    private long ForgetUnnamed() {
        var words = 0L;

        m_scratch.Clear();
        m_scratch.AddRange(m_unused);
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
        m_placed.Clear();
        m_refused.Clear();
        for (var row = 0; row < m_latestMeshCount; row++) {
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
            _ = Forget(mesh: mesh);
            if (Names(mesh: mesh)) {
                _ = m_moved.Add(item: mesh);
            } else {
                m_forgotten.Add(item: mesh);
            }
        }
        m_scratch.Clear();
        if (held) { _ = m_ranges.Free(start: 0UL); }

        // Every range that began under the reserve's end is free, so the lowest run that holds the reserve begins at zero.
        _ = m_ranges.Allocate(count: ((ulong)reserveEnd));
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

