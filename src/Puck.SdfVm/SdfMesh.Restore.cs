using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

// A published mesh's planes (positions, indices, normals and triangle materials, its own and its coarser levels') are
// read to write the mesh's words into a mesh region (SdfMeshRegion.WriteMesh), which then draws from the region, and
// otherwise only by builds and queries that read them for a moment (the irradiance scene's build, the far product, the
// trace judge, a renumbering). A mesh the library published from its store therefore lets them go once nothing needs
// them, and takes them back from the store when something does: a relocation, a grown draw reserve, another residency's
// region, a renumbered variant landing, a region copy whose runs coarsened over the mesh's words, or a build's read.
//
// Invariant: a mesh's planes are read only under a hold (Held), and freed only when no need is held. Enforced in two
// places: the planes are private to the mesh and to its levels' storage, and Held, which only Hold makes (holding a need
// and waiting for or running the restore), is the one door that hands them out, so a reader cannot reach a plane
// without a need; and Restorable counts needs, frees at zero and reports resident only after a restore's copy, under
// one gate. A view kept past its hold reads a freed plane and throws, since each is an OwnedBuffer whose memory views
// read past its zero length; a view taken by a chunk or a renumbered variant reads the restored storage again, the
// buffer being the view's memory manager. The members, vertex members and influences are kept for the mesh's
// lifetime and need no hold.
public sealed partial record SdfMesh {
    private Restorable? m_restorable;

