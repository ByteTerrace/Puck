using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) in one GpuRegion, created like every other table
// (SdfWorldTables.Regions.cs) and copied with them in the upload. The region's arena (SdfMeshRegionArena) keeps each mesh
// at the words it was first placed at, so a frame whose draw list is the one last packed, at the revision and program
// composability last packed, packs nothing, and any other packs its draw records and only the meshes the arena newly
// placed: a body that moves rewrites its record's matrix, and a mesh that lands writes its own words, never the meshes
// already placed. Newly placed meshes land within a frame's share of words (MeshLandingWords), those the views see largest
// first (MeshDemand), and a draw whose mesh is still owed waits out of the staged records until it lands, so a body
// crossing into a world with all its meshes at once lands over a few frames. A plan that relocates every mesh writes every word at once. The
// region is created with the tables, one draw record long so every set that binds it binds a buffer, and grows to the need
// plus an eighth without waiting for the device, the next upload carrying the words it held on the GPU; a frame without draws
// keeps it. A view's mesh pass reads the draws' triangles from it, and primary a mesh hit's material.
public sealed partial class SdfWorldTables {
    private readonly SdfMeshRegionArena m_meshArena = new();

    private SdfMeshClaims? m_meshClaims;
    private bool m_meshTrimListening;
    // One while an account trim request waits: it is raised on whichever thread asked, and the arena and its claims
    // belong to the frame thread, which services it every frame (ServiceMemoryTrim), packed or not.
    private int m_meshTrimRequested;

    private void RequestMeshTrim() {
        if (m_disposed) { return; }
        if (Interlocked.Exchange(location1: ref m_meshTrimRequested, value: 1) == 0) { Puck.Hosting.Jobs.MemoryAccount.Shared.DeferTrim(); }
    }

    /// <summary>Releases, on the frame thread, the placement claims of meshes the latest draws no longer name when the
    /// process account has asked its caches to trim since the last call; otherwise does nothing.</summary>
    internal void ServiceMemoryTrim() {
        if (Interlocked.Exchange(location1: ref m_meshTrimRequested, value: 0) == 0) { return; }
        try {
            m_meshArena.TrimUnused();
            m_meshesOwed.RemoveWhere(match: mesh => !m_meshArena.Placements.ContainsKey(key: mesh));
            // The source order walks the placements, so a trim that forgot one rebuilds it.
            m_meshOrderStale = true;
        } finally {
            Puck.Hosting.Jobs.MemoryAccount.Shared.CompleteDeferredTrim();
        }
    }

    // Whether the arena has written its meshes into the region.
    private bool m_meshSectionWritten;
    // The one-based upload required by the current sourced geometry or buffer carry. Draw matrices and irradiance
    // never move it. Zero means no geometry has needed an upload yet.
    private ulong m_meshRequiredUpload;

    /// <summary>Gets whether the current staged mesh geometry owes no mesh words and its required upload has
    /// completed. Pose, draw-record and irradiance uploads do not delay it. Read on the frame thread; never waits.</summary>
    public bool MeshesResident {
        get {
            if (m_disposed || (m_owedWords != 0)) { return false; }
            var required = m_meshRequiredUpload;
            return ((required == 0UL) || ((required <= m_uploads) &&
                ((required <= m_completedUploads) || m_frameFences[((int)((required - 1UL) % FrameRingSize))].IsSignaled)));
        }
    }

