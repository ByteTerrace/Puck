using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Collections;
using Puck.Maths;
using Puck.Commands;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The session projection's client-side mirror — the "minimal client-side pose/state mirror" docs/architecture/worlds.md's
/// "Observation and display" names, attached to a destination instance's <c>WorldServer</c> under a
/// <c>WorldServer.AttachSink(IClientSink)</c> lease exactly like any other client. It is deliberately not
/// <c>WorldClient</c>: that type also carries a <c>PlayerRoster</c>/seat table this observation-only mirror
/// has no use for (the destination owns any arrived bodies; this observer owns no local seat table),
/// grants/machine/action state it never
/// decodes, and a correction-error easer this mirror does not reproduce — so it keeps only what a session screen's
/// projection graph actually renders: the destination's live document (its static authored geometry) plus a bounded
/// per-entity render-pose table (position, attitude, body color, and look index) for every active body the
/// destination's own tick reports.
/// </summary>
/// <remarks>
/// <para><b>Live pose mirroring.</b> <see cref="DeliverSnapshot"/> keeps a double-buffered previous/current pose per
/// entity index — the same shape <c>WorldClient</c> keeps — so <c>WorldSessionSceneEmitter</c> can
/// interpolate avatars exactly like the boot avatar path does. It deliberately drops what avatar rendering does not
/// need: <see cref="EntitySnapshot.Kit"/> (render selection is index/look-keyed only; nothing branches on kit yet,
/// exactly as <c>WorldClient</c> itself notes) and the correction-error easer (<see cref="EntityContinuity.Kind"/>
/// still selects snap-vs-interpolate, it just never arms a decaying offset). It keeps
/// <see cref="EntitySnapshot.PlacementId"/> and a pose epoch the two declared discontinuities move, which the
/// session's stamp pool roots an inhabited body's creation on.</para>
/// <para><b>Borrowed-snapshot contract.</b> <see cref="DeliverSnapshot"/> is called synchronously on the
/// destination's own fixed-step thread, and <see cref="WorldSnapshot.Entries"/> wraps a reused server-owned array
/// (see <c>Server.WorldOutputHub</c>'s own remarks) that the destination's NEXT tick overwrites — every field this
/// method keeps is copied into this mirror's own arrays before the call returns, never retained by reference.</para>
/// <para><b>Publication.</b> An embedded destination delivers on its fixed-step thread; a federated observer delivers
/// on its socket task while the presentation thread reads this mirror. Definition and scalar clock/revision fields
/// therefore publish through <see cref="Volatile"/>/<see cref="Interlocked"/>. Each entity record is copied before
/// its active flag's release write; readers acquire that flag before consuming the copied pose fields. A reader that
/// needs the whole table from one delivery (<see cref="CaptureBodyImage"/>), or the inputs and arrival history
/// (<see cref="TryInput"/>, <see cref="SnapshotArrivalIntervals"/>), reads the latest complete image each writer
/// publishes when it finishes, through a <see cref="PublishBuffer{T}"/>, so it never waits on a delivery in
/// progress.</para>
/// </remarks>
public sealed class WorldSessionMirror : IClientSink {
    // The bounded entity table uses the same schema ceiling as the client. Appearance-catalog size is unrelated.
    private const int EntityCapacity = WorldBodiesLimits.CapacityCeiling;

    private WorldDeliveredDocument m_document;
    private int m_definitionRevision;

    // The rows state deliveries moved since the presentation last followed them: written by the delivering thread,
    // taken by FollowState, both under m_stampGate. m_followGate serializes followers over the one state mirror.
    private readonly Lock m_stampGate = new();
    private readonly Lock m_followGate = new();
    // The latest delivery's input publications, written under m_snapshotWriteGate and read through the published image.
    private readonly Dictionary<Principal, WorldInputPublication> m_inputs = [];
    // The latest delivery's input answers, kept and published beside the publications.
    private readonly Dictionary<Principal, WorldInputAnswer> m_answers = [];

    /// <summary>Reads the last input the authority answered for an owner from the latest complete image, never waiting on
    /// a delivery in progress.</summary>
    /// <param name="owner">The principal whose answer to read.</param>
    /// <param name="answer">The owner's answer, when the latest image carries one.</param>
    /// <returns><see langword="true"/> when the latest image carries an answer for <paramref name="owner"/>.</returns>
    public bool TryInputAnswer(Principal owner, out WorldInputAnswer answer) => TryOwned(owner: owner, records: static image => image.Answers, value: out answer);

    /// <summary>Reads an authority-issued input epoch and its immutable source decisions from the latest complete
    /// image, never waiting on a delivery in progress.</summary>
    /// <param name="owner">The principal whose input publication to read.</param>
    /// <param name="publication">The owner's publication, when the latest image carries one.</param>
    /// <returns><see langword="true"/> when the latest image carries a publication for <paramref name="owner"/>.</returns>
    public bool TryInput(Principal owner, out WorldInputPublication publication) => TryOwned(owner: owner, records: static image => image.Inputs, value: out publication);
    /// <summary>Reads the input publication of whichever owner drives a body from the latest complete image: what a
    /// client reads when the authority knows its seat by another principal than the client does (a forwarded authority
    /// stamps a traveler as its peer), since a body has one input owner and a delivery carries the recipient's own
    /// records.</summary>
    /// <param name="entity">The body's address at this authority.</param>
    /// <param name="publication">The publication, whose <see cref="WorldInputPublication.Owner"/> names the owner the
    /// authority answers this body's input under (<see cref="TryInputAnswer"/>).</param>
    /// <returns><see langword="true"/> when the latest image carries a publication for the body's index and
    /// generation.</returns>
    public bool TryInputOf(WorldEntityAddress entity, out WorldInputPublication publication) {
        var result = m_images.Read(state: entity, read: static (in SnapshotImage image, long version, WorldEntityAddress body) => {
            foreach (var input in image.Inputs.Values) {
                if ((input.Body == body.Index) && (input.Generation == body.Generation)) { return (Found: true, Value: input); }
            }

            return (Found: false, Value: default(WorldInputPublication));
        });

        publication = result.Value;
        return result.Found;
    }

