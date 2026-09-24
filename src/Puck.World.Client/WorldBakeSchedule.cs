using Puck.Abstractions.Counting;
using Puck.Assets;
using Puck.Hosting;
using Puck.SignedDistance.Baking;

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

    /// <summary>Takes a finished bake, reconciles to <paramref name="definition"/> when it changed, and starts the next
    /// queued bake when none is running. Called once per produced frame, on the presenter's thread.</summary>
    /// <param name="definition">The live definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void Pump(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

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
        m_prototypes.Clear();
        m_current.Clear();

        foreach (var request in WorldBakeStore.RequestsOf(definition: definition, quality: Quality)) {
            var key = request.Key.Pin;

            m_prototypes[request.PrototypeId] = key;
            m_current.Add(item: key);

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
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Held,
            Scheduled,
            Baked,
            Refused,
            Evaluations,
        ];
    }
}