    // Invalidate before staging words or replacing their buffer: until the next submission completes, the geometry
    // currently bound by these tables is not resident, even if an earlier slot's fence is already signaled.
    private void RequireMeshUpload() => m_meshRequiredUpload = checked(m_uploads + 1UL);
    // The draw list last planned and its revision, and whether its draws are all staged: a list whose meshes' words are
    // still owed stages again each frame as they land.
    private IReadOnlyList<SdfMeshDraw>? m_meshSource;
    private long m_meshSourceRevision;
    private bool m_meshStaged;
    // A consumed draw journal belongs to the exact staged roster, not just a revision number. Landing filters and
    // unknown/missing history go through ordinary planning before establishing a new cursor.
    private GpuChangedRows? m_drawChangesSource;
    private long m_drawChangesSerial;
    private readonly GpuUploadRuns m_owedDraws = new(capacity: GpuRegion.MaxCopyRuns);
    /// <summary>Gets the logical array payload retained by this consumer's changed-draw and transform-cut runs.</summary>
    public long DrawChangeScratchBytes => (m_owedDraws.StorageBytes +
        m_meshTransformReseats.StorageBytes + m_reseatedDraws.StorageBytes);

    // The placed meshes whose words the region still owes, and the draws of the planned list whose meshes are written.
    private readonly HashSet<SdfMesh> m_meshesOwed = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_activeOwed = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMeshDraw> m_meshLanded = [];
    // A copy of each draw last staged, by index, and whether that staging staged its whole list (no draw waiting on an
    // owed mesh): what StageChangedDraws compares a list against.
    private SdfMeshDraw[] m_stagedDraws = [];

    private int m_stagedCount;
    private bool m_stagedWhole;

    /// <summary>The words of owed meshes a 60 Hz frame writes into the mesh region at most, past the first mesh it writes:
    /// 2 MB, so a body whose meshes land at once, as when it crosses into a world, lands over a few frames rather than in
    /// one. A longer frame writes a share for each whole 60 Hz frame it spans (<see cref="SdfMeshPathBake.FrameShares"/>),
    /// so a cold bake's long frames land its meshes at the rate a 60 Hz display would.</summary>
    public const int MeshLandingWords = (1 << 19);

    private readonly GpuRegion m_meshRegion;

    // One more for every new draw list the region packs: the cadence signature folds it, so a frame whose draws moved
    // renders.
    private long m_meshRevision;
    private ulong m_meshRegionBytes;
    private ulong m_meshRegionUsedBytes;
    // The draws the frame being rendered rasterizes, from its staged draw list, and how many of them are glass.
    private uint m_meshDrawCount;
    private uint m_glassDrawCount;

    // The draw records as the next write sends them, and one mesh's words as a flush stages them.
    private uint[] m_meshDrawWords = [];
    private uint[] m_meshWords = [];
    private readonly GpuUploadRuns m_meshDrawDifferences = new(capacity: GpuRegion.MaxCopyRuns, mergeGap: GpuRegion.StagedMergeGapWords);

    // The region's source for its mesh section, and the placed meshes by their first word, sorted again after a plan
    // places or forgets one.
    private MeshSection? m_meshSection;

    private readonly List<(int Word, SdfMesh Mesh)> m_meshOrder = [];
    private bool m_meshOrderStale = true;
    // The zeros a flush stages into words no placed mesh holds.
    private static readonly uint[] ZeroWords = new uint[(1 << 16)];

    /// <summary>Gets the words the mesh region's draw records and meshes take for the draws the latest frame staged; zero
    /// before a frame draws a mesh.</summary>
    public int MeshRegionWords => m_meshArena.Words;

    /// <summary>The most bytes one mesh region holds, a whole number of words (<see cref="GpuRegion.MaxByteCount"/>): the
    /// baked meshes a frame draws must fit it, and a frame that needs more throws.</summary>
    public const ulong MostMeshRegionBytes = ((ulong)GpuRegion.MaxByteCount);
    /// <summary>The unit a grown mesh region's size is rounded up to: 16 MiB.</summary>
    public const ulong MeshRegionGranuleBytes = (16UL << 20);

    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for: one draw record's until a frame
    /// needs more. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);
    /// <summary>Gets the bytes of the mesh region the latest planned draw list uses: its draw records and the meshes it
    /// places, which <see cref="MeshRegionBytes"/> holds with room to grow. Safe to read from any thread.</summary>
    public ulong MeshRegionUsedBytes => Volatile.Read(location: ref m_meshRegionUsedBytes);