    // Reads one owner's record from one of the latest complete image's owner-scoped tables.
    private bool TryOwned<T>(Principal owner, Func<SnapshotImage, Dictionary<Principal, T>> records, out T value) {
        var result = m_images.Read(state: (Owner: owner, Records: records), read: static (in SnapshotImage image, long version, (Principal Owner, Func<SnapshotImage, Dictionary<Principal, T>> Records) state) => {
            var found = state.Records(arg: image).TryGetValue(key: state.Owner, value: out var record);

            return (Found: found, Value: record);
        });

        value = result.Value!;
        return result.Found;
    }
    /// <summary>Reads one body from the latest complete image: both pose endpoints, the facts and the tick of one
    /// delivery, never a mixture of two and never waiting on a delivery in progress.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <param name="body">The body as the image holds it, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the image holds an active body at <paramref name="index"/>.</returns>
    public bool TryReadBody(int index, out WorldDeliveredBody body) {
        body = m_images.Read(state: index, read: static (in SnapshotImage image, long version, int index) => {
            if ((((uint)index) >= ((uint)image.Extent)) || !image.Entries[index].Active) {
                return default;
            }

            ref readonly var entry = ref image.Entries[index];

            return new WorldDeliveredBody(
                Active: true,
                Facts: entry.Facts,
                Generation: entry.Generation,
                Heading: entry.Heading,
                Kit: entry.Kit,
                Orientation: entry.CurrentOrientation,
                PoseEpoch: entry.PoseEpoch,
                Position: entry.CurrentPosition,
                PreviousPosition: entry.PreviousPosition,
                StepSeconds: image.StepSeconds,
                Tick: image.Tick
            );
        });

        return body.Active;
    }
    /// <summary>Gets what this mirror's deliveries have cost since it was constructed: each kind's count and the time
    /// its delivering thread spent applying it. The count of a delivery moves after its time, so a reader sees a
    /// count only with the time it cost.</summary>
    public WorldDeliveryWork DeliveryWork => new(
        DefinitionTicks: Interlocked.Read(location: ref m_definitionApplyTicks),
        Definitions: Interlocked.Read(location: ref m_definitionsApplied),
        SnapshotTicks: Interlocked.Read(location: ref m_snapshotApplyTicks),
        Snapshots: Interlocked.Read(location: ref m_snapshotsApplied),
        StateTicks: Interlocked.Read(location: ref m_stateApplyTicks),
        States: Interlocked.Read(location: ref m_statesApplied)
    );

    private long m_definitionApplyTicks;
    private long m_definitionsApplied;
    private long m_snapshotApplyTicks;
    private long m_snapshotsApplied;
    private long m_stateApplyTicks;
    private long m_statesApplied;

    // Charges one delivery to its kind: the time since `started`, then the count.
    private static void Charge(ref long ticks, ref long count, long started) {
        _ = Interlocked.Add(location1: ref ticks, value: (Stopwatch.GetTimestamp() - started));
        _ = Interlocked.Increment(location: ref count);
    }

    private readonly MarkedIndexSet m_pending = new();

    private bool m_pendingEverything;
    private ArenaTime? m_stateTime;

    private int[] m_followedRows = [];

    // The one state view the state mirror reads: the delivered definition's rows, plus the field cells each snapshot
    // carries, applied on delivery under m_followGate, which every read of the view (FollowState) also holds.
    private readonly WorldDocumentStateView m_stateView;
    private readonly IWorldStateView m_synchronizedStateView;

    private int m_followedLifetime;

    // The field and document rows one snapshot's cells moved (ApplyFieldCells's output), used only under m_followGate.
    private readonly int[] m_movedCells = new int[StateCapacity.MaxRows];
    // The body-state rows one snapshot's entries moved (TakeMovedBodyState's output), used only under m_followGate.
    private readonly int[] m_movedBodyState = new int[WorldDocumentStateView.MaxMovedBodyState];

    private WorldStateMirror? m_state;

    private int m_stateRevision = -1;

    private WorldStateMirror? m_observedState;
    private WorldDefinition? m_observedDefinition;

    private int m_observedRevision = -1;

    private bool m_observedSnapshotInstall;
    private ulong m_observedDefinitionEngineTick;

    private event Action<WorldDefinition, int, WorldStateMirror>? DeliveredStateObservers;

    /// <summary>Observes every completed snapshot after its field cells are applied, without coalescing deliveries.
    /// The optional observer mirror owns separate binding samples and reuses the ordinary mirror's resolver and
    /// field storage; the lazy presentation mirror is never advanced on the delivery thread.</summary>
    /// <param name="observer">Consumes the immutable definition, structural revision, and current delivered samples.
    /// The mirror is valid only during this callback: copy or reduce needed values before returning. Callbacks run
    /// synchronously under the delivery gate and must not wait for a presentation-thread operation.</param>
    /// <returns>A registration seeded from the current state; disposal synchronously ends future callbacks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
    public DeliveredStateObservation ObserveDeliveredState(Action<WorldDefinition, int, WorldStateMirror> observer) {
        ArgumentNullException.ThrowIfNull(observer);
        lock (m_snapshotWriteGate) {
            lock (m_followGate) {
                if (m_observedState is null) {
                    m_observedState = new WorldStateMirror(view: m_synchronizedStateView);
                    PublishObservedState(Tick, EngineTick);
                }
                var registration = new DeliveredStateObservation(mirror: m_observedState!, observer: observer, owner: this);

                DeliveredStateObservers += registration.Deliver;
                try {
                    registration.Deliver(definition: m_observedDefinition!, mirror: m_observedState!, revision: m_observedRevision);
                } catch {
                    registration.Dispose();
                    throw;
                }
                return registration;
            }
        }
    }

    private sealed class SynchronizedStateView(WorldSessionMirror owner) : IWorldStateView {
        public WorldPresentationManifest Manifest { get { lock (owner.m_followGate) { return owner.m_stateView.Manifest; } } }

        public bool TryResolveRow(string rowName, out int ordinal) {
            lock (owner.m_followGate) { return owner.m_stateView.TryResolveRow(ordinal: out ordinal, rowName: rowName); }
        }
        public int RowLength(int ordinal) {
            lock (owner.m_followGate) { return owner.m_stateView.RowLength(ordinal: ordinal); }
        }
        public bool TryRead(int ordinal, string? key, bool target, ulong tick, ulong engineTick, out WorldStateSample sample) {
            lock (owner.m_followGate) { return owner.m_stateView.TryRead(engineTick: engineTick, key: key, ordinal: ordinal, sample: out sample, target: target, tick: tick); }
        }
        public bool ReadRow(int ordinal, bool target, ulong tick, ulong engineTick, Span<double> elements, out WorldStateMotion motion) {
            lock (owner.m_followGate) { return owner.m_stateView.ReadRow(elements: elements, engineTick: engineTick, motion: out motion, ordinal: ordinal, target: target, tick: tick); }
        }
    }

    private void PublishObservedState(ulong tick, ulong engineTick, bool completedSnapshot = false) {
        var reinstall = (completedSnapshot && m_observedSnapshotInstall && (engineTick == m_observedDefinitionEngineTick));

        if (completedSnapshot) { m_observedSnapshotInstall = false; }
        if (m_observedState is not { } mirror) { return; }
        var definition = Definition;
        var revision = DefinitionRevision;

        if ((revision != m_observedRevision) || reinstall) {
            mirror.Install(completingDelivery: reinstall, engineTick: engineTick, tick: tick);
        } else {
            mirror.Refresh(stamp: new WorldStateStamp(EngineTick: engineTick, Everything: true, MovedRows: default, Tick: tick));
        }
        m_observedDefinition = definition;
        m_observedRevision = revision;
        DeliveredStateObservers?.Invoke(definition, revision, mirror);
    }

