using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.State;

// The veil: what a job that plans over an actor's sight reads in place of the cells that actor may not see.
//
// Invariant: while a job installed with a sight reads the arena (its restart, its walk, its landing, its position
// keys), every cell of its key rows the actor may not read holds a value derived only from what the actor may read and
// the job's own seed, and every private draw site it samples is bound to the job's own sampler. Enforced in one place:
// the veil is a journal scope OpenVeil opens beneath every candidate scope before any of those reads and Suspend
// rewinds after them, so the arena a caller reads between steps is the authority's again. The job's input stamp is the
// fold of what the actor may read (FoldSeen), so a change the actor cannot see neither restarts the job nor changes
// what it imagines. The guarantee covers the rows a job's key rows name (the plan's rows and every row its judge's
// rules and score read or write); a read outside them is outside it.
public sealed partial class ArenaSearch {
    private readonly List<CellKey> m_veilKeys = [];
    private readonly List<CellKey> m_veilNames = [];
    private readonly List<int> m_veilPositions = [];
    private readonly HashSet<CellKey> m_veilSeen = [];

    private bool m_veilOpen;
    private int m_veilMark;
    private Dictionary<int, int[]>? m_peerPiles;

    // The seed a job imagines with: its plan's own seed, its name and the stamp of what its actor may see. A position
    // the actor sees anew imagines afresh; a change it cannot see leaves the stamp, and so every imagined value, alone.
    private static ulong ImaginedSeed(Job job) {
        if (job.ImaginedValid && (job.ImaginedStamp == job.Stamp)) {
            return job.Imagined;
        }

        var hash = Fnv1aHash.Create();

        hash.Add(value: job.Plan.DrawSeed);
        hash.Add(value: Fnv1aHash.Compute(values: job.Plan.Name.AsSpan()));
        hash.Add(value: job.Stamp);
        job.Imagined = hash.Value;
        job.ImaginedStamp = job.Stamp;
        job.ImaginedValid = true;

        return job.Imagined;
    }
    private static ulong RowStream(StateRow row) => (Fnv1aHash.Compute(values: row.Name.Value.AsSpan()) & Pcg32XshRr.MaxStream);
    private static ulong CellSeed(ulong seed, StateRow row, long cell) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: seed);
        hash.Add(value: RowStream(row: row));
        hash.Add(value: cell);

        return hash.Value;
    }
    // Opens the veil for a job that plans over a sight, unless one is open or the job reads the authority's arena.
    // Returns false when an imagined write was refused: the veil is rewound and the job is idle until its inputs move.
    private bool OpenVeil(Job job) {
        if ((job.Sight is null) || m_veilOpen) {
            return true;
        }

        m_veilMark = m_arena.BeginScope();
        m_veilOpen = true;
        job.Inserted.Clear();

        var seed = ImaginedSeed(job: job);

        foreach (var ordinal in job.KeyRows) {
            if (TryVeilRow(job: job, ordinal: ordinal, reason: out var reason, seed: seed)) {
                continue;
            }

            CloseVeil();
            if (!job.VeilRefused) {
                job.Faults++;
                m_narrate?.Invoke("state.search", $"[state.search: SearchVeilRefused: '{job.Plan.Name}' cannot imagine what its actor may not read of '{m_arena.Rows[ordinal].Name}': {reason}]");
            }
            job.VeilRefused = true;
            job.Running = false;
            job.Done = true;

            return false;
        }

        return true;
    }
    private void CloseVeil() {
        if (!m_veilOpen) {
            return;
        }

        m_veilOpen = false;
        m_arena.Rewind(mark: m_veilMark);
    }
    private bool TryVeilRow(Job job, int ordinal, ulong seed, out string reason) {
        reason = string.Empty;

        var sight = job.Sight!;
        var layout = m_arena.Layout[ordinal];

        if (layout.HostOwned || !layout.IsStored || layout.IsDerivedBoard || m_arena.Catalog.IsPoolRow(rowOrdinal: ordinal) || sight.ReadsWhole(rowOrdinal: ordinal)) {
            return true;
        }

        var row = m_arena.Rows[ordinal];
        var admitted = sight.AdmitsRow(rowOrdinal: ordinal);
        var veiled = (layout.Shape switch {
            RowShape.Ordered => TryVeilPile(admitted: admitted, job: job, layout: layout, ordinal: ordinal, reason: out reason, row: row, seed: seed),
            RowShape.Lattice when !admitted => TryVeilBoard(layout: layout, ordinal: ordinal, reason: out reason, row: row, seed: seed),
            _ => TryVeilCells(admitted: admitted, job: job, layout: layout, ordinal: ordinal, reason: out reason, row: row, seed: seed),
        });

        if (!veiled) {
            return false;
        }
        // A private site's cursor and drawn masks are storage the actor is not shown: its imagined draws start from
        // nothing, so they say nothing about how many words the authority's stream consumed or which values it dealt.
        if ((row.Draw is { PrivateStream: true }) && (layout.MaskWordStart >= 0)) {
            if (!m_arena.TryWriteDrawCursor(cursor: 0L, rowOrdinal: ordinal)) {
                reason = "its draw cursor takes no write";

                return false;
            }
            for (var mask = 0; (mask < layout.MaskCount); mask++) {
                _ = m_arena.TryWriteDrawnMask(index: mask, mask: default, rowOrdinal: ordinal);
            }
        }

        return true;
    }
    // A row whose members the actor may know by name (a slot, a keyed row, a ring, a lattice it may read): each cell
    // it may not read keeps its key and position and takes an imagined value.
    private bool TryVeilCells(Job job, int ordinal, in ArenaRowLayout layout, StateRow row, bool admitted, ulong seed, out string reason) {
        reason = string.Empty;

        var sight = job.Sight!;
        var zero = ((layout.Kind == CellKind.Vector) ? new sbyte[layout.Dimensions] : []);
        var cursor = 0;

        while (m_arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: ordinal)) {
            if (admitted && sight.CanRead(key: key, rowOrdinal: ordinal)) {
                continue;
            }

            var written = (layout.Kind switch {
                CellKind.Text => m_arena.TryWriteText(key: key, reason: out reason, rowOrdinal: ordinal, text: string.Empty),
                CellKind.Vector => m_arena.TryWriteVector(components: zero, key: key, reason: out reason, rowOrdinal: ordinal),
                _ => m_arena.TryWriteLive(
                    key: key,
                    operand: SampleNumber(layout: layout, ordinal: ordinal, row: row, seed: CellSeed(cell: key.Ordinal, row: row, seed: seed)),
                    reason: out reason,
                    rowOrdinal: ordinal,
                    time: Time,
                    write: StateWriteKind.Set
                ),
            });

            if (!written) {
                return false;
            }

            _ = m_arena.TryWriteObservation(key: key, observation: null, rowOrdinal: ordinal);
        }

        return true;
    }
    // A lattice whose own policy excludes the actor withholds every cell, stored or not, so every cell is imagined.
    private bool TryVeilBoard(int ordinal, in ArenaRowLayout layout, StateRow row, ulong seed, out string reason) {
        reason = string.Empty;

        for (var cell = 0; (cell < layout.CellCapacity); cell++) {
            if (!m_arena.TryWriteBoardCell(
                cell: cell,
                reason: out reason,
                rowOrdinal: ordinal,
                value: SampleNumber(layout: layout, ordinal: ordinal, row: row, seed: CellSeed(cell: cell, row: row, seed: seed)),
                write: StateWriteKind.Set
            )) {
                return false;
            }
        }

        return true;
    }
    // A pile, whose membership is what it hides: every member the actor may not read leaves, and as many imagined
    // members as the actor is told of are dealt from the domain keys the actor may read and sees in no pile over that
    // domain. Count and Placeholder tell it the hidden count; Omit, or a row whose own policy excludes the actor, tells
    // it nothing, not even whether any member is hidden, so it imagines none and plans over what it sees. Under
    // Placeholder the imagined members stand where the hidden ones stood, whose places are disclosed; under Count they
    // follow the visible members, whose relative order is all that is disclosed.
    private bool TryVeilPile(Job job, int ordinal, in ArenaRowLayout layout, StateRow row, bool admitted, ulong seed, out string reason) {
        reason = string.Empty;

        var sight = job.Sight!;
        var policy = (row.Visibility?.Hidden ?? HiddenCells.Omit);
        var counted = (admitted && (policy != HiddenCells.Omit));
        var count = m_arena.CellCount(rowOrdinal: ordinal);

        m_veilKeys.Clear();
        m_veilNames.Clear();
        m_veilPositions.Clear();
        m_veilSeen.Clear();

        for (var position = 0; (position < count); position++) {
            if (!m_arena.TryKeyAt(key: out var key, position: position, rowOrdinal: ordinal)) {
                continue;
            }
            if (admitted && sight.CanRead(key: key, rowOrdinal: ordinal)) {
                _ = m_veilSeen.Add(item: key);
            } else {
                m_veilKeys.Add(item: key);
                m_veilPositions.Add(item: position);
            }
        }
        for (var index = (m_veilKeys.Count - 1); (index >= 0); index--) {
            if (!m_arena.TryRemove(key: m_veilKeys[index], reason: out reason, rowOrdinal: ordinal)) {
                return false;
            }
        }

        var domain = layout.DomainOrdinal;

        if (counted && (m_veilPositions.Count > 0) && (domain >= 0)) {
            var whole = sight.ReadsWhole(rowOrdinal: domain);
            var domainAdmitted = sight.AdmitsRow(rowOrdinal: domain);
            var cursor = 0;

            while (m_arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: domain)) {
                if (
                    (whole || (domainAdmitted && sight.CanRead(key: key, rowOrdinal: domain))) &&
                    !m_veilSeen.Contains(item: key) &&
                    !SeenInPeer(domain: domain, job: job, key: key, ordinal: ordinal)
                ) {
                    m_veilNames.Add(item: key);
                }
            }
        }

        var take = (counted
            ? Math.Max(val1: 0, val2: Math.Min(val1: m_veilPositions.Count, val2: Math.Min(val1: m_veilNames.Count, val2: (layout.CellCapacity - m_veilSeen.Count))))
            : 0
        );

        if (take == 0) {
            return true;
        }

        var rng = Pcg32XshRr.Create(state: seed, stream: RowStream(row: row));

        rng.Shuffle(values: CollectionsMarshal.AsSpan(list: m_veilNames));

        for (var index = 0; (index < take); index++) {
            var name = m_veilNames[index];
            var value = (layout.Kind switch {
                CellKind.Bool => CellValue.Bool(value: true),
                CellKind.Text => CellValue.Text(value: string.Empty),
                CellKind.Vector => CellValue.Vector(components: new sbyte[layout.Dimensions]),
                _ => CellValue.FromNumber(kind: layout.Kind, raw: SampleNumber(layout: layout, ordinal: ordinal, row: row, seed: CellSeed(cell: name.Ordinal, row: row, seed: seed))),
            });

            if (!m_arena.TryInsert(
                key: out var inserted,
                name: m_arena.Keys[name],
                position: ((counted && (policy == HiddenCells.Placeholder)) ? m_veilPositions[index] : -1),
                reason: out reason,
                rowOrdinal: ordinal,
                value: value
            )) {
                return false;
            }
            if (ordinal == job.Plan.TokensOrdinal) {
                _ = job.Inserted.Add(item: inserted);
            }
        }

        return true;
    }
    // Whether the actor sees the key in another pile over the same domain, which a card can stand in only one of.
    private bool SeenInPeer(Job job, int ordinal, int domain, CellKey key) {
        var sight = job.Sight!;

        foreach (var peer in PeerPiles(domain: domain)) {
            if (
                (peer != ordinal) &&
                m_arena.TryRead(key: key, rowOrdinal: peer, value: out _) &&
                (sight.ReadsWhole(rowOrdinal: peer) || (sight.AdmitsRow(rowOrdinal: peer) && sight.CanRead(key: key, rowOrdinal: peer)))
            ) {
                return true;
            }
        }

        return false;
    }
    private int[] PeerPiles(int domain) {
        m_peerPiles ??= [];
        if (m_peerPiles.TryGetValue(key: domain, value: out var peers)) {
            return peers;
        }

        var found = new List<int>();

        for (var ordinal = 0; (ordinal < m_arena.Layout.RowCount); ordinal++) {
            if ((m_arena.Layout[ordinal].Shape == RowShape.Ordered) && (m_arena.Layout[ordinal].DomainOrdinal == domain)) {
                found.Add(item: ordinal);
            }
        }

        return m_peerPiles[domain] = [.. found];
    }
    // An imagined number inside the row's declared envelope and symbolic domain, drawn from the cell's own seed.
    private long SampleNumber(in ArenaRowLayout layout, int ordinal, StateRow row, ulong seed) {
        var low = ((layout.Kind == CellKind.Bool) ? 0L : (row.Min ?? long.MinValue));
        var high = ((layout.Kind == CellKind.Bool) ? 1L : (row.Max ?? long.MaxValue));

        if (
            (layout.Kind == CellKind.Int) &&
            m_arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row.Name) &&
            m_arena.Catalog.TryGetEnum(handle: handle, symbols: out var symbols) &&
            (symbols is not null)
        ) {
            low = Math.Max(val1: low, val2: 0L);
            high = Math.Min(val1: high, val2: (symbols.Count - 1L));
        }
        if (high <= low) {
            return low;
        }

        var words = new DrawGeneratorWords<Pcg32XshRr>(generator: Pcg32XshRr.Create(state: seed, stream: 0UL));

        return (BoundedRandomSampling.TrySampleWide(
            exclusiveHigh: (((UInt128)(((Int128)high) - low)) + 1U),
            maxAttempts: GeneratorCapacity.MaxBoundedAttempts,
            reason: out _,
            source: ref words,
            value: out var offset,
            words: out _
        )
            ? ((long)(((Int128)low) + ((Int128)offset)))
            : low
        );
    }
    // One stamp or key row as the job may read it. A row the actor reads whole folds as the arena stores it; any other
    // folds only what the actor is told: its readable cells, the names and places of the cells it knows by name, and a
    // pile's hidden count where its policy discloses one. With the veil open the hidden cells hold imagined values, and
    // the fold of a non-whole row is then its key and value columns as they stand, nothing the authority's storage
    // keeps beside them.
    private void FoldSeen(Job job, int ordinal, ref Fnv1aHash hash) {
        var sight = job.Sight!;

        if (sight.ReadsWhole(rowOrdinal: ordinal) || m_arena.Layout[ordinal].HostOwned) {
            m_arena.AddRowTo(hash: ref hash, rowOrdinal: ordinal);

            return;
        }

        var layout = m_arena.Layout[ordinal];
        var row = m_arena.Rows[ordinal];
        var admitted = sight.AdmitsRow(rowOrdinal: ordinal);
        var pile = (layout.Shape == RowShape.Ordered);
        var counted = (admitted && ((row.Visibility?.Hidden ?? HiddenCells.Omit) != HiddenCells.Omit));
        var placeholder = (counted && (row.Visibility?.Hidden == HiddenCells.Placeholder));
        var hidden = 0L;
        var position = 0L;
        var cursor = 0;

        hash.Add(value: (ulong.MaxValue - 1UL));
        // A lattice its own policy closes to the actor withholds every cell, so which of them it stores is withheld too.
        if (!m_veilOpen && !admitted && (layout.Shape == RowShape.Lattice)) {
            return;
        }

        while (m_arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: ordinal)) {
            // Under the veil every cell is one the actor reads or one the veil imagined.
            var seen = (m_veilOpen || (admitted && sight.CanRead(key: key, rowOrdinal: ordinal)));

            if (seen) {
                hash.Add(value: position);
                hash.Add(value: ((ulong)key.Ordinal));
                if (m_arena.TryRead(key: key, rowOrdinal: ordinal, value: out var value)) {
                    FoldValue(hash: ref hash, value: value);
                }
            } else {
                hidden++;
                if (!pile || placeholder) {
                    hash.Add(value: position);
                }
                if (!pile) {
                    hash.Add(value: ((ulong)key.Ordinal));
                }
            }
            if (!pile || seen || placeholder) {
                position++;
            }
        }
        if (pile && counted) {
            hash.Add(value: hidden);
        }
    }
    private static void FoldValue(ref Fnv1aHash hash, in CellValue value) {
        hash.Add(value: ((byte)value.Kind));
        switch (value.Kind) {
            case CellKind.Text:
                hash.Add(value: Fnv1aHash.Compute(values: value.AsText.AsSpan()));

                break;
            case CellKind.Vector:
                foreach (var component in value.AsVector.Span) {
                    hash.Add(value: ((byte)component));
                }

                break;
            default:
                hash.Add(value: value.Raw);

                break;
        }
    }
}