    /// <summary>Returns the size a mesh region grows to when its meshes need <paramref name="needBytes"/>: the need plus an
    /// eighth, rounded up to <see cref="MeshRegionGranuleBytes"/> and capped at <see cref="MostMeshRegionBytes"/>. The size
    /// is never below the need and is a whole number of words.</summary>
    /// <param name="needBytes">The bytes the planned draws and meshes use; at most <see cref="MostMeshRegionBytes"/>.</param>
    /// <returns>The region's new size in bytes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="needBytes"/> is past <see cref="MostMeshRegionBytes"/>.</exception>
    public static ulong MeshRegionGrownBytes(ulong needBytes) {
        if (needBytes > MostMeshRegionBytes) {
            throw new InvalidOperationException(message: $"The baked meshes need {needBytes} bytes of mesh region, past the {MostMeshRegionBytes} one region holds.");
        }

        var wanted = (needBytes + (needBytes / 8UL));

        return Math.Min(
            val1: (((wanted + (MeshRegionGranuleBytes - 1UL)) / MeshRegionGranuleBytes) * MeshRegionGranuleBytes),
            val2: MostMeshRegionBytes
        );
    }

    /// <summary>Gets the draws the latest frame's mesh passes rasterize in each view, the glass draws among them.</summary>
    public uint MeshDrawCount => m_meshDrawCount;
    /// <summary>Gets how many of <see cref="MeshDrawCount"/> are glass (<see cref="SdfMeshDraw.Glass"/>): the glass pass
    /// draws them, and the mesh pass and the shadow cascades draw the rest.</summary>
    public uint GlassDrawCount => m_glassDrawCount;
    /// <summary>Gets the opaque draws the latest frame's mesh pass and shadow cascades rasterize.</summary>
    public uint OpaqueDrawCount => (m_meshDrawCount - m_glassDrawCount);