    /// <summary>A synchronously removable completed-delivery observer and its existing mirror work source.</summary>
    public sealed class DeliveredStateObservation : IDisposable {
        private readonly Action<WorldDefinition, int, WorldStateMirror> m_observer;

        private WorldSessionMirror? m_owner;

        internal DeliveredStateObservation(WorldSessionMirror owner, Action<WorldDefinition, int, WorldStateMirror> observer, WorldStateMirror mirror) {
            m_owner = owner;
            m_observer = observer;
            Work = mirror;
        }

        /// <summary>Gets the sampled mirror's counted work. Observers of one session share this same source.</summary>
        public Puck.Abstractions.Counting.IWorkCounterSource Work { get; }

        internal void Deliver(WorldDefinition definition, int revision, WorldStateMirror mirror) {
            if (m_owner is not null) { m_observer(definition, revision, mirror); }
        }

        /// <summary>Waits for any active callback and removes this observer before returning.</summary>
        public void Dispose() {
            if (m_owner is not { } owner) { return; }
            lock (owner.m_followGate) {
                if (m_owner is null) { return; }
                m_owner = null;
                owner.DeliveredStateObservers -= Deliver;
                if (owner.DeliveredStateObservers is null) {
                    owner.m_observedState = null;
                    owner.m_observedDefinition = null;
                    owner.m_observedRevision = -1;
                }
            }
        }
    }

    private WorldBodyContactMode[] m_kitBodyContacts;
    private FixedWorldCollider?[] m_kitColliders;
    // Attach replays the authority's most recently completed snapshot. A transfer committed after that snapshot
    // can therefore seed an entity at the SAME tick immediately before attach replays an image in which the entity
    // is still absent. Keep the causally newer commit image until an ordinary snapshot advances beyond its tick or
    // explicitly contains the committed generation.
    private WorldEntityAddress? m_seededRouteEntity;
    private ulong m_seededRouteTick;
    private int m_snapshotRevision;
    // The destination's own step duration in seconds, derived from the latest snapshot's StepTicks — the
    // presentation clock WorldSessionSceneEmitter normalizes real elapsed time against (see its own remarks on why
    // it cannot read a host alpha/delta for a session view).
    private int m_stepSecondsBits;
    private long m_stepTicksBits;
    private long m_tickBits;
    private long m_engineTickBits;

    private readonly WorldPoseTable m_poses = new(capacity: EntityCapacity);
    private readonly float[] m_heading = new float[EntityCapacity];
    private readonly BodyFacts[] m_facts = new BodyFacts[EntityCapacity];
    private readonly Vector3[] m_bodyColor = new Vector3[EntityCapacity];
    private readonly byte[] m_look = new byte[EntityCapacity];
    private readonly byte[] m_catalogRig = new byte[EntityCapacity];
    private readonly byte[] m_kit = new byte[EntityCapacity];
    private readonly int[] m_generation = new int[EntityCapacity];
    // Serializes the two writers of the entity arrays: a route seed written by the transfer/route thread and ordinary
    // snapshots arriving on the observer task. Per-field render reads stay lock-free and need no whole-tick record;
    // a whole-image reader reads the published image instead (m_images) and never this gate.
    private readonly Lock m_snapshotWriteGate = new();

    // The latest complete image of the entity table, its inputs and its arrival history. Invariant: a whole-image reader
    // never sees a torn image and never waits on a writer. Enforced by PublishBuffer, whose slots a writer fills only
    // inside its Publish (PublishImage) and a reader reads only inside a lease.
    private readonly PublishBuffer<SnapshotImage> m_images;

    // One past the highest entity slot either writer has written; every slot above it is still as constructed.
    private int m_extent;

    private string m_authority = string.Empty;
    // A real (wall-clock) timestamp — Stopwatch.GetTimestamp() — captured the instant the CURRENT snapshot's poses
    // were copied in. Presentation-only, exactly like WorldClient's own render-pose interpolation is presentation
    // floats never fed back into simulation state (rule 4's carve-out): the destination's tick thread and the render
    // thread calling WorldSessionSceneEmitter both live in this SAME process, so a real elapsed-time read between
    // them is an honest measure, not a cross-machine clock assumption.
    private long m_snapshotArrivalTimestamp = Stopwatch.GetTimestamp();

    // The last ArrivalHistory snapshot arrival timestamps, a ring under m_snapshotWriteGate that each image copies.
    private const int ArrivalHistory = 64;

    private readonly long[] m_arrivals = new long[ArrivalHistory];

    private int m_arrivalCount;

    private readonly bool m_unread;
    private readonly WorldStreamIngress m_ingress = new();

    /// <summary>Gets the stream this mirror is bound to and the span it holds: what its ingress accepts records
    /// from.</summary>
    public WorldStreamSpan Span => m_ingress.Span;
    /// <summary>Gets the mirror's current delivery span, ended whenever its ingress cuts or closes.</summary>
    public Puck.Abstractions.Presentation.DeliverySpan DeliverySpan => m_ingress.DeliverySpan;
    /// <summary>Gets the number of records the mirror's ingress dropped: a record of another stream, of an earlier
    /// generation, or not ahead of the last record it took.</summary>
    public long IngressRefusals => m_ingress.Refusals;

    /// <summary>Initializes the mirror with an empty placeholder definition — <c>Server.WorldServer.AttachSink</c>
    /// always delivers the live definition synchronously before returning its lease, so no caller ever observes this
    /// placeholder.</summary>
    /// <param name="placeholder">The definition to hold until the first delivery; a span that ends leaves the
    /// undisclosed document (<see cref="WorldProjection.Undisclosed"/>), never this one.</param>
    /// <param name="unread">Whether nothing will ever read the mirror's poses, field cells or inputs, as in a host
    /// that presents nothing: every snapshot is then dropped on arrival, and only definitions and state stamps are
    /// kept.</param>
    public WorldSessionMirror(WorldDefinition placeholder, bool unread = false) {
        ArgumentNullException.ThrowIfNull(argument: placeholder);

        m_unread = unread;
        m_document = new WorldDeliveredDocument(
            Definition: placeholder,
            Lifetime: 0,
            Version: default
        );
        m_stateView = new WorldDocumentStateView(definition: () => Definition);
        m_synchronizedStateView = new SynchronizedStateView(owner: this);
        m_kitColliders = CompileColliders(definition: placeholder);
        m_kitBodyContacts = CompileBodyContacts(definition: placeholder);
        // Every slot starts as the constructed table's image, so the first copy is already complete.
        var constructed = m_snapshotArrivalTimestamp;

        m_images = new PublishBuffer<SnapshotImage>(create: () => new SnapshotImage(arrivalTimestamp: constructed));
    }

