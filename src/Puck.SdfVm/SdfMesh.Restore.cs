using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// A published mesh's normals and coarser levels' indices are read only to write the mesh's words into a mesh region
// (SdfMeshRegion.WriteMesh). A mesh the library published from its store therefore lets them go once no region owes its
// words, and takes them back from the store when a region owes them again: a relocation, a grown draw reserve, another
// residency's region, or a renumbered variant landing.
//
// Invariant: those parts are read only while a need is held (Need, by the region's owed set from placement until the
// words land) and the storage is resident, and they are freed only when no need is held. Enforced in one place: the
// storage below, which counts needs, frees at zero and restores before it reports resident, under one gate. A part read
// while freed throws, since each is an OwnedBuffer whose memory views read past its zero length; a view taken by a chunk
// or a renumbered variant reads the restored storage again, the buffer being the view's memory manager. Not enforced:
// that every reader holds a need; the readers are the region writes on the frame thread, which the owed set covers.
public sealed partial record SdfMesh {
    private Restorable? m_restorable;

    // A mesh prints its counts: its joined indices may be absent and its normals and levels let go.
    private bool PrintMembers(System.Text.StringBuilder builder) {
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"Vertices = {Positions.Length}, Triangles = {TriangleCount}, Levels = {Levels.Count}, Chunks = {Chunks.Count}, Resident = {Resident}");
        return true;
    }

    /// <summary>Gets whether the mesh's normals and coarser levels' indices are in memory: always, unless the library
    /// published it from its store and no region has needed its words since they landed.</summary>
    internal bool Resident => m_restorable?.Resident ?? true;
    /// <summary>Gets whether the store could not restore the mesh's normals and levels, so the mesh can never be written
    /// into a region again and its group bakes again (<see cref="SdfMeshLibrary.LostRevision"/>).</summary>
    internal bool Lost => m_restorable?.Lost ?? false;
    /// <summary>Holds a need for the mesh's normals and coarser levels' indices: they stay in memory, or come back from the
    /// store, until the matching <see cref="Unneed"/>.</summary>
    internal void Need() => m_restorable?.Need();
    /// <summary>Releases a need <see cref="Need"/> took; the last one lets the parts go.</summary>
    internal void Unneed() => m_restorable?.Unneed();

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

        public bool Resident => (m_state == State.Resident);
        public bool Lost => (m_state == State.Lost);

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
            _ = ThreadPool.UnsafeQueueUserWorkItem(callBack: static restorable => restorable.Restore(), state: this, preferLocal: false);
        }
        public void Unneed() {
            lock (m_gate) {
                if (m_needs <= 0) { throw new InvalidOperationException(message: "A mesh need was released twice."); }
                if ((--m_needs == 0) && (m_state == State.Resident) && (m_bytes > 0)) { FreeUnderGate(); }
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

        // Reads the store entry the mesh was published from, off the frame thread, and copies the parts back. The store
        // answers what it answered at publication (SdfMeshLibrary.Store publishes what it reads back), so the words a
        // region writes again are the words it wrote first.
        private void Restore() {
            SdfMesh? stored = null;
            var read = false;

            try {
                read = ((library.Disk is { } disk) && disk.TryRead(geometry: geometry, identity: identity, materials: out _, mesh: out stored, refusal: out _));
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)) {
                read = false;
            }

            var lost = false;

            lock (m_gate) {
                if (m_state != State.Restoring) { return; }
                if (!read || (stored is null) || !Fill(stored: stored)) {
                    m_state = State.Lost;
                    lost = true;
                } else {
                    m_state = State.Resident;
                    _ = Interlocked.Increment(location: ref Restores);
                    _ = Interlocked.Add(location1: ref RestoredBytes, value: m_bytes);
                    _ = Interlocked.Add(location1: ref ReleasedNow, value: -m_bytes);
                    if (m_needs == 0) { FreeUnderGate(); }
                }
            }
            if (lost) {
                _ = Interlocked.Increment(location: ref RestoreMisses);
                library.Lose(geometry: geometry, identity: identity);
            }
        }
        // Checks every part against the stored mesh before writing any, then copies them in.
        private bool Fill(SdfMesh stored) {
            ReadOnlyMemory<Vector3> NormalsOf(int piece) => ((piece < 0) ? stored.Normals : PieceOf(piece: piece)?.Normals ?? default);
            ReadOnlyMemory<uint> LevelOf(int piece, int level) => ((PieceOf(piece: piece) is { } mesh && (level < mesh.Levels.Count)) ? mesh.Levels[level].Indices : default);
            SdfMesh? PieceOf(int piece) => ((stored.Chunks.Count == 0) ? ((piece == 0) ? stored : null) : (((piece >= 0) && (piece < stored.Chunks.Count)) ? stored.Chunks[piece] : null));

            foreach (var (piece, _, length, _) in m_normals) {
                if (NormalsOf(piece: piece).Length != length) { return false; }
            }
            foreach (var (piece, level, _, length, _) in m_levels) {
                if (LevelOf(piece: piece, level: level).Length != length) { return false; }
            }
            foreach (var (piece, block, _, _) in m_normals) {
                var source = NormalsOf(piece: piece).Span;

                block.Resize(length: source.Length, clear: false, preserve: false);
                source.CopyTo(destination: block.GetSpan());
            }
            foreach (var (piece, level, block, _, _) in m_levels) {
                var source = LevelOf(piece: piece, level: level).Span;

                block.Resize(length: source.Length, clear: false, preserve: false);
                source.CopyTo(destination: block.GetSpan());
            }
            return true;
        }

        private enum State { Resident, Released, Restoring, Lost }
    }
}