    // Packs new draws or a new revision, growing the region when the list needs more
    // bytes; the upload sends the slot what it owes.
    private void StageMeshRegion(SdfFrame frame, double frameSeconds) {
        var draws = frame.MeshDraws;
        var revision = frame.MeshDrawsRevision;
        if (!m_meshTrimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested += RequestMeshTrim; m_meshTrimListening = true; }
        ServiceMemoryTrim();
        if (draws.Count == 0) { m_meshHistory.RetireAfter(count: 0); }
        PrepareMeshTransformCuts(frame);
        if (TryStageDrawChanges(frame)) { CommitMeshTransformCuts(frame); return; }
        // Equal-pose cuts need staging too. This also forces the ordinary landing-filter path to observe its actual
        // staged roster, where producer draw indices cannot be used.
        if (m_reseatedDraws.Count > 0) { m_meshStaged = false; }
        // The staged list's meshes hold live claims; the arena's placement claims keep every placed mesh's storage, the
        // ones the list stopped naming included, until the arena forgets them.
        if (!ReferenceEquals(draws, m_meshSource) || (revision != m_meshSourceRevision)) {
            (m_meshClaims ??= new SdfMeshClaims()).Update(draws: draws);
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingClaimRecords, draws.Count);
        }
        StageMeshRegionCore(draws: draws, landingWords: (((long)MeshLandingWords) * SdfMeshPathBake.FrameShares(frameSeconds: frameSeconds)), revision: revision);
        // Publish the cursor only after the whole roster has staged successfully. No landing-filtered index can be
        // interpreted as an index into the producer's list.
        m_drawChangesSource = (m_meshStaged && m_stagedWhole) ? frame.MeshDrawChanges : null;
        m_drawChangesSerial = frame.MeshDrawChangesSerial;
        CommitMeshTransformCuts(frame);
    }
    private bool TryStageDrawChanges(SdfFrame frame) {
        var changes = frame.MeshDrawChanges;
        var draws = frame.MeshDraws;
        if ((changes is null) || !ReferenceEquals(changes, m_drawChangesSource) || !ReferenceEquals(draws, m_meshSource) ||
            !m_meshStaged || !m_stagedWhole || (m_owedWords != 0) || (draws.Count != m_stagedCount) ||
            ((frame.MeshDrawsRevision != m_meshSourceRevision) && (frame.MeshDrawChangesSerial <= m_drawChangesSerial)) ||
            !changes.TryCollect(since: m_drawChangesSerial, through: frame.MeshDrawChangesSerial, into: m_owedDraws)) { return false; }

        CopyRuns(from: m_reseatedDraws, to: m_owedDraws);
        var checkedMeshes = 0;
        for (var run = 0; run < m_owedDraws.Count; run++) {
            var end = Math.Min(draws.Count, checked(m_owedDraws.Start(run) + m_owedDraws.Length(run)));
            for (var index = m_owedDraws.Start(run); index < end; index++) {
                checkedMeshes++;
                if (!ReferenceEquals(draws[index].Mesh, m_stagedDraws[index].Mesh)) {
                    SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingMeshChecks, checkedMeshes);
                    return false;
                }
            }
        }
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingMeshChecks, checkedMeshes);
        // Every mesh is still held at its existing arena address. Pose/metadata changes need neither a new placement
        // plan nor a walk of all mesh claims. Coarsened journal runs may include unchanged records; compare them once.
        if ((draws.Count > 0) && (StageChangedDraws(draws, m_owedDraws) > 0)) { m_meshRevision++; }
        m_meshSourceRevision = frame.MeshDrawsRevision;
        m_drawChangesSerial = frame.MeshDrawChangesSerial;
        return true;
    }
    private void StageMeshRegionCore(IReadOnlyList<SdfMeshDraw> draws, long landingWords, long revision) {
        if (
            !ReferenceEquals(
                objA: draws,
                objB: m_meshSource
            ) ||
            (revision != m_meshSourceRevision)
        ) {
            m_meshArena.Plan(draws: draws);
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingPlanRecords, draws.Count);
            m_meshOrderStale |= !m_meshArena.Unchanged;
            Volatile.Write(
                location: ref m_meshRegionUsedBytes,
                value: (((ulong)m_meshArena.Words) * sizeof(uint))
            );

            if (m_meshArena.Words > 0) {
                if (m_meshArena.Relocated) {
                    FitMeshRegion(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                }
                EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                ShadowMeshRecords();
                // Every mesh is written again at once when the arena moved them or none was written yet, since no mesh's
                // words stand where its draw reads them. Otherwise the meshes a grown draw reserve moved are written now,
                // since their draws were drawn from the words the reserve took, and the rest this list placed are owed and
                // land over the next frames.
                if (m_meshArena.Relocated || !m_meshSectionWritten) {
                    m_meshesOwed.Clear();
                    foreach (var (mesh, placement) in m_meshArena.Placements) { WriteMeshWords(mesh: mesh, placement: placement); }
                } else {
                    m_meshesOwed.UnionWith(other: m_meshArena.Placed);
                    foreach (var mesh in m_meshArena.Moved) {
                        _ = m_meshesOwed.Remove(item: mesh);
                        WriteMeshWords(mesh: mesh, placement: m_meshArena.Placements[mesh]);
                    }
                }
                m_meshSectionWritten = true;
            }

            m_meshSource = draws;
            m_meshSourceRevision = revision;
            m_meshStaged = false;
        }

        if (m_meshStaged) {
            return;
        }

        WriteOwedMeshes(draws: draws, words: landingWords);
        CountOwed(draws);

        // A draw whose mesh's words are still owed waits for them: the staged list holds the draws whose meshes are
        // written, and the next frames write the rest.
        var staged = draws;

        if ((m_meshesOwed.Count > 0) && draws.Any(predicate: draw => m_meshesOwed.Contains(item: draw.Mesh))) {
            m_meshLanded.Clear();
            foreach (var draw in draws) {
                if (!m_meshesOwed.Contains(item: draw.Mesh)) { m_meshLanded.Add(item: draw); }
            }
            staged = m_meshLanded;
        }

        // While existing mesh addresses stand, a same-sized list owes only its changed draws, including draws
        // that swapped meshes. Every other record, box and motion row stands as it was staged.
        if (
            !m_meshArena.Relocated &&
            (m_meshArena.Moved.Count == 0) &&
            m_stagedWhole &&
            ReferenceEquals(objA: staged, objB: draws) &&
            (staged.Count == m_stagedCount) &&
            (m_meshArena.Words > 0)
        ) {
            if (StageChangedDraws(draws: staged) > 0) { m_meshRevision++; }
            m_meshStaged = true;

            return;
        }

        var drawWords = (staged.Count * SdfMeshRecord.DrawWords);

        if (drawWords > m_meshDrawWords.Length) {
            m_meshDrawWords = new uint[drawWords];
        }

        var records = m_meshDrawWords.AsSpan(length: drawWords, start: 0);

        StageMeshMotion(
            draws: staged,
            words: records
        );

        if (m_meshArena.Words > 0) {
            _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: records), offset: 0);
        }

        m_meshDrawCount = ((uint)staged.Count);
        m_glassDrawCount = 0;
        for (var index = 0; (index < staged.Count); index++) {
            var draw = staged[index];

            if (draw.Glass) { m_glassDrawCount++; }
        }
        if (m_stagedDraws.Length < staged.Count) {
            m_stagedDraws = new SdfMeshDraw[Math.Max(val1: staged.Count, val2: (2 * m_stagedDraws.Length))];
        }
        for (var index = 0; (index < staged.Count); index++) {
            m_stagedDraws[index] = staged[index];
        }
        Array.Clear(array: m_stagedDraws, index: staged.Count, length: (m_stagedCount - Math.Min(val1: m_stagedCount, val2: staged.Count)));
        m_stagedCount = staged.Count;
        m_meshRevision++;
        m_meshStaged = ReferenceEquals(objA: staged, objB: draws);
        m_stagedWhole = m_meshStaged;
    }
    // Stages changed draws at their indices while existing mesh addresses stand: each changed record, box and motion
    // row, and the region's words for each run of them.
    private int StageChangedDraws(IReadOnlyList<SdfMeshDraw> draws, GpuUploadRuns? candidates = null) {
        var records = m_meshDrawWords.AsSpan(length: (draws.Count * SdfMeshRecord.DrawWords), start: 0);
        // The records as the region last held them, which a moved matrix is compared against before it is written.
        var old = m_meshRegion.ContentsAt(
            length: ((int)Math.Min(val1: m_meshRegion.ByteCount, val2: (((long)draws.Count) * SdfMeshRegion.DrawBytes))),
            offset: 0
        );
        m_meshDrawDifferences.Clear();
        var changedRecords = 0;
        var changedWords = 0L;
        var comparedRecords = 0;
        var tally = new StagingTally();

        for (var run = 0; run < (candidates?.Count ?? 1); run++) {
            var start = candidates?.Start(run) ?? 0;
            var end = candidates is null ? draws.Count : Math.Min(draws.Count, checked(start + candidates.Length(run)));
            for (var index = start; index < end; index++) {
                var draw = draws[index];
                comparedRecords++;

                if (!draw.SameStagedContent(in m_stagedDraws[index]) ||
                    (m_reseatedDraws.Contains(index) && MeshTransformWasReseated(in draw))) {
                    var record = records.Slice(length: SdfMeshRecord.DrawWords, start: (index * SdfMeshRecord.DrawWords));

                    var mask = StageMeshDraw(draw: in draw, index: index, old: old, record: record, tally: ref tally, incremental: true);
                    changedWords += m_meshDrawDifferences.AddMask(mask, start: (index * SdfMeshRecord.DrawWords));
                    changedRecords++;
                    if (draw.Glass != m_stagedDraws[index].Glass) {
                        m_glassDrawCount = (draw.Glass ? (m_glassDrawCount + 1) : (m_glassDrawCount - 1));
                    }
                    m_stagedDraws[index] = draw;
                }
            }
        }
        // Every previous-matrix decision above reads the unmodified shadow. Only the exact packed-word delta is
        // copied and owed now; bounded run coarsening may include unchanged words already valid in records.
        if (m_meshDrawDifferences.Count > 0) { _ = m_meshRegion.WriteRuns(words: records, runs: m_meshDrawDifferences); }
        tally.Publish();
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.RecordsCompared, amount: comparedRecords);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.RecordsChanged, amount: changedRecords);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.PackedWordsChanged, amount: changedWords);
        return changedRecords;
    }
    /// <summary>Gets or sets the demand points the mesh region lands owed meshes by (<see cref="SdfMeshDemand"/>): the
    /// draws they see largest first, a draw no host box bounds (a skinned body) before any, and the list's order among
    /// equals; <see langword="null"/>, or one with no point, lands them in the list's order. Read on the frame's
    /// thread.</summary>
    public SdfMeshDemand? MeshDemand { get; set; }

    // The draws naming owed meshes by priority, reused frame to frame.
    private readonly List<(float Priority, int Index)> m_owedOrder = [];
    // Higher priority first, then the list's order.
    private static readonly Comparison<(float Priority, int Index)> ByPriority = static (left, right) =>
        ((right.Priority.CompareTo(value: left.Priority) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index));

    // Writes the owed meshes the draw list names, those the demand points see largest first, until the frame's share of
    // words is spent; a frame writes at least one. A placed mesh no draw names stays owed until a list names it again.
    private void WriteOwedMeshes(IReadOnlyList<SdfMeshDraw> draws, long words) {
        if (m_meshesOwed.Count == 0) { return; }

        var demand = ((MeshDemand is { Count: > 0 } points) ? points : null);

        m_owedOrder.Clear();
        for (var index = 0; (index < draws.Count); index++) {
            var draw = draws[index];

            if (!m_meshesOwed.Contains(item: draw.Mesh)) { continue; }
            m_owedOrder.Add(item: (((demand is null)
                ? 0f
                : (SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half) ? demand.Priority(center: center, half: half) : float.PositiveInfinity)), index));
        }
        CollectionsMarshal.AsSpan(list: m_owedOrder).Sort(comparison: ByPriority);

        var written = 0L;

        foreach (var (_, index) in m_owedOrder) {
            var mesh = draws[index].Mesh;

            if (written >= words) { return; }
            if (m_meshesOwed.Remove(item: mesh)) {
                WriteMeshWords(mesh: mesh, placement: m_meshArena.Placements[mesh]);
                written += SdfMeshRegion.WordsOf(mesh: mesh);
            }
        }
    }
    // Owes one placed mesh's words: the region keeps no copy of the mesh section, and the upload's flush stages the words
    // from the mesh itself (WriteSourcedMeshWords). The byte offset is a long: a mesh placed past 512 M words starts past
    // 2 GiB.
    private void WriteMeshWords(SdfMesh mesh, SdfMeshRegionMesh placement) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        RequireMeshUpload();
        m_meshWordsWritten += words;
        m_meshRegion.OweSourced(
            length: (((long)words) * sizeof(uint)),
            offset: (((long)placement.VertexWord) * sizeof(uint))
        );
    }

    // The mesh words the region has written, the frames and time it spent owing words, the words it owes now, and since
    // when it has owed them (a Stopwatch timestamp; zero while it owes none).
    private long m_meshWordsWritten;
    private long m_owedFrames;
    private long m_owedTicks;
    private long m_owedWords;
    private long m_owedSince;

    // Counts a staging that still owes meshes, and the time from the first such staging to the one that owes none.
    private void CountOwed(IReadOnlyList<SdfMeshDraw> draws) {
        var words = 0L;
        m_activeOwed.Clear();
        // Retained placements can be absent from the current roster. They still need an upload if reused, but they
        // neither block this roster's residency nor disable its changed-draw fast path. Count a shared mesh once.
        foreach (var draw in draws) {
            var mesh = draw.Mesh;
            if (m_meshesOwed.Contains(mesh) && m_activeOwed.Add(mesh)) { words += SdfMeshRegion.WordsOf(mesh: mesh); }
        }
        Volatile.Write(location: ref m_owedWords, value: words);

        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (words > 0) {
            m_owedFrames++;
            if (m_owedSince == 0) { m_owedSince = now; }
        } else if (m_owedSince != 0) {
            m_owedTicks += (now - m_owedSince);
            m_owedSince = 0;
        }
    }

    /// <summary>Returns whether the mesh region still owes a mesh's words: the latest staged draw list placed it and its
    /// draws wait out of the staged records until a frame writes them. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked group's mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> while the region owes it.</returns>
    public bool OwesMesh(SdfMesh mesh) => (m_meshArena.Placements.ContainsKey(key: mesh) && m_meshesOwed.Contains(item: mesh));
    /// <summary>Describes the mesh region's landing: the bytes it holds and the latest list uses, the mesh bytes written
    /// into it, the bytes it still owes, the frames and seconds it spent owing them, and the words a frame lands at most.
    /// Safe to read from any thread, the counts being a frame's.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeLanding() {
        var since = Volatile.Read(location: ref m_owedSince);
        var ticks = (Volatile.Read(location: ref m_owedTicks) + ((since == 0) ? 0 : (System.Diagnostics.Stopwatch.GetTimestamp() - since)));
        var seconds = (((double)ticks) / System.Diagnostics.Stopwatch.Frequency);
        var written = (Volatile.Read(location: ref m_meshWordsWritten) * sizeof(uint));

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"region-held={MeshRegionBytes}B region-used={MeshRegionUsedBytes}B written={written}B owed={(Volatile.Read(location: ref m_owedWords) * sizeof(uint))}B owed-frames={Volatile.Read(location: ref m_owedFrames)} owed-seconds={seconds:0.000}s landing-words-per-frame={MeshLandingWords}");
    }

    // One placed mesh's words, from its first vertex word, in the mesh scratch.
    private Span<uint> MeshWordsOf(SdfMesh mesh, SdfMeshRegionMesh placement) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        if (words > m_meshWords.Length) {
            m_meshWords = new uint[Math.Max(val1: words, val2: (2 * m_meshWords.Length))];
        }

        var destination = m_meshWords.AsSpan(length: words, start: 0);

        SdfMeshRegion.WriteMesh(
            destination: destination,
            mesh: mesh,
            placement: new SdfMeshRegionMesh(
                IndexCount: placement.IndexCount,
                IndexWord: (placement.IndexWord - placement.VertexWord),
                MaterialWord: (placement.MaterialWord - placement.VertexWord),
                VertexWord: 0,
                SkinWord: ((placement.SkinWord == 0) ? 0 : (placement.SkinWord - placement.VertexWord))
            )
        );

        return destination;
    }
    // Hands the mesh region's words past the draw reserve to the meshes the arena places there, once the region holds the
    // planned words: the host copy keeps the draw records alone.
    private void ShadowMeshRecords() =>
        m_meshRegion.Shadow(
            shadowBytes: Math.Clamp(
                max: m_meshRegion.ByteCount,
                min: SdfMeshRegion.DrawBytes,
                value: (((long)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes)
            ),
            source: (m_meshSection ??= new MeshSection(tables: this))
        );
    // Writes the mesh region's words [word, word + count), past the draw reserve, into a staging buffer: each placed mesh's
    // words where the arena placed it, and zeros in the words no placed mesh holds, which no draw reads.
    private void WriteSourcedMeshWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
        if (m_meshOrderStale) {
            m_meshOrder.Clear();
            foreach (var (mesh, placement) in m_meshArena.Placements) { m_meshOrder.Add(item: (placement.VertexWord, mesh)); }
            m_meshOrder.Sort(comparison: static (left, right) => left.Word.CompareTo(value: right.Word));
            m_meshOrderStale = false;
        }

        var end = (((long)word) + count);
        var cursor = ((long)word);
        var low = 0;
        var high = m_meshOrder.Count;

        // The first placed mesh that ends past word.
        while (low < high) {
            var middle = ((low + high) >>> 1);

            var (start, mesh) = m_meshOrder[middle];

            if ((((long)start) + SdfMeshRegion.WordsOf(mesh: mesh)) <= word) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }
        for (var index = low; ((index < m_meshOrder.Count) && (m_meshOrder[index].Word < end)); index++) {
            var (start, mesh) = m_meshOrder[index];
            var words = MeshWordsOf(mesh: mesh, placement: m_meshArena.Placements[mesh]);
            var first = Math.Max(val1: cursor, val2: start);
            var last = Math.Min(val1: end, val2: (((long)start) + words.Length));

            WriteZeroWords(buffer: buffer, count: (first - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
            buffer.Write<uint>(
                data: words.Slice(length: ((int)(last - first)), start: ((int)(first - start))),
                destinationOffsetBytes: (destinationOffsetBytes + ((ulong)((first - word) * sizeof(uint))))
            );
            cursor = last;
        }
        WriteZeroWords(buffer: buffer, count: (end - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
    }
    private static void WriteZeroWords(IGpuStorageBuffer buffer, long count, ulong destination) {
        while (count > 0) {
            var piece = ((int)Math.Min(val1: count, val2: ZeroWords.Length));

            buffer.Write<uint>(data: ZeroWords.AsSpan(length: piece, start: 0), destinationOffsetBytes: destination);
            destination += (((ulong)piece) * sizeof(uint));
            count -= piece;
        }
    }

    // The mesh section's words as the region's source sees them.
    private sealed class MeshSection(SdfWorldTables tables) : IGpuRegionSource {
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) =>
            tables.WriteSourcedMeshWords(buffer: buffer, count: count, destinationOffsetBytes: destinationOffsetBytes, word: word);
    }

    // Grows the region to MeshRegionGrownBytes of the need, refusing by name past what a region holds, without waiting for
    // the device (GpuRegion.Grow): the next upload clears the new buffer and copies the words the old one held into it on
    // the GPU, so every placed mesh stands where it was written and nothing it held is staged again, and the old buffer is
    // disposed once that upload completes (Retire). The region keeps its reserved copy sets, so growing takes no
    // descriptor range.
    // Shrinks the region to MeshRegionGrownBytes of the need when a relocation leaves it holding more than half as much
    // again, so the meshes no list names any more give back their device-local, staging and host-copy bytes. A
    // relocation writes every word anyway, so the shrunk region, which owes every word, costs that upload nothing more.
    private void FitMeshRegion(ulong bytes) {
        var fitted = MeshRegionGrownBytes(needBytes: bytes);

        if (((ulong)m_meshRegion.ByteCount) <= (fitted + (fitted / 2UL))) {
            return;
        }

        RequireMeshUpload();
        Retire(replaced: m_meshRegion.Shrink(byteCount: ((long)fitted)));
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: fitted
        );
    }
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            return;
        }

        var grown = MeshRegionGrownBytes(needBytes: bytes);

        RequireMeshUpload();
        Retire(replaced: m_meshRegion.Grow(byteCount: ((long)grown)));
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