    /// <summary>Raised on the delivering thread after each delivered definition or state is published as
    /// <see cref="Document"/>, with the document and version it published.</summary>
    public event Action<WorldDeliveredDocument>? DocumentDelivered;

    /// <summary>The authority named by the latest delivered snapshot.</summary>
    public string Authority => Volatile.Read(location: ref m_authority);
    /// <summary>The destination's live world definition — the boot/attach definition until a later mutation batch or
    /// swap delivers a new one.</summary>
    public WorldDefinition Definition => Document.Definition;
    /// <summary>The destination's live world definition together with its version, published as one pair: a
    /// placeholder carries <see langword="default"/> until the first delivery.</summary>
    public WorldDeliveredDocument Document => Volatile.Read(location: ref m_document);
    /// <summary>The monotonic definition-delivery counter — bumped each time the destination delivers a new
    /// definition, the rebuild-watch component <c>WorldSessionSceneEmitter</c> reads.</summary>
    public int DefinitionRevision => Volatile.Read(location: ref m_definitionRevision);
    /// <summary>The honest presentation fraction through the currently delivered snapshot interval. Remote and
    /// colocated mirrors derive it from the snapshot's own step width and arrival time, never from whichever world
    /// happens to be drawing them.</summary>
    public float InterpolationAlpha => ResolveInterpolationAlpha(
        stepSeconds: StepSeconds,
        arrivalTimestamp: SnapshotArrivalTimestamp
    );
    /// <summary>The wall-clock timestamp (<see cref="Stopwatch.GetTimestamp"/> units) the current snapshot's poses
    /// were copied in at — the "render call's arrival" baseline <c>WorldSessionSceneEmitter</c> measures
    /// elapsed real time against, since a session view supplies neither a host delta nor a host alpha (see that
    /// type's own remarks).</summary>
    public long SnapshotArrivalTimestamp => Interlocked.Read(location: ref m_snapshotArrivalTimestamp);
    /// <summary>The declared-set/palette revision from the latest snapshot — a separate rebuild-watch component from
    /// <see cref="DefinitionRevision"/> (never summed with it: this one is assigned from the wire and can move down,
    /// exactly like <c>WorldClient.WriteRevision</c>'s own server-revision component, for the identical
    /// reason).</summary>
    public int SnapshotRevision => Volatile.Read(location: ref m_snapshotRevision);
    /// <summary><see cref="StepTicks"/> converted to seconds — the denominator
    /// <c>WorldSessionSceneEmitter</c>'s self-derived render alpha divides real elapsed time by.</summary>
    public float StepSeconds => BitConverter.Int32BitsToSingle(value: Volatile.Read(location: ref m_stepSecondsBits));
    /// <summary>The destination's own step width (engine ticks per its authored simulation step) at the latest
    /// delivered snapshot — the destination presentation clock docs/architecture/worlds.md's "Observation and display"
    /// names.</summary>
    public ulong StepTicks => unchecked((ulong)Interlocked.Read(location: ref m_stepTicksBits));
    /// <summary>The destination's latest completed simulation tick — read-back (<c>world.faces</c>'s session echo)
    /// AND the presentation clock <c>WorldSessionSceneEmitter</c> resolves its render alpha against (via
    /// <see cref="StepSeconds"/>/<see cref="SnapshotArrivalTimestamp"/>).</summary>
    public ulong Tick => unchecked((ulong)Interlocked.Read(location: ref m_tickBits));
    /// <summary>The destination's latest delivered engine-tick coordinate — what a <c>StateAdvance</c> row's live
    /// value is computed at; never derived from <see cref="Tick"/> at a simulation rate.</summary>
    public ulong EngineTick => unchecked((ulong)Interlocked.Read(location: ref m_engineTickBits));

    private static WorldBodyContactMode[] CompileBodyContacts(WorldDefinition definition) =>
        definition.Kits.Select(selector: static kit => kit.BodyContact).ToArray();
    private static FixedWorldCollider?[] CompileColliders(WorldDefinition definition) {
        var colliders = new FixedWorldCollider?[definition.Kits.Count];

        for (var index = 0; (index < colliders.Length); index++) {
            colliders[index] = FixedWorldCollider.Compile(
                collider: definition.Kits[index].Collider,
                creations: definition.Creations
            );
        }
        return colliders;
    }
    private static bool SnapshotContains(in WorldSnapshot snapshot, in WorldEntityAddress entity) {
        if (!string.Equals(
            a: snapshot.Authority,
            b: entity.Authority,
            comparisonType: StringComparison.Ordinal
        )) {
            return false;
        }

        return snapshot.TryFindEntity(entity.Index, entity.Generation, out _);
    }

    /// <summary>The durable address of the entity currently occupying a slot.</summary>
    public WorldEntityAddress Address(int index) => new(
        Authority: Authority,
        Index: index,
        Generation: Volatile.Read(location: ref m_generation[index])
    );
    /// <summary>The entity's render body color, mirrored verbatim from the snapshot (a pending seat's is already
    /// gray-lerped server-side).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 BodyColor(int index) => m_bodyColor[index];
    public WorldBodyContactMode BodyContact(int index) {
        var contacts = Volatile.Read(location: ref m_kitBodyContacts);
        var kit = m_kit[index];

        return ((kit < contacts.Length)
            ? contacts[kit]
            : WorldBodyContactMode.Overlap
        );
    }
    /// <summary>The entity-owned procedural rig mirrored from the authoritative snapshot.</summary>
    public byte CatalogRig(int index) => m_catalogRig[index];
    public FixedWorldCollider? Collider(int index) {
        var colliders = Volatile.Read(location: ref m_kitColliders);
        var kit = m_kit[index];

        return ((kit < colliders.Length)
            ? colliders[kit]
            : null
        );
    }
    /// <summary>Copies the latest complete body image out of the mirror's reusable publication storage.</summary>
    public BodyImage CaptureBodyImage(BodyImage? previous = null) => m_images.Read(
        state: (Mirror: this, Previous: previous),
        read: static (in SnapshotImage image, long version, (WorldSessionMirror Mirror, BodyImage? Previous) state) => {
            var mirror = state.Mirror;
            var definition = mirror.Definition;
            var revision = mirror.DefinitionRevision;
            if (state.Previous is { } retained && retained.Version == version &&
                retained.DefinitionRevision == revision && ReferenceEquals(retained.Definition, definition)) {
                return retained;
            }

            var bodies = ImmutableArray.CreateBuilder<Body>(image.Extent);
            var kitColliders = Volatile.Read(ref mirror.m_kitColliders);
            var kitContacts = Volatile.Read(ref mirror.m_kitBodyContacts);
            for (var index = 0; index < image.Extent; index++) {
                var entry = image.Entries[index];
                bodies.Add(new Body(
                    Active: entry.Active,
                    Address: new WorldEntityAddress(Authority: image.Authority, Generation: entry.Generation, Index: index),
                    PoseEpoch: entry.PoseEpoch,
                    PlacementId: entry.PlacementId,
                    PreviousPosition: entry.PreviousPosition,
                    PreviousOrientation: entry.PreviousOrientation,
                    CurrentPosition: entry.CurrentPosition,
                    CurrentOrientation: entry.CurrentOrientation,
                    Color: entry.Color,
                    Look: WorldDefinitionRows.ResolveLook(index: entry.Look, rows: definition.Looks),
                    CatalogRig: entry.CatalogRig,
                    Collider: (entry.Kit < kitColliders.Length) ? kitColliders[entry.Kit] : null,
                    Contact: (entry.Kit < kitContacts.Length) ? kitContacts[entry.Kit] : WorldBodyContactMode.Overlap
                ));
            }

            return new BodyImage(definition, revision, image.Authority, image.Tick, image.Revision, image.StepSeconds,
                image.ArrivalTimestamp, bodies.MoveToImmutable(), version);
        });

