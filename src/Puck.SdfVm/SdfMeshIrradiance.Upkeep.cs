using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// The volume's upkeep, two tasks of the thread pool: the refill of the windows a move left holding other cells'
// probes (Refill), which a lighting-converged frame waits for, and the probe caches' retention, growth and release
// (Upkeep), which no frame waits for.
//
// Invariant: no hold of the gate either task takes spans a scan of a window or a table, so a frame's staging, which
// takes the same gate, never waits on one. Enforced here and in ProbeCache: a refill lists its cells outside the gate,
// reading the volume's words under a hold of the reader epochs as the worker's listing does (ListGpu), and writes what
// it composed RefillStepCells cells a hold; a cache's upkeep visits ProbeCache.StepSlots slots a hold and allocates its
// tables outside the gate (ProbeCache.Upkeep).
public sealed partial class SdfMeshIrradiance {
    // The cells one hold of the gate writes at most as a refill commits.
    private const int RefillStepCells = 1024;
    // The tasks' delegates, made once so a window move allocates none, and the caches' upkeep in flight.
    private Action? m_refillLoop;
    private Action? m_upkeep;
    private Task? m_cacheUpkeep;

    // Owes the refill of the windows the volume just moved, and starts it unless it runs. A lighting-converged frame
    // waits for it (AwaitRefill). Under the gate.
    private void OweRefill() {
        m_windowVersion++;
        m_refillOwed = true;
        foreach (var level in m_levels) { level.Unwritten = (m_layout.Measures(level: level.Index) ? m_layout.CountOf(level: level.Index) : 0); }
        if (m_refillRunning || m_disposed) { return; }
        m_refillRunning = true;
        m_refill = Task.Run(action: (m_refillLoop ??= Refill));
    }
    // Starts the caches' upkeep unless it runs, nothing is owed or the volume is released. Under the gate.
    private void OweUpkeep() {
        if (m_upkeepRunning || m_disposed || !CachesOwe()) { return; }
        m_upkeepRunning = true;
        m_cacheUpkeep = Task.Run(action: (m_upkeep ??= Upkeep));
    }
    // Whether any level's probe cache owes the upkeep work. Under the gate.
    private bool CachesOwe() {
        foreach (var level in m_levels) {
            if (level.Cache.Owes) { return true; }
        }
        return false;
    }
    // Refills until no refill is owed. A refill that faults leaves its refill owed, so Settling holds a converging frame,
    // which reports the fault (AwaitRefill); the next window move starts it again.
    private void Refill() {
        try {
            while (true) {
                lock (m_gate) {
                    if (!m_refillOwed || m_disposed) {
                        m_refillRunning = false;
                        return;
                    }
                }
                RefillRound();
            }
        } catch {
            lock (m_gate) { m_refillRunning = false; }
            throw;
        }
    }
    // Runs the caches' rounds until none owes work. A round that faults ends the upkeep, which the next window move or
    // landing starts again.
    private void Upkeep() {
        try {
            while (true) {
                lock (m_gate) {
                    if (m_disposed || !CachesOwe()) {
                        m_upkeepRunning = false;
                        return;
                    }
                }
                foreach (var level in m_levels) { level.Cache.Upkeep(gate: m_gate); }
            }
        } catch {
            lock (m_gate) { m_upkeepRunning = false; }
            throw;
        }
    }
    // One refill of the moved windows. It lists, outside the gate, each window's cells whose slots hold another cell's
    // probe, reading the words the volume published at the listing under a hold of the reader epochs, and composes the
    // cached ones; then it writes them RefillStepCells cells a hold, each only if its slot still lacks its cell, unless a
    // window moved meanwhile, when the next round lists the new windows. A probe that lands in between stores itself and
    // is left as it stands.
    private void RefillRound() {
        int version;
        Layout layout;
        (int X, int Y, int Z)[] lows;
        VolumeWords volume;
        bool device;
        Span<int> unwritten = stackalloc int[SdfMeshIrradianceProbe.Levels];
        Span<int> before = stackalloc int[SdfMeshIrradianceProbe.Levels];

        using (var hold = m_blocks.Enter()) {
            lock (m_gate) {
                if (!m_refillOwed) { return; }
                version = m_windowVersion;
                layout = m_layout;
                lows = Lows();
                volume = m_volume;
                device = (GpuTrace is not null);
                for (var level = 0; (level < m_levels.Length); level++) { before[level] = m_levels[level].Unwritten; }
            }

            var cells = 0L;

            m_refillCells.Clear();
            for (var level = 0; (level < m_levels.Length); level++) {
                unwritten[level] = 0;
                if (!layout.Measures(level: level)) { continue; }

                var low = lows[level];
                var dims = layout.DimsOf(level: level);
                var offset = layout.OffsetOf(level: level);
                var cache = m_levels[level].Cache;

                for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                    for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                        for (var x = low.X; (x < (low.X + dims.X)); x++) {
                            var slot = offset + SlotOf(dims: dims, x: x, y: y, z: z);

                            cells++;
                            if (volume.Holds(slot, (x, y, z))) { continue; }
                            unwritten[level]++;
                            // GPU records are restored by a selected relight and its guarded device commit.
                            if (!device && cache.ContainsKey(key: (x, y, z))) { m_refillCells.Add(item: (level, (x, y, z))); }
                        }
                    }
                }
            }
            Counters.Add(amount: cells, kind: RefillCells);
        }

        // Native, for this round alone: a window that jumps far composes every slot, and the words are written before
        // the round ends, so none of a volume's size stays held between refills.
        using var composedWords = OwnedBuffer<uint>.OfLength(allocator: m_allocator, clear: false, length: (m_refillCells.Count * SdfMeshIrradianceProbe.ProbeWords));
        var composed = ComposeCells(cells: m_refillCells, words: composedWords);
        var index = 0;
        var first = true;

        while (true) {
            lock (m_gate) {
                if (version != m_windowVersion) { return; }
                if (first) {
                    // Within one window version a slot only comes to hold its cell, and only a landing's store (StoreLanded)
                    // and this refill count one off: each landing since the listing wrote a slot the listing counted.
                    for (var level = 0; (level < m_levels.Length); level++) { m_levels[level].Unwritten = (unwritten[level] - (before[level] - m_levels[level].Unwritten)); }
                    first = false;
                }

                var end = Math.Min(val1: composed, val2: (index + RefillStepCells));

                for (; (index < end); index++) {
                    var (level, cell) = m_refillCells[index];

                    if (Holds(cell: cell, level: level)) { continue; }
                    composedWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords))
                        .CopyTo(destination: SlotWords(cell: cell, level: level));
                    m_levels[level].Unwritten--;
                }
                Counters.Count(kind: RefillSteps);
                if (index >= composed) {
                    m_refillOwed = false;
                    return;
                }
            }
        }
    }
}
