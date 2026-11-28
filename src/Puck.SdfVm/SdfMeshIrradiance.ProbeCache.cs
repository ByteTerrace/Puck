using Puck.Abstractions;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // One level's probes by cell: tables of native arenas (no object per key) that readers walk without a lock under a
    // hold of the volume's reader epochs, and that the volume's gate serializes every write to.
    //
    // Invariant: no hold of the gate visits more than a bounded run of a table's slots, so a frame's staging never waits
    // on a whole-table scan. Enforced by the cache's shape: no method copies, retains or releases a table in one hold.
    // A growth (TrySet) or a retention (BeginRetain) publishes its successor at once, linked to the table it replaces
    // (Table.Older), and the volume's upkeep moves the older table's entries into the newest one StepSlots slots a hold
    // (Step); a table unlinked from the chain (drained, or cleared) releases the blocks its unmoved entries name the same
    // way before it retires. The tables a growth or a retention publishes are allocated outside the gate (Upkeep reserves
    // the next growth once the newest table's arena is three quarters full); a write that finds none reserved, a level's
    // first write among them, allocates one under the gate, which CacheTablesUnreserved counts.
    //
    // Readers walk the chain newest first and take the first entry of their key. Every write goes to the newest table,
    // and an older entry moves only where no newer table holds its key, so a newer table always holds a value as new as
    // any older one's. A moved entry stays readable in the older table (its used flag reads Moved) under the block
    // references the move handed over: a reader that missed the newer copy reads the same value. Every live entry names
    // its blocks once (ProbeStorage); a write names the new value's blocks before it releases the old value's, and an
    // unmoved entry of an unlinked table is released only after the unlink, so a block returns to the pool only after
    // every reader that could have taken it has closed its hold. A table retires to the same epochs once its entries
    // are released, and comes back as the cache's spare once no hold that could read it is open.
    private sealed class ProbeCache(ReaderEpochs<Reclaimable> blocks, IAllocator allocator, WorkCounterSet counters) {
        private const int InitialCapacity = (1 << 16);
        // The slots one hold of the gate visits at most (Step).
        public const int StepSlots = 2048;
        // The holds one round of the upkeep takes at most (Upkeep).
        private const int StepsPerRound = 16;
        // A table slot's used flag: free, a live entry, or an entry moved into a newer table.
        private const int Free = 0;
        private const int Live = 1;
        private const int Moved = 2;

        private Table? m_table;
        private Table? m_spare;
        // The table allocated outside the gate for the newest table's next growth.
        private Table? m_reserved;
        private int m_capacity = InitialCapacity;
        // The tables no chain names whose unmoved entries still name blocks, released StepSlots slots a hold, oldest first.
        private readonly Queue<Table> m_unlinked = new();
        // The box the latest window move keeps (AskRetain), and whether its retention is still to begin.
        private bool m_retainOwed;
        private (int X, int Y, int Z) m_retainLow;
        private (int X, int Y, int Z) m_retainHigh;
        // Set once the volume is released: a table the epochs hand back is freed rather than kept as the spare.
        private volatile bool m_closed;

        // Whether the cache owes the upkeep any work: a retention to begin, a growth to reserve, a drain or a release.
        // The volume gate is held.
        public bool Owes => (m_retainOwed || (m_table?.Older is not null) || (m_unlinked.Count > 0) || GrowthDue(capacity: out _, entries: out _));

        // A table of the size asked for: the spare, emptied, when it has that size; a spare of another size is freed. Takes
        // no gate: the spare moves by exchange.
        private Table NewTable(int capacity, int entries = 0) {
            var spare = Interlocked.Exchange(location1: ref m_spare, value: null);
            var arena = ((entries == 0) ? (capacity / 2) : entries);

            if ((spare is not null) && (spare.Capacity == capacity) && (spare.Values.Capacity == arena)) {
                spare.Reset();
                return spare;
            }
            spare?.Dispose();
            return new Table(allocator: allocator, capacity: capacity, owner: this, entries: entries);
        }
        // Keeps a table no reader can reach (handed back by the epochs, or never published) as the spare, freeing the spare
        // it replaces, or frees it once the volume is released.
        private void Recycle(Table table) {
            if (m_closed) {
                table.Dispose();
                return;
            }
            Interlocked.Exchange(location1: ref m_spare, value: table)?.Dispose();
        }
        // Frees the spare and the reserved table, and every table the epochs hand back from now on; the volume's release
        // calls it under the gate after Clear and Settle.
        public void Close() {
            m_closed = true;
            Interlocked.Exchange(location1: ref m_spare, value: null)?.Dispose();
            m_reserved?.Dispose();
            m_reserved = null;
        }

        // The probes cached: each table's entries less those it moved into a newer one. Any thread; a read beside a write
        // is approximate.
        public int Count {
            get {
                var count = 0;

                for (var table = Volatile.Read(location: ref m_table); (table is not null); table = Volatile.Read(location: ref table.Older)) { count += (table.Count - table.Moved); }
                return count;
            }
        }

        // The bytes of every table the cache holds (the chain, the reserved table and the unlinked tables): its arena
        // machinery, keys and inline storage, and the newest table's capacity. The blocks the entries name are the
        // volume's running figure (ProbeStorage.Charge). The volume gate is held.
        public (long Table, int Capacity) Bytes() {
            var bytes = 32L;

            for (var table = m_table; (table is not null); table = table.Older) { bytes += table.Bytes; }
            if (m_reserved is { } reserved) { bytes += reserved.Bytes; }
            foreach (var unlinked in m_unlinked) { bytes += unlinked.Bytes; }
            return (bytes, (m_table?.Capacity ?? 0));
        }

        // Forgets every probe: the chain is unlinked at once, and its tables release their entries' blocks through the
        // upkeep (Step); the next write starts a table of the capacity the cache last reached. The volume gate is held.
        public void Clear() {
            m_retainOwed = false;
            if (m_table is not { } table) { return; }
            m_capacity = table.Capacity;
            Volatile.Write(location: ref m_table, value: null);
            for (var unlinked = table; (unlinked is not null); unlinked = unlinked.Older) {
                unlinked.Cursor = 0;
                m_unlinked.Enqueue(item: unlinked);
            }
        }
        // Runs every step the cache owes in this hold, for the volume's release alone. The volume gate is held.
        public void Settle() {
            while (Step(budget: int.MaxValue) > 0) { }
        }

        // Asks that the probes outside a box of cells, from low inclusive to high exclusive, be forgotten once the cache
        // holds more probes than the box has cells; the upkeep begins it (Upkeep). The volume gate is held.
        public void AskRetain((int X, int Y, int Z) low, (int X, int Y, int Z) high) {
            m_retainOwed = true;
            m_retainLow = low;
            m_retainHigh = high;
        }

        // One round of the upkeep's work for this cache, on the volume's upkeep task: a retention's table and the next
        // growth's are allocated outside the gate and published under it, then the drains and releases run StepSlots
        // slots a hold, at most StepsPerRound holds, so a refill owed meanwhile waits no longer than that.
        public void Upkeep(Lock gate) {
            int capacity;
            int entries;
            bool due;

            lock (gate) { due = RetentionDue(capacity: out capacity, entries: out entries); }
            if (due) {
                var table = NewTable(capacity: capacity, entries: entries);

                lock (gate) { if (!BeginRetain(table: table)) { Recycle(table: table); } }
            }
            lock (gate) { due = GrowthDue(capacity: out capacity, entries: out entries); }
            if (due) {
                var table = NewTable(capacity: capacity, entries: entries);

                lock (gate) { if (!Reserve(table: table)) { Recycle(table: table); } }
            }
            for (var step = 0; (step < StepsPerRound); step++) {
                int visited;

                lock (gate) { visited = Step(budget: StepSlots); }
                if (visited == 0) { return; }
                counters.Add(amount: visited, kind: CacheSlots);
                counters.Count(kind: CacheSteps);
            }
        }

        // Whether a retention may begin now, and its table's size: the box's cells, plus one, in the arena. One waits while
        // a drain stands. The volume gate is held.
        private bool RetentionDue(out int capacity, out int entries) {
            capacity = 0;
            entries = 0;
            if (!m_retainOwed) { return false; }
            if (m_table is not { } table) {
                m_retainOwed = false;
                return false;
            }
            if (table.Older is not null) { return false; }

            var cells = ((((long)(m_retainHigh.X - m_retainLow.X)) * (m_retainHigh.Y - m_retainLow.Y)) * (m_retainHigh.Z - m_retainLow.Z));

            if (table.Count <= cells) {
                m_retainOwed = false;
                return false;
            }
            // Reserve the incoming window's cells even after a teleport keeps few or no old probes: an arena sized from
            // the kept cells alone would grow again at once while the window refills.
            entries = checked(((int)cells) + 1);
            capacity = Math.Max(val1: InitialCapacity, val2: checked((int)(((ulong)entries) * 2UL).NextPowerOfTwo()));
            return true;
        }
        // Publishes a retention's table, linked to the table it drains, which keeps only the entries inside the box; false,
        // publishing nothing, when the retention is no longer due at that size. The volume gate is held.
        private bool BeginRetain(Table table) {
            if (!RetentionDue(capacity: out var capacity, entries: out var entries) || (table.Capacity != capacity) || (table.Values.Capacity != entries)) { return false; }

            var current = m_table!;

            current.Filtered = true;
            current.KeepLow = m_retainLow;
            current.KeepHigh = m_retainHigh;
            current.Cursor = 0;
            table.Older = current;
            m_capacity = capacity;
            m_retainOwed = false;
            Volatile.Write(location: ref m_table, value: table);
            return true;
        }
        // Whether the newest table's next growth wants a table reserved, and its size, once the newest arena is three
        // quarters full. A level's first table is taken at its first write (TrySet), so a level no window measures holds
        // none. The volume gate is held.
        private bool GrowthDue(out int capacity, out int entries) {
            capacity = 0;
            entries = 0;
            if (m_closed || (m_reserved is not null) || (m_table is not { } table)) { return false; }
            if ((((long)table.Count) * 4L) < (((long)table.Values.Capacity) * 3L)) { return false; }
            GrowthOf(table: table, capacity: out capacity, entries: out entries);
            return true;
        }
        // The size of the table a full table grows into: twice the hash table once a full arena would leave it more than
        // half full, and twice the arena within half the hash table.
        private static void GrowthOf(Table table, out int capacity, out int entries) {
            capacity = ((((table.Values.Capacity + 1) * 2) > table.Capacity) ? (table.Capacity * 2) : table.Capacity);
            entries = Math.Min(val1: (capacity / 2), val2: (table.Values.Capacity * 2));
        }
        // Keeps a table allocated outside the gate for the next growth; false when it no longer fits it. The volume gate
        // is held.
        private bool Reserve(Table table) {
            if (!GrowthDue(capacity: out var capacity, entries: out var entries) || (table.Capacity != capacity) || (table.Values.Capacity != entries)) { return false; }
            m_reserved = table;
            return true;
        }
        // The reserved table when it has the size asked for, else one allocated here, under the gate. The volume gate is
        // held.
        private Table TakeTable(int capacity, int entries) {
            if ((m_reserved is { } reserved) && (reserved.Capacity == capacity) && (reserved.Values.Capacity == entries)) {
                m_reserved = null;
                return reserved;
            }
            counters.Count(kind: CacheTablesUnreserved);
            return NewTable(capacity: capacity, entries: entries);
        }

        private static bool Inside((int X, int Y, int Z) key, (int X, int Y, int Z) low, (int X, int Y, int Z) high) =>
            ((key.X >= low.X) && (key.X < high.X) && (key.Y >= low.Y) && (key.Y < high.Y) && (key.Z >= low.Z) && (key.Z < high.Z));

        public bool ContainsKey((int X, int Y, int Z) key) {
            for (var table = Volatile.Read(location: ref m_table); (table is not null); table = Volatile.Read(location: ref table.Older)) {
                if (Find(key: key, table: table) >= 0) { return true; }
            }
            return false;
        }
        // A read of a probe's blocks is good for as long as the hold it was taken under stays open.
        public bool TryGetValue((int X, int Y, int Z) key, out ProbeData value, in ReaderEpochs<Reclaimable>.Hold hold) {
            if (!hold.IsOpen) { throw new InvalidOperationException(message: "A probe cache read needs an open hold of the volume's reader epochs."); }
            for (var table = Volatile.Read(location: ref m_table); (table is not null); table = Volatile.Read(location: ref table.Older)) {
                var slot = Find(key: key, table: table);

                if (slot >= 0) { return table.Values.TrySnapshot(entry: table.Entries[slot], value: out value); }
            }
            value = default;
            return false;
        }

        // Writes a cell's entry into the newest table, naming its blocks first; false, writing nothing, when a block it
        // names was already retired, which a landing whose window stood never meets. An older table's entry of the key is
        // left to its drain, which drops it. The volume's gate is held.
        public bool TrySet((int X, int Y, int Z) key, in ProbeData value) {
            if (!ProbeStorage.TryAddReferences(epochs: blocks, probe: in value)) { return false; }
            var table = m_table;

            if (table is null) {
                table = TakeTable(capacity: m_capacity, entries: (m_capacity / 2));
                Volatile.Write(location: ref m_table, value: table);
            }

            var slot = Find(key: key, table: table);

            if (slot >= 0) {
                var held = table.Values.TrySnapshot(entry: table.Entries[slot], value: out var previous);

                table.Values.Publish(entry: table.Entries[slot], value: value);
                if (held) { ProbeStorage.Release(epochs: blocks, probe: in previous); }

                return true;
            }
            if ((table.Count + 1) > table.Values.Capacity) {
                table = Grow(table: table);
                slot = Find(key: key, table: table);
            }

            Insert(key: key, slot: ~slot, table: table, value: value);
            return true;
        }
        // Publishes the table a full newest table grows into, linked to it, so its entries move a step at a time. The
        // volume gate is held.
        private Table Grow(Table table) {
            GrowthOf(table: table, capacity: out var capacity, entries: out var entries);

            var grown = TakeTable(capacity: capacity, entries: entries);

            table.Cursor = 0;
            grown.Older = table;
            Volatile.Write(location: ref m_table, value: grown);
            return grown;
        }

        // Visits up to a budget of slots: first the oldest drained table's (unlinking it once every slot is visited), then
        // the unlinked tables' (retiring each once its entries are released). Returns the slots visited, zero when the
        // cache owes no step. The volume gate is held.
        private int Step(int budget) {
            var visited = 0;
            var retired = false;

            while (visited < budget) {
                if (OldestDrained(newer: out var newer) is not { } oldest) { break; }
                visited += Drain(budget: (budget - visited), table: oldest);
                if (oldest.Cursor < oldest.Capacity) { break; }
                Volatile.Write(location: ref newer!.Older, value: null);
                oldest.Cursor = 0;
                m_unlinked.Enqueue(item: oldest);
            }
            while ((visited < budget) && m_unlinked.TryPeek(result: out var unlinked)) {
                visited += ReleaseUnmoved(budget: (budget - visited), table: unlinked);
                if (unlinked.Cursor < unlinked.Capacity) { break; }
                _ = m_unlinked.Dequeue();
                blocks.Retire(item: unlinked);
                retired = true;
            }
            // A table no hold can read any more becomes the spare at once.
            if (retired) { _ = blocks.Reclaim(release: static item => item.Reclaim()); }
            return visited;
        }
        // The oldest table of the chain that drains into a newer one, and the table that links it; null when the chain
        // holds one table or none.
        private Table? OldestDrained(out Table? newer) {
            newer = m_table;
            if (newer?.Older is not { } oldest) { return null; }
            while (oldest.Older is { } older) {
                newer = oldest;
                oldest = older;
            }
            return oldest;
        }
        // Moves a drained table's live entries into the newest table from its cursor on, up to a budget of slots: an entry
        // a newer table holds the key of, or one outside a retention's box, stays unmoved for the release. Returns the
        // slots visited.
        private int Drain(Table table, int budget) {
            var visited = 0;

            for (; ((table.Cursor < table.Capacity) && (visited < budget)); table.Cursor++, visited++) {
                var slot = table.Cursor;

                if (table.Used[slot] != Live) { continue; }

                var key = table.Keys[slot];

                if (table.Filtered && !Inside(high: table.KeepHigh, key: key, low: table.KeepLow)) { continue; }
                if (NewerHolds(key: key, table: table) || !table.Values.TrySnapshot(entry: table.Entries[slot], value: out var value)) { continue; }

                var newest = m_table!;

                if ((newest.Count + 1) > newest.Values.Capacity) { newest = Grow(table: newest); }
                Insert(key: key, slot: ~Find(key: key, table: newest), table: newest, value: value);
                Volatile.Write(location: ref table.Used[slot], value: Moved);
                table.Moved++;
            }
            return visited;
        }
        // Whether a table newer than the one given holds the key.
        private bool NewerHolds((int X, int Y, int Z) key, Table table) {
            for (var newer = m_table; ((newer is not null) && !ReferenceEquals(objA: newer, objB: table)); newer = newer.Older) {
                if (Find(key: key, table: newer) >= 0) { return true; }
            }
            return false;
        }
        // Releases the blocks of an unlinked table's unmoved entries from its cursor on, up to a budget of slots. Returns
        // the slots visited.
        private int ReleaseUnmoved(Table table, int budget) {
            var visited = 0;

            for (; ((table.Cursor < table.Capacity) && (visited < budget)); table.Cursor++, visited++) {
                var slot = table.Cursor;

                if ((table.Used[slot] == Live) && table.Values.TrySnapshot(entry: table.Entries[slot], value: out var value)) { ProbeStorage.Release(epochs: blocks, probe: in value); }
            }
            return visited;
        }

        private static void Insert(Table table, int slot, (int X, int Y, int Z) key, in ProbeData value) {
            table.Values.Publish(entry: table.Count, value: value);
            table.Entries[slot] = table.Count;
            table.Keys[slot] = key;
            Volatile.Write(location: ref table.Used[slot], value: Live);
            table.Count++;
        }
        // The slot holding a key, or the complement of the free slot it would take: linear probing from the key's hash,
        // over a table never more than half full. A moved entry still holds its slot.
        private static int Find((int X, int Y, int Z) key, Table table) {
            var used = table.Used;
            var keys = table.Keys;
            var mask = (keys.Length - 1);

            for (var slot = Hash(key: key) & mask; ; slot = (slot + 1) & mask) {
                if (Volatile.Read(location: ref used[slot]) == Free) { return ~slot; }
                if (keys[slot] == key) { return slot; }
            }
        }
        private static int Hash((int X, int Y, int Z) key) => unchecked(((key.X * 73856093) ^ (key.Y * 19349663)) ^ (key.Z * 83492791));

        // A table's keys, used flags, dense-entry indices and entry arena, all native: read under a hold while a chain
        // names the table or a reader that entered before its retirement may still read it, and freed (Dispose) only once
        // the epochs handed it back (Reclaim) and the cache keeps it no longer as its spare. The cursor, the moved count,
        // the retention's box and the link to the older table it drains are the gate's.
        private sealed class Table : Reclaimable, IDisposable {
            private readonly ProbeCache m_owner;
            private readonly OwnedBuffer<(int X, int Y, int Z)> m_keys;
            private readonly OwnedBuffer<int> m_used;
            private readonly OwnedBuffer<int> m_entries;

            public Table(IAllocator allocator, int capacity, ProbeCache owner, int entries = 0) {
                m_owner = owner;
                m_keys = OwnedBuffer<(int X, int Y, int Z)>.OfLength(allocator: allocator, clear: true, length: capacity);
                m_used = OwnedBuffer<int>.OfLength(allocator: allocator, clear: true, length: capacity);
                m_entries = OwnedBuffer<int>.OfLength(allocator: allocator, clear: true, length: capacity);
                Values = new PublishArena<ProbeData>(allocator: allocator, capacity: ((entries == 0) ? (capacity / 2) : entries));
                Capacity = capacity;
            }

            public int Capacity { get; }
            public Span<(int X, int Y, int Z)> Keys => m_keys.GetSpan();
            public Span<int> Used => m_used.GetSpan();
            public Span<int> Entries => m_entries.GetSpan();
            public PublishArena<ProbeData> Values { get; }
            // x64: table 56, three buffer owners 72, plus each key, used flag and dense-entry index, and the arena.
            public long Bytes => ((128L + (((long)Capacity) * (System.Runtime.CompilerServices.Unsafe.SizeOf<(int, int, int)>() + (2 * sizeof(int))))) + Values.InlineStorageBytes);

            public int Count;
            // The older table this one drains, which readers walk on to; null once every entry of it has been visited.
            public Table? Older;
            // The next slot a drain or a release visits, the entries moved into a newer table, and the box a retention
            // keeps (Filtered), when this table is drained.
            public int Cursor;
            public int Moved;
            public bool Filtered;
            public (int X, int Y, int Z) KeepLow;
            public (int X, int Y, int Z) KeepHigh;

            // Empties the table for its cache to publish again; no reader can reach it (it came back reclaimed, or was never
            // published).
            public void Reset() {
                Used.Clear();
                Values.Reset();
                Count = 0;
                Older = null;
                Cursor = 0;
                Moved = 0;
                Filtered = false;
            }
            public override void Reclaim() => m_owner.Recycle(table: this);
            public void Dispose() {
                Values.Dispose();
                m_keys.Dispose();
                m_used.Dispose();
                m_entries.Dispose();
            }
        }
    }
}