    /// <summary>A retained delivery for an adjacency. Entries are indexed by the destination's entity slot.</summary>
    public sealed record BodyImage(WorldDefinition Definition, int DefinitionRevision, string Authority, ulong Tick,
        int Revision, float StepSeconds, long ArrivalTimestamp, ImmutableArray<Body> Entries, long Version);

    /// <summary>One slot from a retained body image, with its look and collider resolved against the delivered document.</summary>
    public readonly record struct Body(bool Active, WorldEntityAddress Address, int PoseEpoch, string? PlacementId,
        Vector3 PreviousPosition, Quaternion PreviousOrientation, Vector3 CurrentPosition, Quaternion CurrentOrientation,
        Vector3 Color, WorldLook Look, byte CatalogRig, FixedWorldCollider? Collider, WorldBodyContactMode Contact);

    // Publishes the entity table, inputs and arrival history as one complete image (see m_images). Called by a writer
    // holding m_snapshotWriteGate once its writes are done.
    private void PublishImage() => m_images.Publish(state: this, fill: static (ref SnapshotImage image, WorldSessionMirror mirror) => mirror.FillImage(image: image));
    // Overwrites an older image whole; copies only the slots below m_extent, since every other slot is still as
    // constructed.
    private void FillImage(SnapshotImage image) {
        var extent = m_extent;

        if (image.Entries.Length < extent) {
            image.Entries = new ImageEntry[((int)(((uint)extent)).NextPowerOfTwo())];
        }
        for (var index = 0; (index < extent); index++) {
            image.Entries[index] = new ImageEntry(
                Active: m_poses.IsActive(index: index),
                CatalogRig: m_catalogRig[index],
                Color: m_bodyColor[index],
                CurrentOrientation: m_poses.CurrentOrientation(index: index),
                CurrentPosition: m_poses.CurrentPosition(index: index),
                Facts: m_facts[index],
                Generation: m_generation[index],
                Heading: m_heading[index],
                Kit: m_kit[index],
                Look: m_look[index],
                PlacementId: m_poses.PlacementId(index: index),
                PoseEpoch: m_poses.PoseEpoch(index: index),
                PreviousOrientation: m_poses.PreviousOrientation(index: index),
                PreviousPosition: m_poses.PreviousPosition(index: index)
            );
        }
        image.Extent = extent;
        image.Authority = Authority;
        image.Tick = Tick;
        image.Revision = SnapshotRevision;
        image.StepSeconds = StepSeconds;
        image.ArrivalTimestamp = SnapshotArrivalTimestamp;
        image.Inputs.Clear();
        foreach (var (owner, input) in m_inputs) { image.Inputs[owner] = input; }
        image.Answers.Clear();
        foreach (var (owner, answer) in m_answers) { image.Answers[owner] = answer; }
        m_arrivals.CopyTo(array: image.Arrivals, index: 0);
        image.ArrivalCount = m_arrivalCount;
    }
    private void Extend(int index) {
        if (index >= m_extent) { m_extent = (index + 1); }
    }

    // One slot of a published image: what CaptureBodyImage hands out, before resolving the kit and look against the
    // current definition.
    private readonly record struct ImageEntry(
        bool Active,
        int Generation,
        int PoseEpoch,
        string? PlacementId,
        Vector3 PreviousPosition,
        Quaternion PreviousOrientation,
        Vector3 CurrentPosition,
        Quaternion CurrentOrientation,
        Vector3 Color,
        byte Look,
        byte CatalogRig,
        byte Kit,
        float Heading,
        BodyFacts Facts
    );
    private sealed class SnapshotImage(long arrivalTimestamp) {
        public ImageEntry[] Entries = [];

        public int Extent;

        public string Authority = string.Empty;

        public ulong Tick;
        public int Revision;
        public float StepSeconds;

        public long ArrivalTimestamp = arrivalTimestamp;
        public readonly Dictionary<Principal, WorldInputPublication> Inputs = [];
        public readonly Dictionary<Principal, WorldInputAnswer> Answers = [];
        public readonly long[] Arrivals = new long[ArrivalHistory];

        public int ArrivalCount;
    }

    /// <summary>The entity's latest-tick render attitude (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion CurrentOrientation(int index) => m_poses.CurrentOrientation(index: index);
    /// <summary>The entity's latest-tick render position (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 CurrentPosition(int index) => m_poses.CurrentPosition(index: index);

