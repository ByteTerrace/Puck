using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Counting;
using Puck.Assets;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance.Baking;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>Where one prototype's bake stands.</summary>
public enum WorldBakeState : byte {
    /// <summary>The prototype is not in the definition the schedule last saw.</summary>
    Unknown = 0,
    /// <summary>The bake is queued or baking; the prototype draws through its signed-distance field meanwhile.</summary>
    Pending = 1,
    /// <summary>The bake is in the cache.</summary>
    Ready = 2,
    /// <summary>The creation has no bake: the cache holds its refusal, and it draws through its field.</summary>
    Refused = 3,
}
/// <summary>
/// Keeps a presentation's bakes current. Each frame the presenter hands it the live definition; when the definition
/// changes, every prototype's key (<see cref="WorldBakeStore.RequestsOf"/>) is looked up in memory, and a key the cache
/// does not hold is queued. Queued keys are resolved one at a time on the thread pool through
/// <see cref="BackgroundBuild{T}"/>: each is read from the cache's directory when an earlier run kept it there, and baked
/// and kept otherwise, so only a prototype whose content changed is baked again. A result is taken on the presenter's
/// thread at the next frame, which starts the next key, so bakes become ready one by one. Nothing here reads or writes
/// simulation state, and a prototype keeps drawing through its field until its bake is ready.
/// <para>A presentation that draws bakes asks <see cref="TryGetDraw"/> for a ready prototype's baked mesh and impostor,
/// decoded once per bake; the first time a bake is handed out it counts under <see cref="Drawn"/>, the counted switch from
/// the field to the bake. <see cref="Revision"/> moves whenever a bake lands or the definition changes, so a presentation
/// rebuilds what it draws.</para>
/// <para>The schedule counts its work under <see cref="SourceName"/>. Every kind is
/// <see cref="WorkClass.Pacing"/>, because what the cache already holds decides it.</para>
/// </summary>
public sealed class WorldBakeSchedule : IWorkCounterSource, IDisposable {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "sdf.bakes";

    private readonly WorldBakeStore m_store;
    private readonly WorkCounterSet m_counts;

    private readonly BackgroundBuild<List<Outcome>> m_build = new();
    private readonly List<WorldBakeRequest> m_queue = [];
    private readonly HashSet<ContentPin> m_waiting = [];
    private readonly HashSet<ContentPin> m_failed = [];
    private readonly HashSet<ContentPin> m_counted = [];
    private readonly HashSet<ContentPin> m_current = [];
    private readonly Dictionary<string, ContentPin> m_prototypes = new(comparer: StringComparer.Ordinal);
    // Each held bake's draw, decoded the first time it is asked for (null for a refusal or a bake without triangles), and
    // the bakes handed out for drawing, each counted once.
    private readonly Dictionary<ContentPin, WorldBakedDraw?> m_meshes = [];
    private readonly HashSet<ContentPin> m_drawn = [];

    private long m_revision;
    // Published together at the end of a pump, so readiness never combines states from different definitions.
    private volatile Progress m_progress;
    private bool m_ships;

    [Flags]
    private enum Progress {
        None = 0,
        Reconciled = 1,
        Ships = 2,
        Settled = 4,
    }

    private WorldBakeRequest[]? m_inFlight;
    private WorldDefinition? m_definition;

    /// <summary>Initializes a new instance of the <see cref="WorldBakeSchedule"/> class.</summary>
    /// <param name="store">The cache bakes are read from and kept in.</param>
    /// <param name="quality">The quality tier the presentation bakes at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public WorldBakeSchedule(WorldBakeStore store, SdfBakeQuality quality = WorldBakeChunk.Quality) {
        ArgumentNullException.ThrowIfNull(argument: store);

