using Puck.Abstractions.Counting;
using System.Collections.Concurrent;
using Puck.Abstractions.Memory;
using Puck.Assets;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// The baked meshes every residency that holds the library shares, by the geometry each was baked from
/// (<see cref="SdfMeshGeometryBuilder"/>) and the identity of the bake that meshed it
/// (<see cref="SdfMeshStore.IdentityOf"/>), so a body that crosses into another world takes the mesh its first world baked
/// instead of baking it again, and a bake whose identity moved (an edit to its code or kernels) never takes a mesh an
/// earlier identity baked. A program numbers its palette as its emitters register materials, so one body's shapes
/// carry other material ids in another world's program; the geometry names each material by its place among the group's
/// own materials, and a mesh taken into a program whose group carries other ids has its triangle materials renumbered by
/// place.
/// <para>A library over a store (<see cref="Disk"/>) keeps what it bakes between runs: every mesh it stores is written
/// there under its bake's identity, and a bake takes a geometry the store holds from disk (<see cref="TryLoad"/>) instead
/// of baking it again.</para>
/// <para>The library retains meshes within <see cref="ByteBudget"/>, measured as the storage each retained mesh is
/// charged to the process account (<see cref="SdfMesh.StorageBytes"/>), evicting the oldest first. Its members are
/// thread-safe: a group's mesh lands from the thread pool.</para>
/// </summary>
/// <param name="store">The store the library keeps its meshes in between runs, or <see langword="null"/> to keep them for
/// the run alone.</param>
/// <param name="byteBudget">The retained byte budget, or null for the machine-derived default.</param>
/// <param name="allocator">The host allocator for published payloads; null keeps an offline library managed.</param>
public sealed class SdfMeshLibrary(SdfMeshStore? store = null, long? byteBudget = null, IAllocator? allocator = null) : IDisposable {
    /// <summary>Gets the maximum retained bytes; live residencies retain their own in-use meshes independently.</summary>
    public long ByteBudget => Math.Max(val1: 0, val2: Math.Min(val1: (byteBudget ?? Account.CacheBudget), val2: (Account.Read().Budget / 16)));

    /// <summary>Gets the allocator supplied by the host, or null for a managed-only offline library.</summary>
    public IAllocator? Allocator { get; } = allocator;

    /// <summary>Gets the single process byte-accounting authority.</summary>
    public MemoryAccount Account => MemoryAccount.Shared;

    private bool m_clearing;
    private bool m_listening;

    /// <summary>Gets the storage bytes of the meshes the library retains.</summary>
    public long RetainedBytes { get { lock (m_gate) { return m_held.Weight; } } }

    private readonly Lock m_gate = new();
    // The geometries held, least recently taken first, each weighed by the storage of the meshes it holds. Every use is
    // under m_gate; an entry leaving it, by budget or removal, runs Release.
    private readonly ContentAddressedLruCache<(UInt128 Identity, SdfMeshGeometry Geometry), Held> m_held = new(
        capacity: int.MaxValue,
        getWeight: static held => held.Bytes,
        onEvicted: static held => held.Library.Release(held: held)
    );
    // The geometry the latest mesh of each dynamic form was held under, by bake identity (TryTakeForm).
    private readonly Dictionary<(UInt128 Identity, UInt128 Form), SdfMeshGeometry> m_forms = [];
    // The forms of m_forms each held geometry is the latest of, so Release drops them without scanning every form.
    private readonly Dictionary<(UInt128 Identity, SdfMeshGeometry Geometry), HashSet<UInt128>> m_formsOf = [];
    // Groups sharing geometry also share its disk decode; the variants below hold only material renumberings.
    private readonly ConcurrentDictionary<(UInt128 Identity, SdfMeshGeometry Geometry), Lazy<StoredMesh>> m_reads = new();

    /// <summary>Gets the store the library keeps its meshes in between runs, or <see langword="null"/>.</summary>
    public SdfMeshStore? Disk { get; } = store;

    [Puck.SideTable(reason: "the drawn tiers of each world a presentation draws twice, which leave with the world's key")]
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, TierBoard> m_tiers = [];

    /// <summary>Returns the tiers the residency that draws a world for the presentation draws each of its groups at,
    /// by the group's shape: what a residency drawing a second scene of that world follows
    /// (<see cref="SdfFrameContent.DemandWorld"/>).</summary>
    /// <param name="world">What names the world, compared by reference.</param>
    /// <returns>The world's board, the same one for every caller.</returns>
    internal TierBoard TiersOf(object world) => m_tiers.GetValue(key: world, createValueCallback: static _ => new TierBoard());

    /// <summary>The tier one residency draws each of its groups at, by the group's shape and where it stands in the
    /// world (<see cref="PlaceOf"/>), which the residencies drawing a second scene of its world read: a shape many
    /// placements share is drawn at as many tiers as its copies' distances ask, so the shape alone names no group.
    /// Where two groups share a shape and a place, the finest. Thread-safe: the publisher writes it whole under a lock
    /// the readers take.</summary>
    internal sealed class TierBoard {
        /// <summary>The cells a metre is cut into where a group's place is told from another's: 64, well under the
        /// spacing of any two placements and well over the difference rounding makes between two scenes' copies of one
        /// placement's transform.</summary>
        public const float PlaceCellsPerMetre = 64f;