    // Publishes a delivered definition and its version as one pair; the same pair delivered again is kept, so a
    // repeated delivery allocates nothing.
    private void Publish(WorldDefinition definition, WorldDocumentVersion version) {
        lock (m_followGate) {
            var current = Volatile.Read(location: ref m_document);

            if (ReferenceEquals(objA: current.Definition, objB: definition) && (current.Version == version)) {
                return;
            }

            Volatile.Write(
                location: ref m_document,
                value: new WorldDeliveredDocument(
                    Definition: definition,
                    Lifetime: (current.Lifetime + ((current.Version.Activation != version.Activation) ? 1 : 0)),
                    Version: version
                )
            );
        }
    }

    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) {
        // The mirror drives no console — nothing here ever queries the destination.
    }
    /// <inheritdoc/>
    public void DeliverComposition(WorldComposition composition) {
        // No window composer observes a destination through this seam.
    }
    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        if (!m_ingress.Admit(stream: in stream)) { return; }
        if (version.Activation != Document.Version.Activation) {
            CueFrames.Clear();
            AudioCueFrames.Clear();
        }
        ApplyDefinition(definition: definition, version: version);
    }
    /// <inheritdoc/>
    public void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (!m_ingress.Admit(stream: in stream)) { return; }
        var started = Stopwatch.GetTimestamp();
        lock (m_snapshotWriteGate) {
            lock (m_followGate) {
                if (!rows.TryApply(Document.Definition, out var changed)) { throw new InvalidOperationException("A presentation delivery must match the mirror's creation roster."); }
                Publish(changed, version);
                _ = Interlocked.Increment(ref m_definitionRevision);
            }
        }
        DocumentDelivered?.Invoke(Document);
        Charge(count: ref m_definitionsApplied, started: started, ticks: ref m_definitionApplyTicks);
    }
    private void ApplyDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        var started = Stopwatch.GetTimestamp();

        lock (m_stampGate) {
            m_stateTime = null;
        }

        Volatile.Write(
            location: ref m_kitColliders,
            value: CompileColliders(definition: definition)
        );
        Volatile.Write(
            location: ref m_kitBodyContacts,
            value: CompileBodyContacts(definition: definition)
        );
        lock (m_snapshotWriteGate) {
            lock (m_followGate) {
                Publish(
                    definition: definition,
                    version: version
                );
                _ = Interlocked.Increment(location: ref m_definitionRevision);
                m_observedSnapshotInstall = true;
                m_observedDefinitionEngineTick = EngineTick;
                PublishObservedState(Tick, EngineTick);
            }
        }
        DocumentDelivered?.Invoke(obj: Document);
        Charge(count: ref m_definitionsApplied, started: started, ticks: ref m_definitionApplyTicks);
    }
    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        // No presentation service of the OBSERVING world is reachable from inside a destination's own delivery.
    }
    /// <inheritdoc/>
    /// <remarks>Every cut of the bound stream ends the span the mirror was showing: the mirror drops what it held,
    /// holding the undisclosed document, no body, no route seed and no queued cue, so nothing of the ended span stays
    /// showable, and the whole document and primer the stream owes the new generation fill it again from empty (its
    /// first snapshot seeds both pose endpoints of every body). A terminal record also closes the span, after which only
    /// a stream that never closed on this mirror binds it. A record of any other stream changes nothing.</remarks>
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
        if (m_ingress.Cut(stream: in stream, terminal: terminal)) { Withdraw(); }
    }
    /// <inheritdoc/>
    /// <remarks>The mirror drops what it held exactly as for a terminal record, and takes records again only from a
    /// stream that never closed on it.</remarks>
    public void CloseSpan(ulong stream) {
        if (m_ingress.Close(stream: stream)) { Withdraw(); }
    }
    // Drops everything an ended span delivered: the undisclosed document in place of the delivered one, an empty table
    // in place of its bodies, and no route seed or queued cue, applied through the ordinary delivery paths so every
    // reader follows it. The undisclosed document, never the construction placeholder, which can be a delivered
    // document (a route's, or the authority's last).
    private void Withdraw() {
        lock (m_snapshotWriteGate) { m_seededRouteEntity = null; }
        CueFrames.Clear();
        AudioCueFrames.Clear();
        ApplyDefinition(definition: WorldProjection.Undisclosed, version: default);
        if (m_unread) { return; }
        ApplySnapshot(snapshot: new WorldSnapshot(
            Authority: Authority,
            EngineTick: EngineTick,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldsFull: true,
            Revision: SnapshotRevision,
            StepTicks: StepTicks,
            Tick: Tick
        ));
    }

    /// <inheritdoc/>
    /// <remarks>Copies every kept field out of the borrowed snapshot before returning (see this
    /// type's own borrowed-snapshot remarks). The pose endpoints follow <see cref="WorldPoseTable.TryAdmit"/> exactly as
    /// <c>WorldClient</c>'s do, except that a <see cref="EntityContinuityKind.Correction"/>, which the boot client
    /// eases with a decaying render-error offset this mirror does not reproduce, is ordinary double-buffering.</remarks>
    private readonly WorldSolidScaleMirror m_solidScales = new();
    /// <summary>The session's delivered one-shot frames and score conditions.</summary>
    public WorldPresentationCueFeed CueFrames { get; } = new();
    /// <summary>The listening seat's copy of the session's presentation events.</summary>
    public WorldPresentationCueFeed AudioCueFrames { get; } = new();

    /// <summary>The authority-sampled root size of a destination solid.</summary>
    public float? SolidScale(string placementId) { lock (m_snapshotWriteGate) { return m_solidScales.Read(placementId, InterpolationAlpha); } }
    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        if (!m_ingress.Admit(stream: in stream) || m_unread) { return; }

        var started = Stopwatch.GetTimestamp();

        ApplySnapshot(snapshot: in snapshot);
        var cues = WorldPresentationCueFeed.Capture(snapshot);
        if (cues.Cues.Length != 0) { CueFrames.Publish(cues); }
        AudioCueFrames.Publish(cues);
        Charge(count: ref m_snapshotsApplied, started: started, ticks: ref m_snapshotApplyTicks);
    }
    private void ApplySnapshot(in WorldSnapshot snapshot) {
        lock (m_snapshotWriteGate) {
            m_solidScales.Deliver(sizes: snapshot.SolidScales.Span);
            m_inputs.Clear();
            foreach (ref readonly var input in snapshot.Inputs.Span) { m_inputs[input.Owner] = input; }
            m_answers.Clear();
            foreach (ref readonly var answer in snapshot.InputAnswers.Span) { m_answers[answer.Owner] = answer; }
            var seeded = m_seededRouteEntity;
            var containsSeed = ((seeded is { } address) && SnapshotContains(
                entity: in address,
                snapshot: in snapshot
            ));
            var preserveSeed = ((seeded is not null) && !containsSeed && (snapshot.Tick <= m_seededRouteTick));

            if (
                containsSeed ||
                ((seeded is not null) && !preserveSeed)
            ) {
                m_seededRouteEntity = null;
            }

            m_poses.Begin();

            foreach (ref readonly var entry in snapshot.Entries.Span) {
                var index = entry.Index;

                if (
                    (((uint)index) >= EntityCapacity) ||
                    (preserveSeed && (seeded is { } preserved) && (index == preserved.Index) &&
                        (!string.Equals(
                            a: snapshot.Authority,
                            b: preserved.Authority,
                            comparisonType: StringComparison.Ordinal
                        ) || (entry.Generation != preserved.Generation)))
                ) {
                    continue;
                }

                Extend(index: index);
                m_bodyColor[index] = entry.BodyColor;
                m_kit[index] = entry.Kit;
                m_look[index] = entry.Look;
                m_catalogRig[index] = entry.CatalogRig;
                Volatile.Write(
                    location: ref m_generation[index],
                    value: entry.Generation
                );
                m_heading[index] = entry.Heading;
                m_facts[index] = entry.Facts;
                // The pose table publishes the entry's active flag last, after every field above, so a reader on
                // another thread (a federated observer delivers on its socket task) that acquires IsActive sees the
                // completed record.
                _ = m_poses.TryAdmit(
                    admission: out _,
                    easeCorrections: false,
                    entry: in entry
                );
            }

            m_poses.Complete(preserved: ((preserveSeed && (seeded is { } kept))
                ? kept.Index
                : -1
            ));

            _ = Interlocked.Exchange(
                location1: ref m_tickBits,
                value: unchecked((long)Math.Max(
                    val1: snapshot.Tick,
                    val2: (preserveSeed
                ? m_seededRouteTick
                : 0UL)
                ))
            );
            _ = Interlocked.Exchange(
                location1: ref m_engineTickBits,
                value: unchecked((long)snapshot.EngineTick)
            );
            if (!preserveSeed) {
                Volatile.Write(
                    location: ref m_authority,
                    value: snapshot.Authority
                );
            }
            _ = Interlocked.Exchange(
                location1: ref m_stepTicksBits,
                value: unchecked((long)snapshot.StepTicks)
            );
            Volatile.Write(
                location: ref m_snapshotRevision,
                value: snapshot.Revision
            );
            Volatile.Write(
                location: ref m_stepSecondsBits,
                value: BitConverter.SingleToInt32Bits(value: ((float)FixedTickConversion.ToSeconds(ticks: snapshot.StepTicks)))
            );
            var arrival = Stopwatch.GetTimestamp();

            _ = Interlocked.Exchange(
                location1: ref m_snapshotArrivalTimestamp,
                value: arrival
            );
            m_arrivals[(m_arrivalCount % ArrivalHistory)] = arrival;
            m_arrivalCount++;
            // The entity table is complete: publish it before the field and observer tail, which no copy waits on.
            PublishImage();

            lock (m_stampGate) {
                m_stateTime = ArenaTime.At(engineTick: snapshot.EngineTick, tick: snapshot.Tick);
            }
            // Fields land before completed-delivery observers reduce the tick. The presentation mirror stays lazy.
            lock (m_followGate) {
                if (!snapshot.FieldCells.IsEmpty) {
                    var moved = m_stateView.ApplyFieldCells(
                        deltas: snapshot.FieldCells.Span,
                        moved: m_movedCells
                    );

                    lock (m_stampGate) {
                        for (var index = 0; (index < moved); index++) {
                            NoteRow(ordinal: m_movedCells[index]);
                        }
                    }
                }
                // A body's action state rides its entry, applied for every entry this delivery admitted.
                foreach (ref readonly var entry in snapshot.Entries.Span) {
                    if (
                        (((uint)entry.Index) < ((uint)EntityCapacity)) &&
                        m_poses.Admitted(index: entry.Index)
                    ) {
                        m_stateView.ApplyBodyState(
                            body: entry.Index,
                            values: entry.State.Span
                        );
                    }
                }

                var movedBodyState = m_stateView.TakeMovedBodyState(moved: m_movedBodyState);

                if (movedBodyState > 0) {
                    lock (m_stampGate) {
                        for (var index = 0; (index < movedBodyState); index++) {
                            NoteRow(ordinal: m_movedBodyState[index]);
                        }
                    }
                }
                PublishObservedState(snapshot.Tick, snapshot.EngineTick, completedSnapshot: true);
            }
        }
    }
    /// <inheritdoc/>
    /// <remarks>The stamp's moved rows are kept until <see cref="FollowState"/> takes them, so the presentation reads
    /// only the bound slots of rows that moved.</remarks>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!m_ingress.Admit(stream: in stream)) { return; }

        var started = Stopwatch.GetTimestamp();

        // A value-only mutation cannot have changed a kit's collider or body-contact mode: publish the fresh
        // definition for state-value reads without recompiling either table or bumping the rebuild-watch revision.
        // Publish the definition and its stamped clock together with respect to followers, including event readers.
        lock (m_followGate) {
            // A delivery that moved placement rows is a scene change to this mirror's readers, which watch the definition
            // revision alone.
            if (WorldPlacementResponse.ComparePlacements(after: definition, before: Volatile.Read(location: ref m_document).Definition, solid: out _) != WorldPlacementChange.None) {
                _ = Interlocked.Increment(location: ref m_definitionRevision);
            }

            Publish(
                definition: definition,
                version: version
            );

            lock (m_stampGate) {
                // Anchor and observation deltas arrive at unsampled ticks too. Their values stand at the delivery's
                // clock, even while the latest body snapshot still names an older tick.
                m_stateTime = ArenaTime.At(engineTick: stamp.EngineTick, tick: stamp.Tick);

                if (stamp.Everything) {
                    m_pendingEverything = true;
                } else {
                    foreach (var ordinal in stamp.MovedRows.Span) {
                        NoteRow(ordinal: ordinal);
                    }
                }
            }
        }

        if (!stamp.ArenaValues) { DocumentDelivered?.Invoke(obj: Document); }
        Charge(count: ref m_statesApplied, started: started, ticks: ref m_stateApplyTicks);
    }

    // Notes one moved row for the next FollowState to refresh, once however often it moves before then. Called under
    // m_stampGate; a negative ordinal names no row.
    private void NoteRow(int ordinal) {
        if (ordinal < 0) {
            return;
        }

        _ = m_pending.Add(index: ordinal);
    }

    /// <summary>Brings this mirror's state mirror up to the latest delivery and returns it: a definition delivery
    /// installs it, the rows state deliveries moved since the last follow refresh only the slots bound to them, and a
    /// newer tick with nothing moved refreshes only the slots still moving. Called on the thread that presents
    /// frames, as often as a presentation reads; a follow with nothing new reads nothing.</summary>
    /// <returns>The state mirror every presentation read of this destination's rows goes through.</returns>
    public WorldStateMirror FollowState() {
        lock (m_followGate) {
            var state = (m_state ??= new WorldStateMirror(view: m_synchronizedStateView));
            int count;
            bool everything;
            ArenaTime? time;

            lock (m_stampGate) {
                count = m_pending.Count;

                if (m_followedRows.Length < count) {
                    Array.Resize(
                        array: ref m_followedRows,
                        newSize: Math.Max(
                            val1: count,
                            val2: (m_followedRows.Length * 2)
                        )
                    );
                }

                m_pending.Members.CopyTo(destination: m_followedRows);
                m_pending.Clear();
                everything = m_pendingEverything;
                time = m_stateTime;
                m_pendingEverything = false;
            }

            var lifetime = Volatile.Read(location: ref m_document).Lifetime;

            for (; (m_followedLifetime < lifetime); m_followedLifetime++) {
                state.BeginLifetime();
            }

            var revision = DefinitionRevision;
            var tick = (time?.Tick ?? Tick);
            var engineTick = (time?.EngineTick ?? EngineTick);

            if (revision != m_stateRevision) {
                m_stateRevision = revision;
                state.Install(
                    engineTick: engineTick,
                    tick: tick
                );
            } else if (
                everything ||
                (count > 0)
            ) {
                state.Refresh(stamp: new WorldStateStamp(
                    EngineTick: engineTick,
                    Everything: everything,
                    MovedRows: m_followedRows.AsMemory(
                        length: count,
                        start: 0
                    ),
                    Tick: tick
                ));
            } else {
                state.Advance(
                    engineTick: engineTick,
                    tick: tick
                );
            }

            return state;
        }
    }
    /// <summary>Gets an entity's latest authoritative fact mask (<see cref="EntitySnapshot.Facts"/>) — not
    /// interpolated; the facts are discrete authority answers.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public BodyFacts Facts(int index) => m_facts[index];
    /// <summary>Gets an entity's latest authoritative heading in radians about world up (<see cref="EntitySnapshot.Heading"/>)
    /// — not interpolated; the composition seam that reads it wants the authority's current value.</summary>
    public float Heading(int index) => m_heading[index];
    /// <summary>Whether the entity at <paramref name="index"/> was active (drawn) in the latest snapshot.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_poses.IsActive(index: index);
    /// <summary>Gets the placement an entity inhabits (<see cref="EntitySnapshot.PlacementId"/>).</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The placement id, or <see langword="null"/> when the entity inhabits none.</returns>
    public string? PlacementId(int index) => m_poses.PlacementId(index: index);
    /// <summary>Gets an entity's pose epoch: it moves each time the entity becomes active or teleports, the two
    /// discontinuities a snapshot declares, as <c>WorldClient.PoseEpoch</c> does for the local world.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The epoch.</returns>
    public int PoseEpoch(int index) => m_poses.PoseEpoch(index: index);
    /// <summary>Resolves the lowest active entity index that inhabits a placement.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The entity index, or <c>-1</c> when none inhabits it.</param>
    /// <returns><see langword="true"/> when an active entity inhabits the placement.</returns>
    public bool TryInhabitantBody(string placementId, out int index) => m_poses.TryInhabitant(
        index: out index,
        placementId: placementId
    );

    /// <summary>Gets the in-place portal crossings each entity has made, which a consumer that latched an entity's
    /// world-space pose carries it through, as <c>WorldClient.Carries</c> does for the local world.</summary>
    public WorldPoseCarries Carries => m_poses.Carries;

    /// <summary>The look row an entity wears: the delivered look table indexed by the entity's mirrored look index,
    /// or the implicit single catalog look when the world authors no <c>looks</c> section, and for an index the
    /// delivered table cannot cover. The same resolve <c>WorldClient.Look</c> performs, over this mirror's own
    /// table instead.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The entity's look row.</returns>
    public WorldLook Look(int index) => WorldDefinitionRows.ResolveLook(
        rows: Definition.Looks,
        index: m_look[index]
    );
    /// <summary>The entity's previous-tick render attitude (one interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion PreviousOrientation(int index) => m_poses.PreviousOrientation(index: index);
    /// <summary>The entity's previous-tick render position (one interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 PreviousPosition(int index) => m_poses.PreviousPosition(index: index);
    /// <summary>Copies the wall-clock intervals between the most recent snapshot arrivals, oldest first: the evenness of
    /// the clock <see cref="InterpolationAlpha"/> presents by, from the latest complete image, never waiting on a delivery
    /// in progress.</summary>
    /// <param name="milliseconds">The destination, in milliseconds; at most its length are copied.</param>
    /// <returns>The number of intervals copied.</returns>
    public int SnapshotArrivalIntervals(Span<double> milliseconds) {
        return m_images.Read(state: milliseconds, read: static (in SnapshotImage image, long version, scoped Span<double> milliseconds) => {
            var held = Math.Min(val1: image.ArrivalCount, val2: ArrivalHistory);
            var count = Math.Min(val1: Math.Max(val1: (held - 1), val2: 0), val2: milliseconds.Length);
            var first = ((image.ArrivalCount - count) - 1);

            for (var index = 0; (index < count); index++) {
                var from = image.Arrivals[((first + index) % ArrivalHistory)];
                var to = image.Arrivals[(((first + index) + 1) % ArrivalHistory)];

                milliseconds[index] = Stopwatch.GetElapsedTime(endingTimestamp: to, startingTimestamp: from).TotalMilliseconds;
            }

            return count;
        });
    }
    /// <summary>Computes the honest presentation fraction through a delivered snapshot interval from its own step
    /// width and arrival time.</summary>
    public static float ResolveInterpolationAlpha(float stepSeconds, long arrivalTimestamp) {
        if (stepSeconds <= 0f) {
            return 1f;
        }

        var elapsedSeconds = ((float)Stopwatch.GetElapsedTime(startingTimestamp: arrivalTimestamp).TotalSeconds);

        return Math.Clamp(
            max: 1f,
            min: 0f,
            value: (elapsedSeconds / stepSeconds)
        );
    }
    /// <summary>Publishes the exact committed head of a traveler route before its observation socket can deliver the
    /// destination's first ordinary snapshot. This is not prediction: the route answer was read under the final
    /// authority's operation gate after commit. The subsequent snapshot replaces the seed normally.</summary>
    /// <remarks>A seed is not a record of a stream: it comes from a committed transfer read under its authority's gate,
    /// or from a route record that its observation session's own ingress admitted. It holds only within the span it was
    /// seeded into, since every cut and close drops it with the rest of what the mirror held.</remarks>
    public void SeedRoute(in WorldAuthorityRouteDescription route) {
        var index = route.Entity.Index;

        if (((uint)index) >= EntityCapacity) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(route),
                message: $"route entity {index} exceeds mirror capacity {EntityCapacity}"
            );
        }

        lock (m_snapshotWriteGate) {
            ApplyDefinition(
                definition: route.Definition,
                version: route.Version
            );
            var position = route.Position.ToVector3();
            var orientation = route.Orientation.ToQuaternion();

            // A route carries one attitude; its yaw is the heading.
            m_heading[index] = VectorFunctions.YawOf(attitude: orientation);
            m_bodyColor[index] = route.BodyColor;
            m_kit[index] = route.Kit;
            m_look[index] = route.Look;
            m_catalogRig[index] = route.CatalogRig;
            Volatile.Write(
                location: ref m_generation[index],
                value: route.Entity.Generation
            );
            Volatile.Write(
                location: ref m_authority,
                value: route.Entity.Authority
            );
            _ = Interlocked.Exchange(
                location1: ref m_tickBits,
                value: unchecked((long)route.Tick)
            );
            _ = Interlocked.Exchange(
                location1: ref m_snapshotArrivalTimestamp,
                value: Stopwatch.GetTimestamp()
            );
            m_poses.Seed(
                index: index,
                orientation: orientation,
                position: position
            );
            m_seededRouteEntity = route.Entity;
            m_seededRouteTick = route.Tick;
            Extend(index: index);
            PublishImage();
        }
    }
}