        m_store = store;
        Quality = quality;
        m_counts = new WorkCounterSet(
            kinds: Kinds,
            name: SourceName
        );
    }

    /// <summary>Gets the kind counting the prototypes whose bake the cache held without baking: shipped by a compiled
    /// world, or kept by an earlier run on this device.</summary>
    public static WorkKind Held { get; } = new(name: "sdf.bakes.held", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bakes queued for the background.</summary>
    public static WorkKind Scheduled { get; } = new(name: "sdf.bakes.scheduled", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bakes made on this device.</summary>
    public static WorkKind Baked { get; } = new(name: "sdf.bakes.baked", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the creations this device found have no bake.</summary>
    public static WorkKind Refused { get; } = new(name: "sdf.bakes.refused", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the field evaluations this device's bakes made.</summary>
    public static WorkKind Evaluations { get; } = new(name: "sdf.bakes.evaluations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bakes a presentation switched to from their field: one per bake, the first
    /// time <see cref="TryGetDraw"/> hands it out.</summary>
    public static WorkKind Drawn { get; } = new(name: "sdf.bakes.drawn", unit: "count", workClass: WorkClass.Pacing);

    /// <summary>Gets the schedule's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Order.Kinds;
    /// <inheritdoc/>
    public string Name => SourceName;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;
    /// <summary>Gets the quality tier the presentation bakes at.</summary>
    public SdfBakeQuality Quality { get; }
    /// <summary>Gets whether any bake is queued or baking.</summary>
    public bool IsBusy => ((m_queue.Count > 0) || m_build.IsPending);
    /// <summary>Gets whether the definition the schedule last pumped has every bake settled: baked, held or refused,
    /// none queued or baking. False before the first pump. Written at the end of each pump and safe to read from any
    /// thread.</summary>
    public bool IsSettled => ((m_progress & Progress.Settled) != 0);
    /// <summary>Gets whether the schedule has reconciled to a definition: false before the first pump, so a reader waits
    /// for <see cref="Ships"/> to be known. Safe to read from any thread.</summary>
    public bool HasReconciled => ((m_progress & Progress.Reconciled) != 0);
    /// <summary>Gets whether the world ships its bakes: the definition the schedule last reconciled to names at least one
    /// bake, and a bake pack supplied every one (<see cref="WorldBakeStore.IsShipped"/>), as a compiled world's
    /// <c>BAKE</c> chunk does when the world loads. A bake this machine made or kept in its cache never counts, so the
    /// answer is the same on every machine. False before the first pump. Safe to read from any thread.</summary>
    public bool Ships => ((m_progress & Progress.Ships) != 0);

    /// <summary>Reads one published pump state to decide whether the draw rule can be served: the schedule has
    /// reconciled and, when bakes draw, has settled. Safe to read from any thread.</summary>
    /// <param name="bakes">The render lever, or <see langword="null"/> to draw only shipped bakes.</param>
    /// <returns>Whether the schedule is ready for this draw rule.</returns>
    public bool IsReadyForDrawing(bool? bakes) {
        var progress = m_progress;

        return (
            ((progress & Progress.Reconciled) != 0) &&
            (!(bakes ?? ((progress & Progress.Ships) != 0)) || ((progress & Progress.Settled) != 0))
        );
    }

    /// <summary>Gets the revision of what the schedule can hand out: one more whenever a bake lands or the definition it
    /// reconciled to changed.</summary>
    public long Revision => m_revision;

    /// <summary>Takes a finished bake, reconciles to <paramref name="definition"/> when it changed, and starts the next
    /// queued bake when none is running. Called on the presenter's thread during graph preparation and frame capture.</summary>
    /// <param name="definition">The live definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void Pump(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!ReferenceEquals(objA: definition, objB: m_definition)) {
            m_progress = Progress.None;
        }

        Collect();

        if (!ReferenceEquals(objA: definition, objB: m_definition)) {
            Reconcile(definition: definition);
        }

        if (
            !m_build.IsPending &&
            (m_queue.Count > 0)
        ) {
            WorldBakeRequest[] batch = [m_queue[0]];
            var store = m_store;

            m_queue.RemoveAt(index: 0);
            m_inFlight = batch;
            m_build.Start(build: token => Resolve(batch: batch, store: store, token: token));
        }

        m_progress = Progress.Reconciled | (m_ships ? Progress.Ships : Progress.None) | (IsBusy ? Progress.None : Progress.Settled);
    }
    /// <summary>Returns where a prototype of the definition last pumped stands.</summary>
    /// <param name="prototypeId">The prototype row's id.</param>
    /// <returns>The state.</returns>
    public WorldBakeState StateOf(string prototypeId) {
        if (!m_prototypes.TryGetValue(key: prototypeId, value: out var key)) {
            return WorldBakeState.Unknown;
        }

        if (!m_store.TryGetHeld(key: key, outcome: out var outcome)) {
            return WorldBakeState.Pending;
        }

        return (((outcome.Length > 0) && (outcome.Span[0] == 0))
            ? WorldBakeState.Refused
            : WorldBakeState.Ready);
    }
    /// <summary>Returns a ready prototype's baked mesh, in the creation's engine frame like its inline mesh, and its
    /// impostor, decoded once per bake and counted under <see cref="Drawn"/> the first time it is handed out.</summary>
    /// <param name="prototypeId">The prototype row's id.</param>
    /// <param name="draw">The baked draw, when the bake is ready and holds triangles.</param>
    /// <returns><see langword="true"/> when the prototype draws its bake; otherwise it draws through its field.</returns>
    public bool TryGetDraw(string prototypeId, [NotNullWhen(returnValue: true)] out WorldBakedDraw? draw) {
        draw = null;

        if (
            !m_prototypes.TryGetValue(key: prototypeId, value: out var key) ||
            !m_store.TryGetHeld(key: key, outcome: out var outcome)
        ) {
            return false;
        }
        if (!m_meshes.TryGetValue(key: key, value: out draw)) {
            draw = ((CreationBakeCodec.TryDecode(bake: out var bake, content: outcome.Span, refusal: out _) && (bake.Mesh.Indices.Length > 0))
                ? new WorldBakedDraw(Impostor: ImpostorOf(bake: bake), Mesh: MeshOf(bake: bake))
                : null);
            m_meshes[key] = draw;
        }
        if (draw is null) {
            return false;
        }
        if (m_drawn.Add(item: key)) {
            m_counts.Count(kind: Drawn);
        }

        return true;
    }
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        m_counts.TryRead(
            kind: kind,
            value: out value
        );
    /// <summary>Reads the current total of one of this schedule's kinds.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The total counted so far.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    public long Read(WorkKind kind) =>
        m_counts.Read(kind: kind);
    /// <summary>Cancels the running bake without waiting for it, so a shutdown never waits out a bake. A bake that
    /// finishes anyway only fills the store, which outlives the schedule.</summary>
    public void Dispose() =>
        m_build.Cancel();

    private readonly record struct Outcome(ContentPin Key, bool Baked, bool Refusal, long Evaluations);

    // The drawable mesh of a bake: its positions, normals and texture coordinates, each triangle's palette entry, read
    // from the bake's material identity at the triangle's texture-coordinate centroid, which lies inside the triangle's
    // own tile, and its five surface textures.
    private static SdfMeshImpostor? ImpostorOf(SdfBake bake) {
        try {
            return new SdfMeshImpostor(impostor: bake.Impostor);
        } catch (ArgumentException) {
            return null;
        }
    }
    private static SdfMesh MeshOf(SdfBake bake) {
        var baked = bake.Mesh;
        var positions = new System.Numerics.Vector3[baked.Vertices.Length];
        var normals = new System.Numerics.Vector3[baked.Vertices.Length];
        var uvs = new System.Numerics.Vector2[baked.Vertices.Length];

        for (var index = 0; (index < positions.Length); index++) {
            positions[index] = baked.Vertices[index].Position;
            normals[index] = baked.Vertices[index].Normal;
            uvs[index] = baked.Vertices[index].Uv;
        }

        var identity = bake.Textures.FirstOrDefault(predicate: static texture => (texture.Usage == SdfBakeTextureUsage.Material));
        var materials = new uint[(baked.Indices.Length / 3)];

        if (identity is { Levels: [var texels, ..] }) {
            for (var triangle = 0; (triangle < materials.Length); triangle++) {
                var centroid = (((uvs[baked.Indices[(3 * triangle)]] + uvs[baked.Indices[((3 * triangle) + 1)]]) + uvs[baked.Indices[((3 * triangle) + 2)]]) / 3f);
                var x = Math.Clamp(
                    max: (identity.Width - 1),
                    min: 0,
                    value: ((int)(centroid.X * identity.Width))
                );
                var y = Math.Clamp(
                    max: (identity.Height - 1),
                    min: 0,
                    value: ((int)(centroid.Y * identity.Height))
                );

                materials[triangle] = texels[((y * identity.Width) + x)];
            }
        }

        return new SdfMesh(
            indices: baked.Indices,
            normals: normals,
            positions: positions,
            textures: new SdfMeshTextures(textures: bake.Textures),
            triangleMaterials: materials,
            uvs: uvs
        );
    }
    private static List<Outcome> Resolve(WorldBakeRequest[] batch, WorldBakeStore store, CancellationToken token) {
        var outcomes = new List<Outcome>(capacity: batch.Length);

        foreach (var request in batch) {
            if (token.IsCancellationRequested) {
                break;
            }

            var key = request.Key.Pin;

            if (store.TryGet(key: key, outcome: out _)) {
                outcomes.Add(item: new Outcome(Baked: false, Evaluations: 0L, Key: key, Refusal: false));
                continue;
            }

            var bytes = WorldBakeStore.Bake(request: request, work: out var work);

            _ = store.Keep(key: key, outcome: bytes);
            outcomes.Add(item: new Outcome(Baked: true, Evaluations: work.FieldEvaluations, Key: key, Refusal: (bytes[0] == 0)));
        }

        return outcomes;
    }
    private void Collect() {
        if (!m_build.TryTake(error: out _, result: out var outcomes)) {
            return;
        }

        m_revision++;

        foreach (var outcome in (outcomes ?? [])) {
            m_waiting.Remove(item: outcome.Key);

            if (!outcome.Baked) {
                m_counts.Count(kind: Held);
            } else if (outcome.Refusal) {
                m_counts.Count(kind: Refused);
            } else {
                m_counts.Count(kind: Baked);
            }

            m_counts.Add(amount: outcome.Evaluations, kind: Evaluations);
        }

        // A key the batch returned no outcome for failed to bake; it is not tried again, and its prototype keeps drawing
        // through its field.
        foreach (var request in (m_inFlight ?? [])) {
            if (m_waiting.Remove(item: request.Key.Pin)) {
                m_failed.Add(item: request.Key.Pin);
            }
        }

        m_inFlight = null;
    }
    private void Reconcile(WorldDefinition definition) {
        m_definition = definition;
        m_revision++;
        m_prototypes.Clear();
        m_current.Clear();

        var shipped = 0;
        var named = 0;

        var requests = WorldBakeStore.RequestsOf(definition: definition, quality: Quality);

        for (var index = 0; (index < requests.Count); index++) {
            var request = requests[index];
            var key = request.Key.Pin;

            m_prototypes[request.PrototypeId] = key;
            m_current.Add(item: key);
            named++;

            if (m_store.IsShipped(key: key, prototype: definition.Creations[index])) {
                shipped++;
            }
            if (m_store.TryGetHeld(key: key, outcome: out _)) {
                if (m_counted.Add(item: key)) {
                    m_counts.Count(kind: Held);
                }

                continue;
            }

            if (
                m_failed.Contains(item: key) ||
                !m_waiting.Add(item: key)
            ) {
                continue;
            }

            m_counted.Add(item: key);
            m_queue.Add(item: request);
            m_counts.Count(kind: Scheduled);
        }

        // A queued bake of a creation the definition no longer carries is dropped before it starts.
        foreach (var request in m_queue) {
            if (!m_current.Contains(item: request.Key.Pin)) {
                m_waiting.Remove(item: request.Key.Pin);
            }
        }

        _ = m_queue.RemoveAll(match: request => !m_current.Contains(item: request.Key.Pin));
        m_ships = ((named > 0) && (shipped == named));
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Held,
            Scheduled,
            Baked,
            Refused,
            Evaluations,
            Drawn,
        ];
    }
}
