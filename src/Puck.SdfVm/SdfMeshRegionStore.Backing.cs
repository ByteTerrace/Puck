using Puck.Abstractions.Gpu;
using Puck.Maths;

namespace Puck.SdfVm;

// The region's device memory: its limits from the grant before a plan, its growth to what a plan placed, and, for a
// mapped region, the chunks its placements cover.
public sealed partial class SdfMeshRegionStore {
    // Reserves room for the region to grow before a plan places meshes, and holds the arena to what was reserved, so no
    // mesh is placed in words the region could not be grown to hold. A mapped region's grant reserves the chunks it
    // backs; the chunks inside its size that an earlier fit gave back (Cover) hold no device bytes, so they widen the
    // words the arena may place without widening the reservation, and backing one again is admitted by the ledger when a
    // placement needs it. While a refused growth holds (HoldsRefusedGrowth), the arena is held to what the region holds
    // instead, and the grant presses the bakes' budgets to it, so no plan places a mesh the region was just refused
    // room for.
    private void PrepareLimit() {
        if (HoldsRefusedGrowth()) {
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.RefusalHolds);
            SetArenaLimit(limitBytes: m_grant.HoldStanding(limitBytes: m_region.ByteCount));
            return;
        }

        var held = ((long)m_region.DestinationBytes);
        var holes = (m_region.Mapped ? Math.Max(val1: 0L, val2: ((m_region.ByteCount.CeilingDivide(divisor: m_region.MappedChunkBytes) * m_region.MappedChunkBytes) - held)) : 0L);

