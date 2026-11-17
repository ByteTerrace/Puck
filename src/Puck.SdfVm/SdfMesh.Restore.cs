using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// A published mesh's normals and coarser levels' indices are read only to write the mesh's words into a mesh region
// (SdfMeshRegion.WriteMesh). A mesh the library published from its store therefore lets them go once no region needs its
// words, and takes them back from the store when a region needs them again: a relocation, a grown draw reserve, another
// residency's region, a renumbered variant landing, or a region copy whose runs coarsened over the mesh's words.
//
// Invariant: those parts are read only under a need (Need, NeedResident) while the storage is resident, and freed only
// when no need is held. Enforced in one place: Restorable counts needs, frees at zero and reports resident only after a
// restore's copy, under one gate. The region reads a mesh's words only through a Held (SdfWorldTables.MeshWordsOf takes
// one), which holds a need and waits for or runs the restore, so a region source asked for any mesh's words, owed or
// not, reads resident storage. A part read while freed throws, since each is an OwnedBuffer whose memory views read past
// its zero length; a view taken by a chunk or a renumbered variant reads the restored storage again, the buffer being the
// view's memory manager.
public sealed partial record SdfMesh {
    private Restorable? m_restorable;

    // A mesh prints its counts: its joined indices may be absent and its normals and levels let go.
    private bool PrintMembers(System.Text.StringBuilder builder) {
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"Vertices = {Positions.Length}, Triangles = {TriangleCount}, Levels = {Levels.Count}, Chunks = {Chunks.Count}, Resident = {Resident}");
        return true;
    }

    /// <summary>Gets whether the mesh's normals and coarser levels' indices are in memory: always, unless the library
    /// published it from its store and no region has needed its words since they landed.</summary>
    internal bool Resident => (m_restorable?.Resident ?? true);
    /// <summary>Gets whether the store could not restore the mesh's normals and levels, so the mesh can never be written
    /// into a region again and its group bakes again (<see cref="SdfMeshLibrary.LostRevision"/>).</summary>
    internal bool Lost => (m_restorable?.Lost ?? false);

    /// <summary>Holds a need for the mesh's normals and coarser levels' indices: they stay in memory, or come back from the
    /// store off the caller's thread, until the matching <see cref="Unneed"/>.</summary>
    internal void Need() => m_restorable?.Need();
    /// <summary>Releases a need <see cref="Need"/> took; the last one lets the parts go.</summary>
    internal void Unneed() => m_restorable?.Unneed();
    /// <summary>Holds a need and returns once the mesh's parts are resident or lost, running a restore that has not
    /// started on the caller's thread: the one way to read the parts of a mesh no owed set holds.</summary>
    /// <returns>The hold, which releases its need when disposed.</returns>
    internal Held Hold() {
        m_restorable?.NeedResident();
        return new Held(mesh: this);
    }

    /// <summary>A need on a mesh's parts, released when disposed: what reading a published mesh's normals and coarser
    /// levels' indices takes (<see cref="SdfMeshRegion.WriteMesh"/>). Only this assembly creates one, through a hold
    /// that waits until the parts are resident.</summary>
    public readonly ref struct Held {
        internal Held(SdfMesh mesh) => Mesh = mesh;

        /// <summary>Gets the held mesh.</summary>
        public SdfMesh Mesh { get; }
        /// <summary>Gets whether the mesh's parts are readable: false only for a mesh whose store could not restore
        /// them.</summary>
        public bool Readable => !Mesh.Lost;

        /// <summary>Releases the need.</summary>
        public void Dispose() => Mesh.Unneed();
    }

    /// <summary>Describes the process's restorable mesh storage: the parts let go and taken back, their bytes, and the
    /// restores the store could not answer.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public static string DescribeRestores() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"mesh-releases={Interlocked.Read(location: ref Releases)} mesh-released-bytes={Interlocked.Read(location: ref ReleasedBytes)}B mesh-restores={Interlocked.Read(location: ref Restores)} mesh-restored-bytes={Interlocked.Read(location: ref RestoredBytes)}B mesh-restore-misses={Interlocked.Read(location: ref RestoreMisses)} mesh-released-now={Interlocked.Read(location: ref ReleasedNow)}B");

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
        // Each block with the payload that owns it and the length it holds while resident, which a restore must find
        // again. A block leaves its list when its payload's last claim goes (Detach), before the payload disposes it.
        private readonly List<(int Piece, OwnedBuffer<Vector3> Block, int Length, Payload Owner)> m_normals = [];
        private readonly List<(int Piece, int Level, OwnedBuffer<uint> Block, int Length, Payload Owner)> m_levels = [];

        private int m_needs;
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

        // A piece of -1 names the joined normals of a chunked mesh, which its chunks view in order.
        public void AddNormals(int piece, Payload owner, OwnedBuffer<Vector3>? block) {
            if (block is null) { return; }
            m_normals.Add(item: (piece, block, block.Length, owner));
            m_bytes += (((long)block.Length) * (3 * sizeof(float)));
            owner.Restorable = this;
        }
        public void AddLevel(int piece, int level, Payload owner, OwnedBuffer<uint>? block) {
            if (block is null) { return; }
            m_levels.Add(item: (piece, level, block, block.Length, owner));
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
                var bytes = 0L;

                for (var index = (m_normals.Count - 1); (index >= 0); index--) {
                    if (!ReferenceEquals(objA: m_normals[index].Owner, objB: owner)) { continue; }
                    bytes += (((long)m_normals[index].Length) * (3 * sizeof(float)));
                    m_normals.RemoveAt(index: index);
                }
                for (var index = (m_levels.Count - 1); (index >= 0); index--) {
                    if (!ReferenceEquals(objA: m_levels[index].Owner, objB: owner)) { continue; }
                    bytes += (((long)m_levels[index].Length) * sizeof(uint));
                    m_levels.RemoveAt(index: index);
                }
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
                if ((--m_needs == 0) && (m_state == State.Resident) && (m_bytes > 0)) { FreeUnderGate(); }
            }
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

        private void FreeUnderGate() {
            foreach (var (_, block, _, _) in m_normals) { block.Free(); }
            foreach (var (_, _, block, _, _) in m_levels) { block.Free(); }
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
        // Reads the store entry the mesh was published from and decodes the parts back into their blocks with the gate open:
        // while the state is Restoring nothing reads the blocks (readers need Resident) and nothing frees them (Free needs
        // Resident), and a claim on each owning payload keeps its blocks from being disposed. The decode skips every plane
        // but the normals and the coarser levels' indices (Refill), and checks the entry's counts against the blocks' before
        // it writes any. The store answers what it answered at publication (SdfMeshLibrary.Store publishes what it reads
        // back), so the words a region writes again are the words it wrote first. Any failure, an allocation's included,
        // ends the storage Lost, which bakes its groups again; nothing escapes to the worker.
        private void RunClaimed() {
            var owners = new List<Payload>();

            lock (m_gate) {
                foreach (var entry in m_normals) { if (!owners.Contains(item: entry.Owner)) { owners.Add(item: entry.Owner); } }
                foreach (var entry in m_levels) { if (!owners.Contains(item: entry.Owner)) { owners.Add(item: entry.Owner); } }
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
                        levels: [.. m_levels.Where(predicate: entry => claimed.Contains(item: entry.Owner))],
                        normals: [.. m_normals.Where(predicate: entry => claimed.Contains(item: entry.Owner))]
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
                    if ((m_needs == 0) && (m_bytes > 0)) { FreeUnderGate(); }
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

        // The storage a restore decodes into: the blocks of the normals and coarser levels' indices that live payloads hold,
        // each sized to the length it held while resident. It takes no other plane, so the decode skips them by their
        // stated lengths.
        private sealed class Refill((int Piece, OwnedBuffer<Vector3> Block, int Length, Payload Owner)[] normals, (int Piece, int Level, OwnedBuffer<uint> Block, int Length, Payload Owner)[] levels) : SdfMeshStorage {
            private SdfMeshShape? m_shape;

            public override bool Begin(SdfMeshShape shape) {
                Abandon();
                foreach (var (piece, _, length, _) in normals) {
                    var expected = ((piece < 0) ? shape.Pieces.Sum(selector: static each => each.Normals) : ((piece < shape.Pieces.Length) ? shape.Pieces[piece].Normals : 0));

                    if (expected != length) { return false; }
                }
                foreach (var (piece, level, _, length, _) in levels) {
                    var expected = (((piece < shape.Pieces.Length) && (level < shape.Pieces[piece].Levels.Length)) ? shape.Pieces[piece].Levels[level].Indices : 0);

                    if (expected != length) { return false; }
                }
                foreach (var (_, block, length, _) in normals) { block.Resize(clear: false, length: length, preserve: false); }
                foreach (var (_, _, block, length, _) in levels) { block.Resize(clear: false, length: length, preserve: false); }
                m_shape = shape;

                return true;
            }
            public override bool Normals(int piece, out Span<Vector3> destination) {
                foreach (var (at, block, _, _) in normals) {
                    if (at == piece) {
                        destination = block.GetSpan();

                        return true;
                    }
                }
                foreach (var (at, block, _, _) in normals) {
                    if (at < 0) {
                        destination = block.GetSpan().Slice(length: m_shape!.Pieces[piece].Normals, start: m_shape.FirstNormal(piece: piece));

                        return true;
                    }
                }
                destination = default;

                return false;
            }
            public override bool LevelIndices(int piece, int level, out Span<uint> destination) {
                foreach (var (at, which, block, _, _) in levels) {
                    if ((at == piece) && (which == level)) {
                        destination = block.GetSpan();

                        return true;
                    }
                }
                destination = default;

                return false;
            }
            public override bool Positions(int piece, out Span<Vector3> destination) { destination = default; return false; }
            public override bool Indices(int piece, out Span<uint> destination) { destination = default; return false; }
            public override bool Materials(int piece, out Span<uint> destination) { destination = default; return false; }
            public override bool Members(int piece, out Span<SdfMeshMember> destination) { destination = default; return false; }
            public override bool VertexMembers(int piece, out Span<byte> destination) { destination = default; return false; }
            public override bool Influences(int piece, out Span<SdfMeshInfluence> destination) { destination = default; return false; }
            public override bool LevelMaterials(int piece, int level, out Span<uint> destination) { destination = default; return false; }
            public override SdfMesh? Finish() { m_shape = null; return null; }
            // The blocks a failed decode sized hold nothing, so they are freed rather than kept while the storage is lost.
            public override void Abandon() {
                if (m_shape is null) { return; }
                m_shape = null;
                foreach (var (_, block, _, _) in normals) { block.Free(); }
                foreach (var (_, _, block, _, _) in levels) { block.Free(); }
            }
        }

        // The storage waiting for a restore, worked off by at most MaxWorkers pool items at once, so a rebuilt region's
        // thousand restores never fill the pool with blocking reads and decodes.
        private static readonly ConcurrentQueue<Restorable> Waiting = new();
        private static readonly int MaxWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 4));

        private static int Workers;

        private static void Enqueue(Restorable restorable) {
            Waiting.Enqueue(item: restorable);
            TryStartWorker();
        }
        private static void TryStartWorker() {
            while (!Waiting.IsEmpty) {
                var workers = Volatile.Read(location: ref Workers);

                if (workers >= MaxWorkers) { return; }
                if (Interlocked.CompareExchange(comparand: workers, location1: ref Workers, value: (workers + 1)) != workers) { continue; }
                _ = ThreadPool.UnsafeQueueUserWorkItem(callBack: static _ => Work(), state: ((object?)null), preferLocal: false);
                return;
            }
        }
        private static void Work() {
            while (Waiting.TryDequeue(result: out var restorable)) { restorable.Run(); }
            _ = Interlocked.Decrement(location: ref Workers);
            // An item queued after the last dequeue and before the decrement found every worker busy.
            TryStartWorker();
        }

        private enum State { Resident, Released, Restoring, Lost }
    }
}