        private readonly Lock m_gate = new();
        private readonly Dictionary<(UInt128 Shape, long Place), int> m_tiers = [];
        private long m_revision;

        /// <summary>Returns the cell a group's world-space box is centred in, as one number: the place a group stands
        /// at, alike for the same placement in two scenes of one world and apart for two copies of one shape.</summary>
        /// <param name="center">The centre of the group's box, in world space.</param>
        /// <returns>The place.</returns>
        public static long PlaceOf(System.Numerics.Vector3 center) {
            static long Cell(float value) => (((long)MathF.Round(x: (value * PlaceCellsPerMetre))) & 0x1FFFFFL);

            return ((Cell(value: center.X) | (Cell(value: center.Y) << 21)) | (Cell(value: center.Z) << 42));
        }

        /// <summary>Gets a count that moves each time the board is published.</summary>
        public long Revision => Interlocked.Read(location: ref m_revision);

        /// <summary>Replaces the board's tiers with those a publisher lists.</summary>
        /// <typeparam name="TState">The publisher's state.</typeparam>
        /// <param name="state">The publisher's state.</param>
        /// <param name="list">Lists each group's shape, place and tier through the callback it is handed.</param>
        public void Publish<TState>(TState state, Action<TState, Action<UInt128, long, int>> list) {
            lock (m_gate) {
                m_tiers.Clear();
                list(arg1: state, arg2: (shape, place, tier) => {
                    ref var held = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_tiers, exists: out var exists, key: (shape, place));

                    held = (exists ? Math.Min(val1: held, val2: tier) : tier);
                });
                _ = Interlocked.Increment(location: ref m_revision);
            }
        }
        /// <summary>Returns the tier the publisher draws a group at.</summary>
        /// <param name="shape">The group's shape.</param>
        /// <param name="place">The group's place (<see cref="PlaceOf"/>).</param>
        /// <param name="tier">The tier, when this returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> where the publisher draws no group of the shape at the place.</returns>
        public bool TryGet(UInt128 shape, long place, out int tier) {
            lock (m_gate) { return m_tiers.TryGetValue(key: (shape, place), value: out tier); }
        }
    }

    // The trace judge's verdicts every bake holding the library reads and gives, by bake identity and the two
    // geometries: a verdict is a function of the two meshes and the judge alone, so a floor one residency judged is
    // judged for every other, and the store keeps it between runs.
    private readonly ConcurrentDictionary<(UInt128 Identity, SdfMeshGeometry Floor, SdfMeshGeometry Finer), bool> m_verdicts = new();

    /// <summary>Returns the trace judge's verdict on a floor and a finer mesh of one group, where a bake holding the
    /// library gave it in this run or the store kept it from an earlier one.</summary>
    /// <param name="identity">The identity of the bake that meshed both meshes.</param>
    /// <param name="floor">The floor's geometry.</param>
    /// <param name="finer">The finer mesh's geometry.</param>
    /// <returns>Whether the floor was admitted, or <see langword="null"/> when no verdict on the pair is known.</returns>
    /// <remarks>Reads the judge's code identity (<see cref="SdfMeshTraceJudge.CodeIdentity"/>), whose first read walks
    /// the running build's modules: call it off the frame's thread.</remarks>
    internal bool? TraceVerdict(UInt128 identity, SdfMeshGeometry floor, SdfMeshGeometry finer) {
        if (m_verdicts.TryGetValue(key: (identity, floor, finer), value: out var known)) { return known; }
        if (Disk?.TraceVerdict(finer: finer, floor: floor, identity: identity, judge: SdfMeshTraceJudge.CodeIdentity) is not { } kept) { return null; }
        _ = m_verdicts.TryAdd(key: (identity, floor, finer), value: kept);

        return kept;
    }
    /// <summary>Keeps the trace judge's verdict on a floor and a finer mesh for every bake holding the library, and in
    /// <see cref="Disk"/> between runs.</summary>
    /// <param name="identity">The identity of the bake that meshed both meshes.</param>
    /// <param name="floor">The floor's geometry.</param>
    /// <param name="finer">The finer mesh's geometry.</param>
    /// <param name="admitted">The verdict.</param>
    internal void KeepTraceVerdict(UInt128 identity, SdfMeshGeometry floor, SdfMeshGeometry finer, bool admitted) {
        if (m_verdicts.TryAdd(key: (identity, floor, finer), value: admitted)) { Disk?.KeepTraceVerdict(admitted: admitted, finer: finer, floor: floor, identity: identity, judge: SdfMeshTraceJudge.CodeIdentity); }
    }
    /// <summary>Gets the counts of the groups planned again as a frame input moved (<see cref="SdfMeshInputs"/>) every bake
    /// holding the library counts into, so one source reads a presentation's residencies together.</summary>
    public WorkCounterSet Inputs { get; } = SdfMeshInputs.Counters();
    /// <summary>Gets the skinned meshes' counts (<see cref="SdfMeshSkin"/>) every bake holding the library counts into:
    /// each skinned mesh a group starts drawing, and its vertices by vertex-stage path.</summary>
    public WorkCounterSet Skins { get; } = SdfMeshSkin.Counters();
    /// <summary>Gets the counts of the groups that waited on a bake of their geometry another group already ran
    /// (<see cref="SdfMeshBakeShare"/>) every bake holding the library counts into: the groups joined, those that took
    /// the leader's mesh, the samples that saved, and those whose leader left them to start again.</summary>
    public WorkCounterSet Shares { get; } = SdfMeshBakeShare.Counters();
    /// <summary>Gets what every bake holding the library does about a rung its store lacks
    /// (<see cref="SdfMeshCachePolicy"/>); <see cref="SdfMeshCachePolicy.Demand"/> unless the host says otherwise.</summary>
    public SdfMeshCachePolicy Policy { get; init; }
    /// <summary>Gets the counts of what the bakes holding the library asked of its cache beyond a hit
    /// (<see cref="SdfMeshCache"/>): the lattices dispatched, the shapes baked, and the rungs missed, retried and
    /// filled.</summary>
    public WorkCounterSet Cache { get; } = SdfMeshCache.Counters();

    /// <summary>Returns whether the library or its store holds a geometry's mesh, or its empty result, under a bake's
    /// identity, as <see cref="SdfMeshStore.Holds"/> answers for the store: with no file read once the identity's
    /// entries are listed. A <see cref="SdfMeshCachePolicy.Ladder"/> or <see cref="SdfMeshCachePolicy.Stored"/> bake
    /// decides what to bake or draw from it.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <returns><see langword="true"/> when a take or a load of the geometry would find it.</returns>
    public bool Holds(UInt128 identity, SdfMeshGeometry geometry) => (HoldsAny(geometry: geometry, identity: identity) || (Disk?.Holds(geometry: geometry, identity: identity) ?? false));
    /// <summary>Returns the geometry a dynamic form's latest mesh is also stored under (<see cref="Store"/>), so a later
    /// run takes the mesh of the form at whatever pose its other slots stand in, as <see cref="TryTakeForm"/> takes one
    /// this run holds.</summary>
    /// <param name="form">The form (<see cref="SdfMeshGeometryBuilder.Form"/>).</param>
    /// <returns>The geometry the store keys the form's mesh by.</returns>
    public static SdfMeshGeometry FormGeometry(UInt128 form) => new(Dynamic: true, Hash: form);
    /// <summary>Returns whether the library holds a mesh of a dynamic form, baked at any pose, or its store holds one
    /// (<see cref="FormGeometry"/>).</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="form">The form.</param>
    /// <returns><see langword="true"/> when a group of the form would take or load a mesh.</returns>
    public bool HoldsForm(UInt128 identity, UInt128 form) {
        lock (m_gate) {
            if (m_forms.ContainsKey(key: (identity, form))) { return true; }
        }

        return (Disk?.Holds(geometry: FormGeometry(form: form), identity: identity) ?? false);
    }

    // The bake running each geometry under a bake identity, which a group of any bake holding the library waits on rather
    // than bake it again (Share). Under m_gate, which is also the one gate a share is joined, left and resolved under.
    // Never trimmed: an entry is work in flight, not a cache, and leaves when its leader resolves it or a waiter deserts it.
    private readonly Dictionary<(UInt128 Identity, SdfMeshGeometry Geometry), SdfMeshBakeShare> m_baking = [];

    /// <summary>Returns the bake of a geometry a group waits on, joining it, or opens one the group leads when no bake of
    /// the geometry runs under the identity: the one door a group starting a bake passes, so two residencies that plan
    /// the same geometry before either lands it bake it once. A geometry the library came to hold since the group last
    /// asked (a leader landed between) answers a share already resolved, which the group takes from at once.</summary>
    /// <param name="identity">The bake identity the group bakes under.</param>
    /// <param name="geometry">The group's sealed geometry.</param>
    /// <param name="bake">The group's bake.</param>
    /// <param name="group">The group.</param>
    /// <param name="waiter">The group's place among the share's waiters when it joins a running or landed bake, or
    /// <see langword="null"/> when it leads the share returned.</param>
    /// <param name="landed">Whether the share returned has already landed, so no leader hands the group back.</param>
    /// <returns>The share the group waits on or leads.</returns>
    internal SdfMeshBakeShare Share(UInt128 identity, SdfMeshGeometry geometry, SdfMeshPathBake bake, object group, out SdfMeshBakeShare.Waiter? waiter, out bool landed) {
        lock (m_gate) {
            landed = false;
            if (m_baking.TryGetValue(key: (identity, geometry), value: out var running)) {
                waiter = new SdfMeshBakeShare.Waiter(bake: bake, group: group);
                running.Waiters.Add(item: waiter);
                running.Recount();
                return running;
            }
            if (m_held.Contains(hash: (identity, geometry)) || Revive(key: (identity, geometry))) {
                waiter = new SdfMeshBakeShare.Waiter(bake: bake, group: group);
                landed = true;
                return new SdfMeshBakeShare(bake: bake, geometry: geometry, identity: identity) { Resolved = true };
            }

            var share = new SdfMeshBakeShare(bake: bake, geometry: geometry, identity: identity);

            m_baking.Add(key: (identity, geometry), value: share);
            waiter = null;
            return share;
        }
    }
    /// <summary>Resolves a share whose leader is done or retired: no group joins it after this, and every group that
    /// waited is handed back to take the leader's mesh from the library or start the geometry again.</summary>
    /// <param name="share">The share.</param>
    /// <param name="samples">The samples the leader dispatched for the geometry.</param>
    /// <returns>The groups that waited, each exactly once; empty for a share resolved before.</returns>
    internal SdfMeshBakeShare.Waiter[] Resolve(SdfMeshBakeShare share, long samples) {
        lock (m_gate) {
            if (share.Resolved) { return []; }
            share.Resolved = true;
            share.Samples = samples;
            Unlist(share: share);

            var waiters = share.Waiters.ToArray();

            share.Waiters.Clear();
            share.Recount();
            return waiters;
        }
    }
    /// <summary>Takes a waiting group off a share: a group retired while it waited, or one deserting a leader no frame
    /// drives, which also closes the share to later groups so the next to start leads a bake of its own.</summary>
    /// <param name="share">The share.</param>
    /// <param name="waiter">The group's place among its waiters.</param>
    /// <param name="desert">Whether later groups stop joining the share.</param>
    internal void Leave(SdfMeshBakeShare share, SdfMeshBakeShare.Waiter waiter, bool desert) {
        lock (m_gate) {
            _ = share.Waiters.Remove(item: waiter);
            share.Recount();
            if (desert) { Unlist(share: share); }
        }
    }
    /// <summary>Returns the highest priority a share's waiters were last measured at, which its leader ranks by when that
    /// is higher than its own.</summary>
    /// <param name="share">The share.</param>
    /// <returns>The priority; zero with no waiter.</returns>
    internal float MostWanted(SdfMeshBakeShare share) {
        if (!share.HasWaiters) { return 0f; }
        lock (m_gate) {
            var most = 0f;

            foreach (var waiter in share.Waiters) { most = MathF.Max(x: most, y: waiter.Priority); }
            return most;
        }
    }
    // Removes a share from the registry while it is the one listed for its geometry, under the gate.
    private void Unlist(SdfMeshBakeShare share) {
        var key = (share.Identity, share.Geometry);

        if (m_baking.TryGetValue(key: key, value: out var listed) && ReferenceEquals(objA: listed, objB: share)) { _ = m_baking.Remove(key: key); }
    }

    /// <summary>Gets how many geometries the library holds.</summary>
    public int Count {
        get {
            lock (m_gate) { return m_held.Count; }
        }
    }

    /// <summary>Stores a group's mesh, baked from its geometry, with the material ids its group's shapes carry in the order
    /// the geometry numbers them, and writes it to <see cref="Disk"/> under the bake's identity; a geometry already held
    /// keeps its mesh.</summary>
    /// <param name="geometry">The geometry the mesh was baked from.</param>
    /// <param name="mesh">The mesh, whose triangle materials are ids of <paramref name="materials"/> or ids no shape of the
    /// group carries.</param>
    /// <param name="materials">The group's material ids, by their place in the geometry.</param>
    /// <param name="identity">The identity of the bake that meshed it (<see cref="SdfMeshStore.IdentityOf"/>).</param>
    /// <param name="form">The form of the dynamic group the mesh was baked for (<see cref="SdfMeshGeometryBuilder.Form"/>),
    /// which <see cref="TryTakeForm"/> then answers with it, or <see langword="null"/> for a group in world space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <returns>An owned mesh claim. The caller releases it after its last borrowed view ends.</returns>
    /// <remarks>A library with a native allocator and a store publishes the mesh the store's entry answers (positions on
    /// the entry's grid, normals through its octahedral words), so a mesh baked in this run draws what a later run loads,
    /// and its normals and coarser levels can be let go and restored from the entry (<see cref="SdfMesh.DescribeRestores"/>);
    /// the answer is written from the baked mesh and the entry is not read back. A write the store refuses publishes the
    /// baked mesh, which keeps them.</remarks>
    public SdfMesh Store(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials, UInt128 identity = default, UInt128? form = null) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        SdfMeshStoreCodec.Positions? stored = null;
        var written = ((Allocator is not null) && (Disk is { } disk) && !HoldsAny(geometry: geometry, identity: identity) &&
            disk.Write(geometry: geometry, identity: identity, materials: materials, mesh: mesh, stored: out stored));

        // A published mesh restores its parts from its identity's set, which this process pins (a lease the store could not
        // open leaves a restore that misses to bake the group again).
        if (written) {
            _ = Disk!.Hold(identity: identity);
            // A dynamic group's geometry holds its other slots' poses, which no later run stands in again: its mesh is
            // stored under its form too, the latest replacing the one before.
            if ((form is { } shapes) && (FormGeometry(form: shapes) != geometry)) { _ = Disk.Write(geometry: FormGeometry(form: shapes), identity: identity, materials: materials, mesh: mesh); }
        }

        var owned = (written
            ? mesh.NativeCopy(Allocator, Account, restore: (this, identity, geometry), stored: stored)
            : CopyUnstored(mesh));

        try {
            if (Hold(form: form, geometry: geometry, identity: identity, materials: materials, mesh: owned) && !written) {
                _ = Disk?.Write(geometry: geometry, identity: identity, materials: materials, mesh: owned);
            }
            return owned;
        } catch { owned.Release(); throw; }
    }

    // A caller-owned copy without a disk entry or a library claim.
    internal SdfMesh CopyUnstored(SdfMesh mesh) => mesh.NativeCopy(Allocator, Account);

    private bool HoldsAny(SdfMeshGeometry geometry, UInt128 identity) {
        lock (m_gate) { return (m_held.Contains(hash: (identity, geometry)) || Revive(key: (identity, geometry))); }
    }

    /// <summary>Takes the latest mesh the library holds of a dynamic group's form, baked at whatever pose its other slots
    /// stood in (<see cref="SdfMeshGeometryBuilder.Form"/>): the same shapes, which the mesh's skin carries through each
    /// member's slot (<see cref="SdfMesh.Members"/>), renumbered for the taking group's materials as
    /// <see cref="TryTake(SdfMeshGeometry, int[], out SdfMesh?, UInt128)"/> renumbers. A body crossing into another world mid-stride takes the mesh its other pose
    /// baked.</summary>
    /// <param name="form">The form.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="identity">The taking bake's identity; only a mesh baked under it is taken.</param>
    /// <returns><see langword="true"/> when the library holds a mesh of the form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTakeForm(UInt128 form, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        SdfMeshGeometry geometry;

        lock (m_gate) {
            if (!m_forms.TryGetValue(key: (identity, form), value: out geometry)) {
                mesh = null;
                return false;
            }
        }

        return TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh);
    }
    /// <summary>Records that a geometry's bake found no surface, so a later group of it under the same bake identity, in
    /// another program or residency, finishes with no mesh rather than baking again.</summary>
    /// <param name="geometry">The geometry that meshed nothing.</param>
    /// <param name="identity">The identity of the bake that found no surface.</param>
    /// <param name="persist">Whether the empty result is also written to <see cref="Disk"/> under
    /// <paramref name="identity"/>, to be kept between runs.</param>
    public void StoreEmpty(SdfMeshGeometry geometry, UInt128 identity = default, bool persist = false) {
        lock (m_gate) {
            if (m_held.Contains(hash: (identity, geometry))) { return; }

            Admit(held: new Held(geometry: geometry, identity: identity, library: this, variants: []));
        }
        if (persist) { _ = Disk?.Write(geometry: geometry, identity: identity, materials: [], mesh: null); }
    }
    /// <summary>Returns whether a geometry's bake under an identity found no surface (<see cref="StoreEmpty"/>).</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="identity">The bake's identity.</param>
    /// <returns><see langword="true"/> when the library holds the geometry as one that meshes nothing.</returns>
    public bool HoldsEmpty(SdfMeshGeometry geometry, UInt128 identity = default) {
        lock (m_gate) { return (m_held.TryPeek(hash: (identity, geometry), value: out var held) && (held.Variants.Count == 0)); }
    }
    /// <summary>Returns whether <see cref="Disk"/> may hold a geometry's mesh under a bake's identity, as
    /// <see cref="SdfMeshStore.MayHold"/> answers it: from the store's known entries, with no file read.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <returns><see langword="false"/> when the store is known to hold no entry for it.</returns>
    public bool MayHold(UInt128 identity, SdfMeshGeometry geometry) => (Disk?.MayHold(geometry: geometry, identity: identity) ?? false);
    /// <summary>Takes a geometry's mesh from <see cref="Disk"/> for a group whose shapes carry <paramref name="materials"/>,
    /// holding it for the run as <see cref="Store"/> would, then taking it as <see cref="TryTake(SdfMeshGeometry, int[], out SdfMesh?, UInt128)"/> does. Groups share
    /// one decode per identity and geometry until the library empties; a failed read is retried next time.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, or null for a sound entry whose bake found no surface.</param>
    /// <param name="refusal">Why the store refused the entry it held, or <see langword="null"/>.</param>
    /// <param name="form">The form of the dynamic group loading it, as <see cref="Store"/> takes it, or
    /// <see langword="null"/>.</param>
    /// <param name="declined">Whether the store holds a sound entry the read did not take because the account had no
    /// room for its workspace (<see cref="SdfMeshStore.WorkspaceRefusal"/>): a later read may take it.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryLoad(UInt128 identity, SdfMeshGeometry geometry, int[] materials, out SdfMesh? mesh, out string? refusal, out bool declined, UInt128? form = null) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        mesh = null;
        refusal = null;
        declined = false;
        if (Disk is null) { return false; }
        // A geometry the library already holds under this identity is served from memory: the decode is shared until
        // the library evicts it, and the store is read again only for a geometry it does not hold.
        if (HoldsEmpty(geometry: geometry, identity: identity)) { return true; }
        if (TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out var held)) {
            if (form is { } heldForm) { lock (m_gate) { SetForm(form: heldForm, geometry: geometry, identity: identity); } }
            mesh = held;
            return true;
        }
        var key = (Identity: identity, Geometry: geometry);

        while (true) {
            var pending = m_reads.GetOrAdd(key, static (key, state) => new Lazy<StoredMesh>(valueFactory: () => state.Library.Decode(disk: state.Disk, form: state.Form, geometry: key.Geometry, identity: key.Identity, taking: state.Taking)), (Library: this, Disk, Form: form, Taking: materials));

            try {
                var result = pending.Value;

                refusal = result.Refusal;
                declined = result.Declined;
                if (!result.Found) { return false; }
                if (result.Mesh is null) {
                    StoreEmpty(geometry: geometry, identity: identity);
                    return true;
                }

                // The decode already holds the mesh (Decode), so the groups that shared it take it from the library. One of
                // them owns the decode's claim and may use the mesh itself if the library let go of the geometry in between.
                var owns = result.TakeOwnership();

                try {
                    if (TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh)) {
                        if (form is { } heldForm) { lock (m_gate) { SetForm(form: heldForm, geometry: geometry, identity: identity); } }
                        return true;
                    }
                    if (owns) {
                        // The library let go of the geometry between the hold and the take (a trim): use the decoded mesh.
                        if (form is { } lostForm) { lock (m_gate) { SetForm(form: lostForm, geometry: geometry, identity: identity); } }
                        mesh = (result.Materials.AsSpan().SequenceEqual(other: materials) ? result.Mesh.Acquire() : Renumber(from: result.Materials, mesh: result.Mesh, to: materials));
                        return true;
                    }
                } finally { if (owns) { result.Mesh.Release(); } }
            } finally {
                // Lazy remembers a decode exception. End this attempt even when it throws, leaving any newer attempt alone.
                _ = m_reads.TryRemove(item: new KeyValuePair<(UInt128, SdfMeshGeometry), Lazy<StoredMesh>>(key: key, value: pending));
            }
        }
    }
    // Decodes a geometry's entry into the storage its mesh keeps, with the taking group's ids written in it when the mesh can
    // be renumbered back from them (SdfMeshStoreDecoder.TryDecode), and holds the result for the run. The decode's own claim
    // outlives the hold, for the groups that share the decode to take the mesh from.
    private StoredMesh Decode(SdfMeshStore disk, UInt128 identity, SdfMeshGeometry geometry, int[] taking, UInt128? form) {
        // A native mesh lets its parts go and restores them from this set, so the set stays while this process runs.
        var storage = ((Allocator is { } native) ? SdfMesh.Native(allocator: native, account: Account, restore: (this, identity, geometry)) : new SdfMeshManagedStorage());
        var loaded = disk.TryReadInto(declined: out var declined, geometry: geometry, identity: identity, materials: out var held, mesh: out var mesh, refusal: out var why, storage: storage, taking: taking);

        if (loaded && (mesh is not null)) {
            if (Allocator is not null) { _ = disk.Hold(identity: identity); }
            _ = Hold(form: form, geometry: geometry, identity: identity, materials: held, mesh: mesh);
        }

        return new StoredMesh(declined: declined, found: loaded, materials: held, mesh: mesh, refusal: why);
    }
    /// <summary>Takes the mesh of a geometry for a group whose shapes carry <paramref name="materials"/>: the stored mesh
    /// when its group carried the same ids, and otherwise the stored mesh with each triangle's material renumbered from the
    /// id its group carried at a place to the id <paramref name="materials"/> holds there, kept for the next group that
    /// carries the same ids.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="identity">The taking bake's identity; only a mesh baked under it is taken.</param>
    /// <returns><see langword="true"/> when the library holds the geometry under <paramref name="identity"/>; a renumbered
    /// copy the host memory account has no room for is a miss, and the taking group bakes or loads the geometry instead.</returns>
    /// <remarks>A successful take returns an owned mesh claim; the consumer releases it after its last borrowed view ends.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTake(SdfMeshGeometry geometry, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, UInt128 identity = default) =>
        TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh, refused: out _);
    // As the public TryTake, also answering the refusal of a renumbered copy the account had no room for, which a caller
    // that would rather wait than bake the geometry again parks on (SdfMeshPathBake.TakeLanded).
    internal bool TryTake(SdfMeshGeometry geometry, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out HostMemoryRefusedException? refused, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        var key = (identity, geometry);
        Variant source;

        refused = null;

        lock (m_gate) {
            if (!m_held.Contains(hash: key)) { _ = Revive(key: key); }
            if (!m_held.TryGet(hash: key, value: out var held) || (held.Variants.Count == 0)) {
                mesh = null;
                return false;
            }
            foreach (var variant in held.Variants) {
                if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                    mesh = variant.Mesh.Acquire();
                    return true;
                }
            }
            source = held.Variants[0];
            _ = source.Mesh.Acquire();
        }

        // The renumbered copy is made outside the gate the frame thread takes; the first variant held for these ids wins.
        SdfMesh renumbered;

        try {
            renumbered = Renumber(mesh: source.Mesh, from: source.Materials, to: materials);
        } catch (HostMemoryRefusedException refusal) {
            refused = refusal;
            mesh = null;
            return false;
        } finally {
            source.Mesh.Release();
        }
        lock (m_gate) {
            if (m_held.TryPeek(hash: key, value: out var held) && (held.Variants.Count > 0)) {
                foreach (var variant in held.Variants) {
                    if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                        mesh = variant.Mesh.Acquire();
                        renumbered.Release();
                        return true;
                    }
                }
                held.Variants.Add(item: new Variant(Materials: materials, Mesh: renumbered.Acquire(cached: true)));
                Grow(held: held, bytes: renumbered.StorageBytes);
            }
        }
        mesh = renumbered;
        return true;
    }

    // Holds a geometry's first mesh under a bake identity within the byte budget and makes it its form's latest under
    // that identity; false when it held one already.
    private bool Hold(SdfMeshGeometry geometry, UInt128 identity, SdfMesh mesh, int[] materials, UInt128? form) {
        var key = (identity, geometry);

        lock (m_gate) {
            if (form is { } shapes) { SetForm(form: shapes, geometry: geometry, identity: identity); }
            if (m_held.Contains(hash: key) || Revive(key: key)) { return false; }
            Admit(held: new Held(geometry: geometry, identity: identity, library: this, variants: [new Variant(Materials: materials, Mesh: mesh.Acquire(cached: true))]) { Bytes = mesh.StorageBytes });

            return true;
        }
    }
    // Holds a geometry within the byte budget, evicting the least recently taken geometries that exceed it, under the gate.
    private void Admit(Held held) {
        if (!m_listening) { Account.TrimRequested += Trim; m_listening = true; }
        m_held.Set(hash: (held.Identity, held.Geometry), value: held);
        m_held.TrimTo(weight: ByteBudget);
    }
    // Charges a held geometry for a mesh it gained, under the gate.
    private void Grow(Held held, long bytes) {
        _ = m_held.Reweigh(delta: bytes, hash: (held.Identity, held.Geometry));
        m_held.TrimTo(weight: ByteBudget);
    }
    // Drops a geometry's cache claims, its pending read and the forms it answers, under the gate.
    private void Evict((UInt128 Identity, SdfMeshGeometry Geometry) key) => m_held.Remove(hash: key);

    // The geometries the cache let go of (its budget, or a trim) whose meshes a residency or the mesh region may still
    // hold, each with the meshes the cache held for it and no claim on any. Invariant: one geometry under one bake
    // identity has one mesh in the process for as long as anything holds it. The cache's budget bounds what the library
    // itself keeps alive, never what it knows: a take of a geometry it let go of finds the living mesh here (Revive)
    // instead of decoding the store's entry into a second one, which a second residency of the same world would then
    // hold beside the first's, in host memory and in the mesh region. Revive is the one door, which every answer about
    // whether the library holds a geometry passes (TryTake, HoldsAny, Share, Hold). Under m_gate.
    private readonly Dictionary<(UInt128 Identity, SdfMeshGeometry Geometry), Held> m_let = [];
    // Whether a geometry is leaving because its store entry is lost (Lose), so no later take may find its meshes.
    private bool m_losing;
    private long m_revived;

    /// <summary>Gets how many times a take found the living mesh of a geometry the cache had let go of, in place of
    /// decoding the store's entry again.</summary>
    public long Revived => Interlocked.Read(location: ref m_revived);

    // Takes a geometry the cache let go of back into it, when the first mesh it held for the geometry still lives: that
    // mesh and each renumbered mesh still living are claimed again. False when the cache never held it, or its first
    // mesh has gone, and the caller reads the store. Under m_gate.
    private bool Revive((UInt128 Identity, SdfMeshGeometry Geometry) key) {
        if (!m_let.Remove(key: key, value: out var let)) { return false; }
        if (m_held.Contains(hash: key)) { return true; }
        if (!let.Variants[0].Mesh.TryAcquire(cached: true)) { return false; }

        var variants = new List<Variant>(capacity: let.Variants.Count) { let.Variants[0] };
        var bytes = let.Variants[0].Mesh.StorageBytes;

        for (var index = 1; (index < let.Variants.Count); index++) {
            if (!let.Variants[index].Mesh.TryAcquire(cached: true)) { continue; }
            variants.Add(item: let.Variants[index]);
            bytes += let.Variants[index].Mesh.StorageBytes;
        }
        _ = Interlocked.Increment(location: ref m_revived);
        Admit(held: new Held(geometry: key.Geometry, identity: key.Identity, library: this, variants: variants) { Bytes = bytes });

        return m_held.Contains(hash: key);
    }
    // What leaving the cache takes with a geometry, run by the cache under the gate.
    private void Release(Held held) {
        var key = (held.Identity, held.Geometry);

        foreach (var variant in held.Variants) { variant.Mesh.Release(cached: true); }
        _ = m_reads.TryRemove(key: key, value: out _);
        if (!m_losing && (held.Variants.Count > 0)) {
            m_let[key] = held;
            // The meshes nothing holds any more leave the table once it has grown well past what the cache holds.
            if (m_let.Count > ((4 * m_held.Count) + 4096)) {
                foreach (var (gone, entry) in m_let) { if (entry.Variants[0].Mesh.IsReleased) { _ = m_let.Remove(key: gone); } }
            }
        }
        if (m_clearing) { return; }
        if (m_formsOf.Remove(key: key, value: out var forms)) {
            foreach (var form in forms) { _ = m_forms.Remove(key: (key.Identity, form)); }
        }
    }
    // Makes a geometry the latest of a form under an identity, keeping m_formsOf its exact inverse, under the gate.
    private void SetForm(UInt128 form, SdfMeshGeometry geometry, UInt128 identity) {
        if (m_forms.TryGetValue(key: (identity, form), value: out var previous)) {
            if (previous.Equals(other: geometry)) { return; }
            if (m_formsOf.TryGetValue(key: (identity, previous), value: out var siblings) && siblings.Remove(item: form) && (siblings.Count == 0)) {
                _ = m_formsOf.Remove(key: (identity, previous));
            }
        }
        m_forms[(identity, form)] = geometry;
        if (!m_formsOf.TryGetValue(key: (identity, geometry), value: out var forms)) {
            m_formsOf.Add(key: (identity, geometry), value: forms = []);
        }
        _ = forms.Add(item: form);
    }
    /// <summary>Gets a count that moves each time a published mesh's store entry could not restore its normals and
    /// levels (<see cref="Lose"/>): a bake holding the library restarts when it moves, baking again each group whose
    /// mesh is lost.</summary>
    public long LostRevision => Interlocked.Read(location: ref m_lostRevision);

    private long m_lostRevision;

    /// <summary>Forgets a geometry whose store entry could not restore a published mesh's normals and levels, so the
    /// next group of it misses the library and the store and bakes, and moves <see cref="LostRevision"/>.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="identity">The bake identity it was held under.</param>
    /// <param name="reason">Why the restore failed, for the log.</param>
    internal void Lose(SdfMeshGeometry geometry, UInt128 identity, string reason) {
        lock (m_gate) {
            m_losing = true;
            try { Evict(key: (identity, geometry)); } finally { m_losing = false; }
            _ = m_let.Remove(key: (identity, geometry));
        }
        var lost = Interlocked.Increment(location: ref m_lostRevision);

        // An evicted set loses every geometry it held at once: the first and each doubling are named.
        if ((lost & (lost - 1)) == 0) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] lost identity={identity:x32} geometry={geometry.Hash:x32} lost={lost}: the store could not restore its normals and levels ({reason}); its groups bake again");
        }
    }

    /// <summary>Evicts cache claims; live residency and scene claims keep their payloads.</summary>
    public void Dispose() {
        lock (m_gate) {
            if (m_listening) { Account.TrimRequested -= Trim; m_listening = false; }
            Trim();
        }
    }

    // Releases every cache claim the library holds, as the account's trim requests; live residency and scene claims keep
    // their payloads, and the store keeps its entries.
    private void Trim() {
        lock (m_gate) {
            m_clearing = true;
            try { m_held.Clear(); } finally { m_clearing = false; }
            m_reads.Clear();
            m_forms.Clear();
            m_formsOf.Clear();
        }
    }
    // The mesh with each triangle material that `from` holds at a place renumbered to what `to` holds there; one neither
    // list reaches keeps its id. The geometry's equality makes the two lists the same length.
    private SdfMesh Renumber(SdfMesh mesh, int[] from, int[] to) {
        var map = new SdfMeshMaterials.Remap(from, to);
        return Allocator is { } native ? mesh.RenumberNative(map, native, Account) : RenumberManaged(mesh, map);
    }
    // A managed mesh renumbered into new managed arrays: an offline library's variants, and a decode before its native copy.
    private static SdfMesh RenumberManaged(SdfMesh mesh, SdfMeshMaterials.Remap map) {
        if (mesh.Chunks.Count != 0) { return SdfMeshChunks.Join(chunks: mesh.Chunks.Select(selector: chunk => RenumberManaged(chunk, map)).ToArray()); }
        var levels = new SdfMeshLevel[mesh.Levels.Count];

        for (var level = 0; (level < levels.Length); level++) {
            levels[level] = (mesh.Levels[level] with { TriangleMaterials = Renumbered(source: mesh.Levels[level].TriangleMaterials.Span, map: map) });
        }

        return new SdfMesh(indices: mesh.Indices, members: mesh.Members, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: Renumbered(source: mesh.TriangleMaterials.Span, map: map), vertexMembers: mesh.VertexMembers, influences: mesh.Influences) { BakeCell = mesh.BakeCell, Levels = levels };
    }
    private static uint[] Renumbered(ReadOnlySpan<uint> source, SdfMeshMaterials.Remap map) {
        var renumbered = new uint[source.Length];

        for (var triangle = 0; (triangle < source.Length); triangle++) {
            renumbered[triangle] = map.Renumber(source[triangle]);
        }

        return renumbered;
    }

    private sealed record Variant(int[] Materials, SdfMesh Mesh);
    // A geometry the library holds under a bake identity: its meshes, one per material numbering taken, and the storage
    // they are charged. None for a geometry whose bake found no surface.
    private sealed class Held(SdfMeshGeometry geometry, UInt128 identity, SdfMeshLibrary library, List<Variant> variants) {
        public long Bytes { get; init; }
        public SdfMeshGeometry Geometry { get; } = geometry;
        public UInt128 Identity { get; } = identity;
        public SdfMeshLibrary Library { get; } = library;
        public List<Variant> Variants { get; } = variants;
    }
    // One decode every group of a geometry shares. Its mesh carries the decode's own claim, which exactly one of them takes
    // over and releases (TakeOwnership).
    private sealed class StoredMesh(bool found, SdfMesh? mesh, int[] materials, string? refusal, bool declined) {
        private int m_owned;

        public bool Declined { get; } = declined;
        public bool Found { get; } = found;
        public int[] Materials { get; } = materials;
        public SdfMesh? Mesh { get; } = mesh;
        public string? Refusal { get; } = refusal;

        public bool TakeOwnership() => (Interlocked.Exchange(location1: ref m_owned, value: 1) == 0);
    }
}