    // A mesh prints its counts: its joined indices may be absent and its planes let go.
    private bool PrintMembers(System.Text.StringBuilder builder) {
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"Vertices = {VertexCount}, Triangles = {TriangleCount}, Levels = {Levels.Count}, Chunks = {Chunks.Count}, Resident = {Resident}");
        return true;
    }

    /// <summary>Gets whether the mesh's planes are in memory: always, unless the library published it from its store and
    /// nothing has held it since they landed.</summary>
    internal bool Resident => (m_restorable?.Resident ?? true);
    /// <summary>Gets whether the store could not restore the mesh's planes, so the mesh can never be read again and its
    /// group bakes again (<see cref="SdfMeshLibrary.LostRevision"/>).</summary>
    internal bool Lost => (m_restorable?.Lost ?? false);

    /// <summary>Holds a need for the mesh's planes: they stay in memory, or come back from the store off the caller's
    /// thread, until the matching <see cref="Unneed"/>. A need reads nothing; a reader takes a hold.</summary>
    internal void Need() => m_restorable?.Need();
    /// <summary>Releases a need <see cref="Need"/> took; the last one lets the planes go.</summary>
    internal void Unneed() => m_restorable?.Unneed();
    /// <summary>Holds a need and returns once the mesh's planes are resident or lost, running a restore that has not
    /// started on the caller's thread (a read and decode of the mesh's store entry): the one way to read the mesh's
    /// positions, indices, normals and triangle materials.</summary>
    /// <returns>The hold, which releases its need when disposed.</returns>
    public Held Hold() {
        m_restorable?.NeedResident();
        return new Held(mesh: this);
    }

    /// <summary>A need on a mesh's planes, released when disposed: the one door to the mesh's positions, indices, normals
    /// and triangle materials and its levels' (<see cref="SdfMeshRegion.WriteMesh"/>). Only <see cref="Hold"/> creates
    /// one, which waits until the planes are resident. A memory view taken from a hold is read only while the hold
    /// stands.</summary>
    public readonly ref struct Held {
        internal Held(SdfMesh mesh) => Mesh = mesh;

        /// <summary>Gets the held mesh.</summary>
        public SdfMesh Mesh { get; }
        /// <summary>Gets whether the mesh's planes are readable: false only for a mesh whose store could not restore
        /// them, whose planes then read as nothing.</summary>
        public bool Readable => !Mesh.Lost;

        /// <summary>Gets the object-space vertex positions.</summary>
        public ReadOnlyMemory<Vector3> Positions => (Readable ? Mesh.m_positions : default);
        /// <summary>Gets three vertex indices per triangle.</summary>
        /// <exception cref="InvalidOperationException">The mesh is a published chunked mesh, which keeps no joined
        /// indices: its chunks hold its triangles, and only they are drawn, traced and stored.</exception>
        public ReadOnlyMemory<uint> Indices => (Readable ? Mesh.JoinedIndices : default);
        /// <summary>Gets one object-space normal per vertex, or none.</summary>
        public ReadOnlyMemory<Vector3> Normals => (Readable ? Mesh.m_normals : default);
        /// <summary>Gets one palette entry per triangle, added to its draw's material, or none.</summary>
        public ReadOnlyMemory<uint> TriangleMaterials => (Readable ? Mesh.m_triangleMaterials : default);

        /// <summary>Returns a level's indices: the mesh's own for level zero, else those of
        /// <see cref="SdfMesh.Levels"/> at one less.</summary>
        /// <param name="level">The level, from zero to <see cref="SdfMesh.Levels"/>' count.</param>
        /// <returns>The level's indices.</returns>
        public ReadOnlyMemory<uint> LevelIndices(int level) => ((level == 0) ? Indices : (Readable ? Mesh.Levels[(level - 1)].UnheldIndices : default));
        /// <summary>Returns a level's triangle materials, as <see cref="LevelIndices"/> numbers levels.</summary>
        /// <param name="level">The level, from zero to <see cref="SdfMesh.Levels"/>' count.</param>
        /// <returns>The level's triangle materials, or none for a mesh without them.</returns>
        public ReadOnlyMemory<uint> LevelMaterials(int level) => ((level == 0) ? TriangleMaterials : (Readable ? Mesh.Levels[(level - 1)].UnheldMaterials : default));

        /// <summary>Releases the need.</summary>
        public void Dispose() => Mesh.Unneed();
    }

    /// <summary>Describes the process's restorable mesh storage: the parts let go and taken back, their bytes, and the
    /// restores the store could not answer.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public static string DescribeRestores() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"mesh-releases={Interlocked.Read(location: ref Releases)} mesh-released-bytes={Interlocked.Read(location: ref ReleasedBytes)}B mesh-restores={Interlocked.Read(location: ref Restores)} mesh-restored-bytes={Interlocked.Read(location: ref RestoredBytes)}B mesh-restore-misses={Interlocked.Read(location: ref RestoreMisses)} mesh-released-now={Interlocked.Read(location: ref ReleasedNow)}B");

    /// <summary>The seconds a published mesh's planes stand resident with no need before they go
    /// (<see cref="ReleaseIdle"/>): long enough for the reads that follow a landing to find them resident.</summary>
    public const double IdleSeconds = 5.0;

    // The planes waiting to go, oldest first, each with the stamp of the idling it was queued for.
    private static readonly Queue<(Restorable Storage, long Stamp)> Idle = new();
    private static readonly Lock IdleGate = new();

    /// <summary>Lets go of the planes of every published mesh that has stood unneeded for <see cref="IdleSeconds"/>, or of
    /// every unneeded one when <paramref name="all"/> is set (the host account's trim): a residency calls it once a frame.
    /// The planes come back from the store when next held.</summary>
    /// <param name="all">Whether to let go of every unneeded mesh's planes, however recently it idled.</param>
    public static void ReleaseIdle(bool all = false) {
        var cutoff = (all ? long.MaxValue : (System.Diagnostics.Stopwatch.GetTimestamp() - ((long)(IdleSeconds * System.Diagnostics.Stopwatch.Frequency))));

        while (true) {
            (Restorable Storage, long Stamp) next;

            lock (IdleGate) {
                if (!Idle.TryPeek(result: out next) || (next.Stamp > cutoff)) { return; }
                _ = Idle.Dequeue();
            }
            next.Storage.FreeIfIdle(cutoff: cutoff, stamp: next.Stamp);
        }
    }

    private static long Releases;
    private static long ReleasedBytes;
    private static long Restores;
    private static long RestoredBytes;
    private static long RestoreMisses;
    private static long ReleasedNow;

    // The parts one NativeCopy of a store-published mesh may let go, shared by its chunks and every renumbered variant,
    // which view the same buffers, and the store entry that restores them.
    private sealed class Restorable(SdfMeshLibrary library, UInt128 identity, SdfMeshGeometry geometry) {
        private readonly Lock m_gate = new();
        // Each block with its plane, the piece and level it holds, the payload that owns it and the length it holds while
        // resident, which a restore must find again. A block leaves its list when its payload's last claim goes
        // (Detach), before the payload disposes it.
        private readonly List<Entry<Vector3>> m_vectors = [];
        private readonly List<Entry<uint>> m_words = [];

        private int m_needs;
        // When the planes last stood resident with no need, which names their entry in the idle queue.
        private long m_idleSince;
        // Written under the gate; read without it, so the frame thread never waits on a restore's copy. The write that
        // makes it Resident follows the copy, so a reader that sees Resident sees the restored parts.
        private volatile State m_state;
        // The bytes the blocks hold while resident.
        private long m_bytes;
        // Whether a worker or a NeedResident caller is running the restore, and the event the others wait on.
        private bool m_running;
        private ManualResetEventSlim? m_done;

        public bool Lost => (m_state == State.Lost);
        public bool Resident => (m_state == State.Resident);

        // A piece of -1 names a chunked mesh's joined plane (its positions, normals or triangle materials), which its chunks
        // view in order; a level counts from zero among the mesh's coarser levels.
        public void Add(Plane plane, int piece, Payload owner, OwnedBuffer<Vector3>? block) {
            if (block is null) { return; }
            m_vectors.Add(item: new(Plane: plane, Piece: piece, Level: 0, Block: block, Length: block.Length, Owner: owner));
            m_bytes += (((long)block.Length) * (3 * sizeof(float)));
            owner.Restorable = this;
        }
        public void Add(Plane plane, int piece, int level, Payload owner, OwnedBuffer<uint>? block) {
            if (block is null) { return; }
            m_words.Add(item: new(Plane: plane, Piece: piece, Level: level, Block: block, Length: block.Length, Owner: owner));
            m_bytes += (((long)block.Length) * sizeof(uint));
            owner.Restorable = this;
        }
        // Gives a published mesh and its chunks their restorable storage, or none when it holds nothing to let go.
        public static SdfMesh Attach(Restorable? restorable, SdfMesh mesh) {
            var kept = (((restorable is not null) && (restorable.m_bytes > 0)) ? restorable : null);

            mesh.m_restorable = kept;
            foreach (var chunk in mesh.Chunks) { chunk.m_restorable = kept; }
            return mesh;
        }
        // Forgets a payload's blocks as its last claim goes, before it disposes them.
        public void Detach(Payload owner) {
            lock (m_gate) {
                var freed = (m_state is State.Released or State.Restoring or State.Lost);
                var bytes = (Detach(entries: m_vectors, owner: owner) * (3 * sizeof(float))) + (Detach(entries: m_words, owner: owner) * sizeof(uint));

                m_bytes -= bytes;
                if (freed) { _ = Interlocked.Add(location1: ref ReleasedNow, value: -bytes); }
            }
        }
        public void Need() {
            lock (m_gate) {
                m_needs++;
                if (m_state != State.Released) { return; }
                m_state = State.Restoring;
            }
            Enqueue(restorable: this);
        }
        public void Unneed() {
            lock (m_gate) {
                if (m_needs <= 0) { throw new InvalidOperationException(message: "A mesh need was released twice."); }
                if ((--m_needs == 0) && (m_state == State.Resident) && (m_bytes > 0)) { IdleUnderGate(); }
            }
        }
        // Lets the planes go if they have stood unneeded since the stamp their idling was queued with, no later than the
        // cutoff; a need taken since, or a later idling, keeps them.
        public void FreeIfIdle(long stamp, long cutoff) {
            lock (m_gate) {
                if ((m_needs == 0) && (m_state == State.Resident) && (m_bytes > 0) && (m_idleSince == stamp) && (stamp <= cutoff)) { FreeUnderGate(); }
            }
        }
        // Under the gate, with no need held: queues the planes to go once they have stood unneeded for IdleSeconds, so the
        // reads that follow a landing (the irradiance scene's build, the trace judge) find them resident.
        private void IdleUnderGate() {
            m_idleSince = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (IdleGate) { Idle.Enqueue(item: (this, m_idleSince)); }
        }
        // Takes a need and returns once the parts are resident or lost: a restore no worker has started runs on the
        // caller, one a worker is running is waited for.
        public void NeedResident() {
            Need();
            while (true) {
                ManualResetEventSlim? running;

                lock (m_gate) {
                    if (m_state != State.Restoring) { return; }
                    if (!m_running) {
                        m_running = true;
                        running = null;
                    } else {
                        running = (m_done ??= new ManualResetEventSlim(initialState: false));
                        running.Reset();
                    }
                }
                if (running is null) {
                    RunClaimed();
                    return;
                }
                running.Wait();
            }
        }

        // Drops a payload's entries, answering the elements they held.
        private static long Detach<T>(List<Entry<T>> entries, Payload owner) where T : unmanaged {
            var elements = 0L;

            for (var index = (entries.Count - 1); (index >= 0); index--) {
                if (!ReferenceEquals(objA: entries[index].Owner, objB: owner)) { continue; }
                elements += entries[index].Length;
                entries.RemoveAt(index: index);
            }
            return elements;
        }
        private void FreeUnderGate() {
            foreach (var entry in m_vectors) { entry.Block.Free(); }
            foreach (var entry in m_words) { entry.Block.Free(); }
            m_state = State.Released;
            _ = Interlocked.Increment(location: ref Releases);
            _ = Interlocked.Add(location1: ref ReleasedBytes, value: m_bytes);
            _ = Interlocked.Add(location1: ref ReleasedNow, value: m_bytes);
        }
        // A worker's turn at this storage: nothing when its restore already ran (on a NeedResident caller) or another
        // worker claimed it.
        private void Run() {
            lock (m_gate) {
                if ((m_state != State.Restoring) || m_running) { return; }
                m_running = true;
            }
            RunClaimed();
        }
        // Reads the store entry the mesh was published from and decodes the planes back into their blocks with the gate
        // open: while the state is Restoring nothing reads the blocks (readers need Resident) and nothing frees them (Free
        // needs Resident), and a claim on each owning payload keeps its blocks from being disposed. The decode skips every
        // plane the storage did not let go (the members, vertex members and influences) and checks the entry's counts
        // against the blocks' before it writes any (Refill). The store answers what it answered at publication
        // (SdfMeshLibrary.Store publishes what the entry answers, SdfMeshStoreCodec.Conform), so the words a region writes
        // again are the words it wrote first. Any failure, an allocation's included, ends the storage Lost, which bakes its
        // groups again; nothing escapes to the worker.
        private void RunClaimed() {
            var owners = new List<Payload>();

            lock (m_gate) {
                foreach (var entry in m_vectors) { if (!owners.Contains(item: entry.Owner)) { owners.Add(item: entry.Owner); } }
                foreach (var entry in m_words) { if (!owners.Contains(item: entry.Owner)) { owners.Add(item: entry.Owner); } }
            }

            var claimed = new List<Payload>(capacity: owners.Count);
            var filled = false;
            string? failure;

            try {
                foreach (var owner in owners) {
                    try {
                        owner.Acquire(cached: false);
                        claimed.Add(item: owner);
                    } catch (ObjectDisposedException) {
                        // Its last claim went: Detach drops its blocks, and nothing reads them.
                    }
                }
                Refill refill;

                lock (m_gate) {
                    refill = new Refill(
                        vectors: [.. m_vectors.Where(predicate: entry => claimed.Contains(item: entry.Owner))],
                        words: [.. m_words.Where(predicate: entry => claimed.Contains(item: entry.Owner))]
                    );
                }
                if (library.Disk is { } disk) {
                    // A declined entry says why: its parts no longer fit these blocks, or the read found no workspace memory.
                    filled = disk.TryReadInto(declined: out _, geometry: geometry, identity: identity, materials: out _, mesh: out _, refusal: out var refusal, storage: refill, taking: null);
                    failure = (filled ? null : (refusal ?? "the store holds no entry"));
                } else {
                    failure = "the store holds no entry";
                }
            } catch (Exception exception) {
                filled = false;
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }

            lock (m_gate) {
                m_running = false;
                if (filled) {
                    m_state = State.Resident;
                    _ = Interlocked.Increment(location: ref Restores);
                    _ = Interlocked.Add(location1: ref RestoredBytes, value: m_bytes);
                    _ = Interlocked.Add(location1: ref ReleasedNow, value: -m_bytes);
                    if ((m_needs == 0) && (m_bytes > 0)) { IdleUnderGate(); }
                } else {
                    m_state = State.Lost;
                }
                m_done?.Set();
            }
            foreach (var owner in claimed) { owner.Release(cached: false); }
            if (!filled) {
                _ = Interlocked.Increment(location: ref RestoreMisses);
                library.Lose(geometry: geometry, identity: identity, reason: (failure ?? "unknown"));
            }
        }

        // The storage a restore decodes into: the blocks the storage let go that live payloads hold, each sized to the
        // length it held while resident. It takes no other plane, so the decode skips them by their stated lengths.
        private sealed class Refill(Entry<Vector3>[] vectors, Entry<uint>[] words) : SdfMeshStorage {
            private SdfMeshShape? m_shape;

            public override bool Begin(SdfMeshShape shape) {
                Abandon();
                foreach (var entry in vectors) {
                    if (Expected(level: entry.Level, piece: entry.Piece, plane: entry.Plane, shape: shape) != entry.Length) { return false; }
                }
                foreach (var entry in words) {
                    if (Expected(level: entry.Level, piece: entry.Piece, plane: entry.Plane, shape: shape) != entry.Length) { return false; }
                }
                // Once any resize can run, the codec's Abandon must be able to free the partially refilled planes.
                m_shape = shape;
                foreach (var entry in vectors) { entry.Block.Resize(clear: false, length: entry.Length, preserve: false); }
                foreach (var entry in words) { entry.Block.Resize(clear: false, length: entry.Length, preserve: false); }

                return true;
            }
            public override bool Positions(int piece, out Span<Vector3> destination) => Take(destination: out destination, entries: vectors, level: 0, piece: piece, plane: Plane.Positions);
            public override bool Normals(int piece, out Span<Vector3> destination) => Take(destination: out destination, entries: vectors, level: 0, piece: piece, plane: Plane.Normals);
            public override bool Indices(int piece, out Span<uint> destination) => Take(destination: out destination, entries: words, level: 0, piece: piece, plane: Plane.Indices);
            public override bool Materials(int piece, out Span<uint> destination) => Take(destination: out destination, entries: words, level: 0, piece: piece, plane: Plane.Materials);
            public override bool LevelIndices(int piece, int level, out Span<uint> destination) => Take(destination: out destination, entries: words, level: level, piece: piece, plane: Plane.LevelIndices);
            public override bool LevelMaterials(int piece, int level, out Span<uint> destination) => Take(destination: out destination, entries: words, level: level, piece: piece, plane: Plane.LevelMaterials);
            public override bool Members(int piece, out Span<SdfMeshMember> destination) { destination = default; return false; }
            public override bool VertexMembers(int piece, out Span<byte> destination) { destination = default; return false; }
            public override bool Influences(int piece, out Span<SdfMeshInfluence> destination) { destination = default; return false; }
            public override SdfMesh? Finish() { m_shape = null; return null; }
            // The blocks a failed decode sized hold nothing, so they are freed rather than kept while the storage is lost.
            public override void Abandon() {
                if (m_shape is null) { return; }
                m_shape = null;
                foreach (var entry in vectors) { entry.Block.Free(); }
                foreach (var entry in words) { entry.Block.Free(); }
            }

            // The span a piece's plane decodes into: its own block, or its run of a chunked mesh's joined block.
            private bool Take<T>(Entry<T>[] entries, Plane plane, int piece, int level, out Span<T> destination) where T : unmanaged {
                foreach (var entry in entries) {
                    if ((entry.Plane == plane) && (entry.Piece == piece) && (entry.Level == level)) {
                        destination = entry.Block.GetSpan();

                        return true;
                    }
                }
                foreach (var entry in entries) {
                    if ((entry.Plane == plane) && (entry.Piece < 0)) {
                        var first = 0;

                        for (var index = 0; (index < piece); index++) { first += Expected(level: level, piece: index, plane: plane, shape: m_shape!); }
                        destination = entry.Block.GetSpan().Slice(length: Expected(level: level, piece: piece, plane: plane, shape: m_shape!), start: first);

                        return true;
                    }
                }
                destination = default;

                return false;
            }
        }

        // The elements a shape gives a piece's plane, or all its pieces' together for a piece of -1.
        private static int Expected(SdfMeshShape shape, Plane plane, int piece, int level) {
            if (piece < 0) {
                var sum = 0;

                for (var index = 0; (index < shape.Pieces.Length); index++) { sum += Expected(level: level, piece: index, plane: plane, shape: shape); }
                return sum;
            }
            if (piece >= shape.Pieces.Length) { return 0; }

            var counts = shape.Pieces[piece];

            return plane switch {
                Plane.Positions => counts.Vertices,
                Plane.Normals => counts.Normals,
                Plane.Indices => counts.Indices,
                Plane.Materials => counts.Materials,
                Plane.LevelIndices => ((level < counts.Levels.Length) ? counts.Levels[level].Indices : 0),
                Plane.LevelMaterials => ((level < counts.Levels.Length) ? counts.Levels[level].Materials : 0),
                _ => 0,
            };
        }

        // The storage waiting for a restore, worked off by at most a quarter of the processors' pool items at once, so a
        // rebuilt region's thousand restores never fill the pool with blocking reads and decodes. Run reports its own
        // failures (the storage ends Lost).
        private static readonly BoundedWorkQueue<Restorable> Waiting = new(concurrency: Math.Max(val1: 1, val2: (Environment.ProcessorCount / 4)), run: static restorable => restorable.Run());

        private static void Enqueue(Restorable restorable) => Waiting.Enqueue(item: restorable);

        private enum State { Resident, Released, Restoring, Lost }
    }

    // A plane a published mesh may let go and restore (Restorable).
    private enum Plane : byte { Positions, Normals, Indices, Materials, LevelIndices, LevelMaterials }

    // One block a published mesh may let go: its plane, the piece (-1 for a chunked mesh's joined plane) and level it
    // holds, the block, the length it holds while resident and the payload that owns it.
    private readonly record struct Entry<T>(Plane Plane, int Piece, int Level, OwnedBuffer<T> Block, int Length, Payload Owner) where T : unmanaged;
}