        SetArenaLimit(limitBytes: m_grant.ReserveGrowth(capacityBytes: held, mostBytes: ((long)MostBytes), spareBytes: m_region.MappedSpareBytes, unbackedBytes: holes));
    }
    // Hands the arena the grant's two limits: the address range it may place meshes in, and the bytes the region may
    // hold, which is the residencies' share of the elastic budget and can be less than the region holds now.
    private void SetArenaLimit(long limitBytes) {
        var held = m_grant.HeldLimitBytes;

        m_arena.SetLimit(heldBytes: ((held == long.MaxValue) ? null : ((ulong)held)), limitBytes: ((ulong)limitBytes));
    }
    // A growth the account or the device refused (EnsureCapacity): the bytes the region held then, the pool's room and
    // the grant's releases at the refusal, and the plans prepared since. Asking again for room just refused costs a
    // refused allocation, a clip and the meshes' placements every plan, so the region holds what it has until something
    // that could make room happens: the region changed size, a replaced buffer was released, the pool's room grew, a
    // trim ran, or RefusedGrowthPlans plans passed (memory another owner gave back reaches no event here).
    private long m_refusedGrowthBytes = -1L;
    private long m_refusedGrowthRoom;
    private long m_refusedGrowthReleases;
    private int m_refusedGrowthPlans;
    private const int RefusedGrowthPlans = 240;

    private void RefuseGrowth(long heldBytes) {
        m_refusedGrowthBytes = heldBytes;
        m_refusedGrowthRoom = m_grant.Room;
        m_refusedGrowthReleases = m_grant.Releases;
        m_refusedGrowthPlans = 0;
    }
    private bool HoldsRefusedGrowth() {
        if (m_refusedGrowthBytes < 0L) { return false; }
        if ((m_region.ByteCount != m_refusedGrowthBytes) || (m_grant.Releases != m_refusedGrowthReleases) ||
            (++m_refusedGrowthPlans > RefusedGrowthPlans) || (m_grant.Room > m_refusedGrowthRoom)) {
            m_refusedGrowthBytes = -1L;
            return false;
        }
        return true;
    }

    // A region buffer a growth or a shrink replaced, which leaves the grant's reservation when its readers finish and
    // it is released.
    private sealed class ReplacedRegion(IDisposable replaced, long bytes, SdfResidencyGrant grant) : IDisposable {
        public void Dispose() {
            replaced.Dispose();
            grant.Released(bytes: bytes);
        }
    }

    /// <summary>Returns the size the region grows to when its meshes need <paramref name="needBytes"/>: for a region that
    /// grows by replacement, the need plus an eighth rounded up to <see cref="GranuleBytes"/>; for a mapped one
    /// (<see cref="GpuRegion.Mapped"/>), whose growth copies nothing, the need alone rounded up to its chunk. Either is
    /// capped at <paramref name="mostBytes"/>, never below the need and a whole number of words.</summary>
    /// <param name="needBytes">The bytes the placed meshes use; at most <paramref name="mostBytes"/>.</param>
    /// <param name="mostBytes">The most bytes the region may hold, a whole number of words; at most
    /// <see cref="MostBytes"/>.</param>
    /// <param name="mappedChunkBytes">The chunk a mapped region backs (<see cref="GpuRegion.MappedChunkBytes"/>), a whole
    /// number of words; zero for a region that grows by replacement.</param>
    /// <returns>The region's new size in bytes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="needBytes"/> is past <paramref name="mostBytes"/>.</exception>
    public static ulong GrownBytes(ulong needBytes, ulong mostBytes = MostBytes, long mappedChunkBytes = 0L) {
        if (needBytes > mostBytes) {
            throw new InvalidOperationException(message: $"The baked meshes need {needBytes} bytes of mesh region, past the {mostBytes} the region may hold.");
        }

        return ((ulong)((mappedChunkBytes > 0L)
            ? GpuCapacity.Headroom(ceiling: ((long)mostBytes), divisor: long.MaxValue, granule: mappedChunkBytes, required: ((long)needBytes))
            : GpuCapacity.Headroom(ceiling: ((long)mostBytes), divisor: 8L, granule: ((long)GranuleBytes), required: ((long)needBytes))));
    }

    // Shrinks the region to GrownBytes of the need: a mapped region whenever its size passes the need, giving its chunks
    // past it back behind their readers with nothing allocated, so a full pool gets its bytes back; any other only when
    // it holds more than half as much again and the pool has room for the smaller buffer beside it, which carries the
    // words below its size on the GPU.
    private void Fit(ulong bytes) {
        var fitted = GrownBytes(mappedChunkBytes: m_region.MappedChunkBytes, mostBytes: Math.Max(val1: bytes, val2: ((ulong)(m_arena.LimitWords * sizeof(uint)))), needBytes: Math.Max(val1: bytes, val2: sizeof(uint)));
        var held = m_region.ByteCount;

        if (m_region.Mapped) {
            if (((ulong)held) <= fitted) { return; }
            Replaced(newBytes: fitted, replaced: m_region.Shrink(byteCount: ((long)fitted)));
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Shrinks);

            return;
        }
        // A shrink holds both buffers until the old one's readers finish: one the pool has no room for waits for a
        // later plan, and the region keeps what it holds.
        if ((((ulong)held) <= (fitted + (fitted / 2UL))) || !m_grant.TryReserveReplacement(newBytes: ((long)fitted))) { return; }

        IDisposable replaced;

        try {
            replaced = m_region.Shrink(byteCount: ((long)fitted));
        } catch (GpuMemoryRefusedException) {
            m_grant.Kept(refused: true);

            return;
        }

        // The replacement carries every word below its size on the GPU (GpuRegion.Shrink), so each placed mesh stands
        // where it was written; its page lease follows the new buffer (RefreshPages).
        Replaced(newBytes: fitted, replaced: replaced);
        SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Shrinks);
    }
    // The placement version the mapped region's chunks were last fitted to, and the size and chunk they were fitted at.
    private long m_coveredVersion = -1L;
    private long m_coveredBytes = -1L;
    private long m_coveredChunk = -1L;
    // The chunks a fit visits: those the arena's counts crossed since the last fit, and those a fit could not back.
    private readonly List<int> m_coverCandidates = [];
    private readonly List<int> m_refusedChunks = [];
    // Whether the latest fit left a covered chunk unbacked because the device refused it, which the fit is tried again
    // for at every service and a mesh named ahead in such a chunk is refused ahead for (RefusesAhead).
    private bool m_backingRefused;

    // Fits a mapped region's chunks to its placements, the one place its interior backing follows the arena: a chunk
    // that holds a placed mesh's words is backed, and one that holds none is given back behind its readers, so a
    // forgotten mesh's device bytes leave the pool wherever it lay, not only at the region's end.
    // Invariant: every word a draw or a landing may name lies in a backed chunk. The arena places a mesh only inside the
    // region's size, which EnsureCapacity backs as it grows; this restores the backing of an interior chunk an earlier
    // fit gave back before any landing leases its words (a landing leases only backed pages, GpuRegion.TryLease). A chunk
    // the device refuses to back leaves the meshes there owed, their rows absent: the fit then forgets the meshes no
    // name holds, whose chunks go back, and is tried again at every service until it backs them, since the chunks it
    // gave back leave the pool only once their readers retire.
    private void Cover() {
        if (!m_region.Mapped || (m_coveredVersion == m_version)) { return; }
        if (!TryCover() && ForgetUnused()) { _ = TryCover(); }
    }
    // One fit of the chunks to the placements, giving back every chunk no placement covers before backing any it now
    // covers; false when the device refused a chunk. A fit visits only the chunks whose count crossed zero since the
    // last fit and those it could not back, so it costs what the placements changed; a new size or chunk, or counts
    // taken afresh, visits every chunk once.
    private bool TryCover() {
        m_coveredVersion = m_version;

        var chunk = m_region.MappedChunkBytes;
        var size = m_region.ByteCount;
        var chunks = ((int)size.CeilingDivide(divisor: chunk));
        var refused = false;
        var candidates = m_coverCandidates;

        m_arena.TrackChunks(chunkWords: (chunk / sizeof(uint)));
        candidates.Clear();
        var whole = (m_arena.ChunksRecounted || (m_coveredBytes < 0L) || (m_coveredChunk != chunk));

        m_arena.TakeCrossedChunks(crossed: candidates);
        if (whole) {
            candidates.Clear();
            for (var index = 0; (index < chunks); index++) { candidates.Add(item: index); }
        } else {
            candidates.AddRange(collection: m_refusedChunks);
            // A new size changes only the chunks past the shorter size, and the last chunk of either, which may turn
            // whole or partial: a growth backs its new chunks whether a placement covers them or not.
            if (m_coveredBytes != size) {
                var held = ((int)m_coveredBytes.CeilingDivide(divisor: chunk));

                for (var index = Math.Max(val1: 0, val2: (Math.Min(val1: held, val2: chunks) - 1)); (index < chunks); index++) { candidates.Add(item: index); }
            }
        }
        m_refusedChunks.Clear();
        m_coveredBytes = size;
        m_coveredChunk = chunk;
        SdfRowChecks.ProcessWork.Add(kind: SdfRowChecks.CoverChunks, amount: candidates.Count);
        foreach (var index in candidates) {
            if ((index >= chunks) || Covered(index: index)) { continue; }
            var offset = (index * chunk);
            var bytes = Math.Min(val1: chunk, val2: (size - offset));

            if ((bytes == chunk) && m_region.IsBacked(bytes: bytes, offset: offset)) {
                Replaced(newBytes: ((ulong)size), replaced: m_region.UnmapRange(bytes: bytes, offset: offset));
            }
        }
        foreach (var index in candidates) {
            if ((index >= chunks) || !Covered(index: index)) { continue; }
            var offset = (index * chunk);
            var bytes = Math.Min(val1: chunk, val2: (size - offset));

            if (m_region.IsBacked(bytes: bytes, offset: offset)) { continue; }
            try {
                m_region.MapRange(bytes: bytes, offset: offset);
            } catch (GpuMemoryRefusedException) {
                m_grant.Kept(refused: true);
                refused = true;
                m_refusedChunks.Add(item: index);
                continue;
            }
            Replaced(newBytes: ((ulong)size), replaced: GpuMappedRelease.None);
        }
        if (refused) { m_coveredVersion = -1L; }
        m_backingRefused = refused;
        return !refused;

        // The first chunk stays backed, so the buffer every set binds always holds memory.
        bool Covered(int index) => ((index == 0) || (m_arena.ChunkCount(chunk: index) > 0));
    }
    // The region changed size. The device bytes it gave up (a replaced buffer, or a mapped region's chunks past its new
    // size) wait behind every submission that may read them and leave the grant's reservation when they are released.
    // A replaced buffer is carried by the next recorded copy and every residency binds the new one (RefreshPages); a
    // mapped region keeps its buffer, its bindings and its words.
    private void Replaced(IDisposable replaced, ulong newBytes) {
        var released = ((replaced is GpuMappedRelease release) ? ((long)release.Bytes) : (m_region.Mapped ? 0L : m_grant.Capacity));

        m_retired.Hold(item: new ReplacedRegion(bytes: released, grant: m_grant, replaced: replaced), pendingUse: () => (m_region.OwesCarry || (m_unsubmitted > 0)));
        m_grant.Grown(newBytes: ((long)m_region.DestinationBytes), oldBytes: released);
        Volatile.Write(location: ref m_bytes, value: ((long)newBytes));
    }
    // The growth is reserved before the plan that asks for it (PrepareLimit), so the ledger admits the new backing
    // against the grant. A growth refused all the same (the pool had no room for it, or the device said no) leaves the
    // region as it is: the placements past what it holds are taken back and refused (SdfMeshRegionArena.Clip), their
    // draws wait, and nothing throws.
    private void EnsureCapacity(ulong bytes, SdfWorldTables? planner) {
        var current = ((ulong)m_region.ByteCount);

        if (bytes <= current) {
            m_grant.Kept();

            return;
        }

        var grown = GrownBytes(mappedChunkBytes: m_region.MappedChunkBytes, mostBytes: Math.Max(val1: bytes, val2: ((ulong)(m_arena.LimitWords * sizeof(uint)))), needBytes: bytes);
        IDisposable replaced;

        try {
            replaced = m_region.Grow(byteCount: ((long)grown));
        } catch (GpuMemoryRefusedException) {
            m_grant.Kept(refused: true);
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Clips);
            m_arena.Clip(limitBytes: current);
            ForgetArenaMeshes(planner: planner);
            RefuseGrowth(heldBytes: m_region.ByteCount);

            return;
        }

        Replaced(newBytes: grown, replaced: replaced);
    }
}
