using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
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
    // The entity extras the pose table does not keep, at one length. Grown with the pose table by Extend before a writer
    // touches a slot, and replaced whole, so a lock-free reader reads one complete generation and bounds its index
    // against it (At): a slot past it reads as never delivered.
    private sealed class Extras(int length) {
        public readonly Vector3[] BodyColor = new Vector3[length];
        public readonly byte[] CatalogRig = new byte[length];
        public readonly BodyFacts[] Facts = new BodyFacts[length];
        public readonly int[] Generation = new int[length];
        public readonly float[] Heading = new float[length];
        public readonly byte[] Kit = new byte[length];
        public readonly byte[] Look = new byte[length];
        // One more than the index of the body ridden, so a slot never delivered reads as riding nothing.
        public readonly int[] Rides = new int[length];

        public int Length => Heading.Length;

        public Extras Grown(int length) {
            var grown = new Extras(length: length);

            BodyColor.CopyTo(array: grown.BodyColor, index: 0);
            CatalogRig.CopyTo(array: grown.CatalogRig, index: 0);
            Facts.CopyTo(array: grown.Facts, index: 0);
            Generation.CopyTo(array: grown.Generation, index: 0);
            Heading.CopyTo(array: grown.Heading, index: 0);
            Kit.CopyTo(array: grown.Kit, index: 0);
            Look.CopyTo(array: grown.Look, index: 0);
            Rides.CopyTo(array: grown.Rides, index: 0);

            return grown;
        }
    }


    private WorldDeliveredDocument m_document;
    private int m_definitionRevision;
    private int m_composingRevision;
    // The definitions the latest two publications were handed, before live state values were bound into them: a delivery
    // that moved holdings alone is judged on these, since binding rebuilds an adopted row's state references.
    private WorldDefinition? m_lastDelivered;
    private WorldDefinition? m_previousDelivered;

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
    // The latest delivery's own-body continuations, kept and published beside the answers.
    private readonly Dictionary<Principal, WorldOwnBody> m_ownBodies = [];
    /// <summary>Reads the exact continuation of the body an owner drives from the latest complete image, never waiting on
    /// a delivery in progress.</summary>
    /// <param name="owner">The principal whose body to read: the owner the authority answers its input under.</param>
    /// <param name="body">The owner's continuation, when the latest image carries one.</param>
    /// <returns><see langword="true"/> when the latest image carries a continuation for <paramref name="owner"/>.</returns>
    public bool TryOwnBody(Principal owner, out WorldOwnBody body) => TryOwned(owner: owner, records: static image => image.OwnBodies, value: out body);

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

            return DeliveredOf(entry: in image.Entries[index], image: in image);
        });

        return body.Active;
    }
    /// <summary>Copies every active solid body other than <paramref name="excludeIndex"/> from the latest complete
    /// image, never waiting on a delivery in progress.</summary>
    /// <param name="excludeIndex">The 0-based entity index to omit, or <c>-1</c> to include all active solid bodies.</param>
    /// <param name="bodies">Receives each solid body's entity index and delivered pose.</param>
    public void CopySolidBodies(int excludeIndex, List<(int Index, WorldDeliveredBody Body)> bodies) {
        ArgumentNullException.ThrowIfNull(argument: bodies);
        _ = m_images.Read(
            state: (Bodies: bodies, Exclude: excludeIndex, Mirror: this),
            read: static (in SnapshotImage image, long version, (List<(int Index, WorldDeliveredBody Body)> Bodies, int Exclude, WorldSessionMirror Mirror) state) => {
                var contacts = Volatile.Read(location: ref state.Mirror.m_kitBodyContacts);

                state.Bodies.Clear();
                for (var index = 0; (index < image.Extent); index++) {
                    if ((index == state.Exclude) || !image.Entries[index].Active) {
                        continue;
                    }

                    ref readonly var entry = ref image.Entries[index];
                    var contact = ((entry.Kit < contacts.Length) ? contacts[entry.Kit] : WorldBodyContactMode.Overlap);

                    if (contact == WorldBodyContactMode.Solid) {
                        state.Bodies.Add(item: (index, DeliveredOf(entry: in entry, image: in image)));
                    }
                }
                return 0;
            }
        );
    }
    private static WorldDeliveredBody DeliveredOf(in SnapshotImage image, in ImageEntry entry) => new(
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

    /// <summary>Reads a row's slot cell as the delivering authority's latest delivery holds it, at an authoritative tick of
    /// that authority: an advancing cell is read at <paramref name="engineTick"/> by its own trait, so a tick past the
    /// latest delivery reads what that authority's row predicts for it. Exact: the value is the cell's raw bits, never a
    /// presentation number.</summary>
    /// <param name="rowName">The row's name in the delivering authority's document.</param>
    /// <param name="tick">The delivering authority's simulation tick the read answers as of.</param>
    /// <param name="engineTick">The delivering authority's engine tick the read answers as of.</param>
    /// <param name="value">The cell's value, or the empty value when the delivery discloses no such row or cell.</param>
    /// <returns><see langword="true"/> when the delivery carries the row and its slot cell holds a value.</returns>
    public bool TryReadDelivered(string rowName, ulong tick, ulong engineTick, out CellValue value) {
        value = default;
        if (!m_synchronizedStateView.TryResolveRow(rowName: rowName, ordinal: out var ordinal) ||
            !m_synchronizedStateView.TryRead(ordinal: ordinal, key: null, target: true, tick: tick, engineTick: engineTick, sample: out var sample) ||
            !sample.Value.HasValue) {
            return false;
        }
        value = sample.Value;
        return true;
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

    private readonly WorldPoseTable m_poses = new();
    private Extras m_extras = new(length: 0);
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
    // Withdraws the mirror itself on every span move, between ending the old span and opening the next.
    private readonly WorldStreamIngress m_ingress;

    /// <summary>Gets the stream this mirror is bound to and the span it holds: what its ingress accepts records
    /// from.</summary>
    public WorldStreamSpan Span => m_ingress.Span;
    /// <summary>Gets the delivery span of what the mirror holds now (<see cref="WorldStreamIngress.DeliverySpan"/>):
    /// ended from an accepted cut, generation step, first bind or close until the mirror has dropped what it held, and
    /// after a close.</summary>
    public Puck.Abstractions.Presentation.DeliverySpan DeliverySpan => m_ingress.DeliverySpan;
    /// <summary>Gets a value indicating whether an open spanned stream has delivered its current non-undisclosed
    /// document to this mirror (as opposed to an unbound route seed or an opening cut).</summary>
    public bool HasStreamDelivery => ((m_ingress.Span is { Stream: not 0UL, Closed: false }) && Document.Version.IsDelivered && !ReferenceEquals(objA: Document.Definition, objB: WorldProjection.Undisclosed));
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
        m_stateView = new WorldDocumentStateView(clock: () => (Tick, EngineTick), delivered: placeholder, gate: m_followGate);
        m_ingress = new WorldStreamIngress(withdraw: Withdraw);
        m_document = new WorldDeliveredDocument(
            Definition: m_stateView.Definition,
            Lifetime: 0,
            Version: default
        );
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
    /// <summary>Gets the counter that moves with <see cref="DefinitionRevision"/> except for a delivery that moved only
    /// placement holdings: the program-rebuild watch component of a scene that presents responses without composing
    /// again (<c>WorldSessionSceneEmitter</c>).</summary>
    public int ComposingRevision => Volatile.Read(location: ref m_composingRevision);
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
        Generation: GenerationOf(index: index)
    );
    /// <summary>The entity's render body color, mirrored verbatim from the snapshot (a pending seat's is already
    /// gray-lerped server-side).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 BodyColor(int index) => ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).BodyColor, index: index);
    public WorldBodyContactMode BodyContact(int index) {
        var contacts = Volatile.Read(location: ref m_kitBodyContacts);
        var kit = ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Kit, index: index);

        return ((kit < contacts.Length)
            ? contacts[kit]
            : WorldBodyContactMode.Overlap
        );
    }
    /// <summary>The entity-owned procedural rig mirrored from the authoritative snapshot.</summary>
    public byte CatalogRig(int index) => ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).CatalogRig, index: index);
    public FixedWorldCollider? Collider(int index) {
        var colliders = Volatile.Read(location: ref m_kitColliders);
        var kit = ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Kit, index: index);

        return ((kit < colliders.Length)
            ? colliders[kit]
            : null
        );
    }
    /// <summary>Copies the latest complete body image out of the mirror's reusable publication storage, with its active
    /// slots and the slots whose body differs from <paramref name="previous"/>'s (<see cref="BodyImage.Changed"/>).</summary>
    /// <param name="previous">The image the caller holds, returned again while the publication and document stand, and
    /// compared against otherwise; <see langword="null"/> for none.</param>
    /// <returns>The same hold when the image stands, or a new image whose caller owns one release.</returns>
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

            var bodies = System.Buffers.ArrayPool<Body>.Shared.Rent(image.Extent);
            try {
                // A previous image of another authority names every body at another address, so it is no base.
                var basis = ((state.Previous is { } held) && string.Equals(a: held.Authority, b: image.Authority, comparisonType: StringComparison.Ordinal)) ? state.Previous : null;
                var reader = new BodyReader(Image: image, Definition: definition, Colliders: Volatile.Read(ref mirror.m_kitColliders), Contacts: Volatile.Read(ref mirror.m_kitBodyContacts));
                var runs = (CaptureRuns ??= new GpuUploadRuns(capacity: 64));
                var changed = (CaptureChanged ??= []);
                changed.Clear();
                // A base read against the same document resolves every look and collider as this image would, so only
                // the slots the body journal names between the two images can differ from it (FillImage's invariant):
                // the rest are copied from it whole.
                var incremental = (basis is not null) && ReferenceEquals(basis.Definition, definition) && (basis.DefinitionRevision == revision) &&
                    (basis.Serial > 0) && mirror.m_bodyJournal.TryCollect(since: basis.Serial, through: image.Serial, rows: image.Rows, into: runs);
                var activeMoved = !incremental || (basis!.Extent != image.Extent);

                if (incremental) {
                    basis!.CopyTo(destination: bodies.AsSpan(start: 0, length: Math.Min(val1: basis.Extent, val2: image.Extent)));
                    for (var run = 0; (run < runs.Count); run++) {
                        var end = Math.Min(val1: (runs.Start(index: run) + runs.Length(index: run)), val2: image.Extent);
                        for (var index = runs.Start(index: run); (index < end); index++) { activeMoved |= reader.Take(basis: basis, bodies: bodies, changed: changed, index: index); }
                    }
                    for (var index = basis.Extent; (index < image.Extent); index++) { _ = reader.Take(basis: basis, bodies: bodies, changed: changed, index: index); }
                } else {
                    for (var index = 0; (index < image.Extent); index++) { _ = reader.Take(basis: basis, bodies: bodies, changed: changed, index: index); }
                }
                // A slot past this image's extent that the base held active is a body that left.
                for (var index = image.Extent; (basis is not null) && (index < basis.Extent); index++) {
                    if (basis.Entry(index, basis.Version).Active) { changed.Add(item: index); activeMoved = true; }
                }

                return new BodyImage(definition, revision, image.Authority, image.Tick, image.Revision, image.StepSeconds,
                    image.ArrivalTimestamp, bodies, image.Extent, version) {
                    Active = (activeMoved ? ActiveSlots(bodies: bodies.AsSpan(start: 0, length: image.Extent)) : basis!.Active),
                    Changed = ((basis is null) ? default : SortedUnique(slots: changed)),
                    Serial = image.Serial,
                    Rows = image.Rows,
                };
            } catch {
                System.Buffers.ArrayPool<Body>.Shared.Return(bodies, clearArray: true);
                throw;
            }
        });
    // A capturing reader's changed slots, reused: CaptureBodyImage runs on any reader's thread, outside every gate.
    [ThreadStatic] private static List<int>? CaptureChanged;
    // Reads a published image's slots as bodies, each look and collider resolved against one document.
    private readonly record struct BodyReader(SnapshotImage Image, WorldDefinition Definition, FixedWorldCollider?[] Colliders, WorldBodyContactMode[] Contacts) {
        private Body Read(int index) {
            var entry = Image.Entries[index];
            return new Body(
                Active: entry.Active,
                Address: new WorldEntityAddress(Authority: Image.Authority, Generation: entry.Generation, Index: index),
                PoseEpoch: entry.PoseEpoch,
                PlacementId: entry.PlacementId,
                PreviousPosition: entry.PreviousPosition,
                PreviousOrientation: entry.PreviousOrientation,
                CurrentPosition: entry.CurrentPosition,
                CurrentOrientation: entry.CurrentOrientation,
                Color: entry.Color,
                Look: WorldDefinitionRows.ResolveLook(index: entry.Look, rows: Definition.Looks),
                CatalogRig: entry.CatalogRig,
                Collider: (entry.Kit < Colliders.Length) ? Colliders[entry.Kit] : null,
                Contact: (entry.Kit < Contacts.Length) ? Contacts[entry.Kit] : WorldBodyContactMode.Overlap
            );
        }
        // Reads one slot into bodies and notes it in changed when it differs from the base's, reporting whether its
        // active flag moved.
        public bool Take(BodyImage? basis, Body[] bodies, List<int> changed, int index) {
            bodies[index] = Read(index: index);
            if (basis is null) { return false; }
            if (index >= basis.Extent) { changed.Add(item: index); return bodies[index].Active; }
            ref readonly var before = ref basis.Entry(index, basis.Version);
            if (before == bodies[index]) { return false; }
            changed.Add(item: index);
            return (before.Active != bodies[index].Active);
        }
    }
    // The active slots of a body table, in ascending order.
    private static ImmutableArray<int> ActiveSlots(ReadOnlySpan<Body> bodies) {
        var count = 0;
        foreach (ref readonly var body in bodies) { count += (body.Active ? 1 : 0); }
        var active = ImmutableArray.CreateBuilder<int>(initialCapacity: count);
        for (var index = 0; (index < bodies.Length); index++) {
            if (bodies[index].Active) { active.Add(item: index); }
        }
        return active.MoveToImmutable();
    }
    // The slots once each, in ascending order: a journal window over several frames can name a slot more than once.
    private static ImmutableArray<int> SortedUnique(List<int> slots) {
        if (slots.Count == 0) { return []; }
        slots.Sort();
        var count = 1;
        for (var index = 1; (index < slots.Count); index++) { count += ((slots[index] != slots[(index - 1)]) ? 1 : 0); }
        var unique = ImmutableArray.CreateBuilder<int>(initialCapacity: count);
        unique.Add(item: slots[0]);
        for (var index = 1; (index < slots.Count); index++) {
            if (slots[index] != slots[(index - 1)]) { unique.Add(item: slots[index]); }
        }
        return unique.MoveToImmutable();
    }

    /// <summary>A retained delivery for an adjacency. Entries are indexed by the destination's entity slot.</summary>
    public sealed class BodyImage {
        private Body[]? m_entries;
        private int m_readers = 1;

        internal BodyImage(WorldDefinition definition, int definitionRevision, string authority, ulong tick,
            int revision, float stepSeconds, long arrivalTimestamp, Body[] entries, int extent, long version) {
            Definition = definition;
            DefinitionRevision = definitionRevision;
            Authority = authority;
            Tick = tick;
            Revision = revision;
            StepSeconds = stepSeconds;
            ArrivalTimestamp = arrivalTimestamp;
            m_entries = entries;
            Extent = extent;
            Version = version;
        }

        /// <summary>Gets the delivered definition.</summary>
        public WorldDefinition Definition { get; }
        /// <summary>Gets the delivered definition revision.</summary>
        public int DefinitionRevision { get; }
        /// <summary>Gets the delivered authority.</summary>
        public string Authority { get; }
        /// <summary>Gets the delivered simulation tick.</summary>
        public ulong Tick { get; }
        /// <summary>Gets the delivered body-set revision.</summary>
        public int Revision { get; }
        /// <summary>Gets the interval between delivered simulation ticks.</summary>
        public float StepSeconds { get; }
        /// <summary>Gets the host timestamp when the snapshot arrived.</summary>
        public long ArrivalTimestamp { get; }
        /// <summary>Gets the number of body slots in this image.</summary>
        public int Extent { get; }
        /// <summary>Gets the snapshot publication number.</summary>
        public long Version { get; }

        /// <summary>Reads one body while the caller holds this publication.</summary>
        /// <param name="index">The slot in the delivered image.</param>
        /// <param name="expectedVersion">The publication number pinned by the reader.</param>
        /// <returns>The held body.</returns>
        public ref readonly Body Entry(int index, long expectedVersion) {
            if ((expectedVersion != Version) || (Volatile.Read(ref m_entries) is not { } held)) {
                throw new InvalidOperationException("The reader no longer holds this body publication.");
            }
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Extent);
            return ref held[index];
        }

        // The body journal frame the image was published through and the journal's row count at it, which a capture
        // over this image collects the slots it changed from.
        internal long Serial { get; init; }
        internal int Rows { get; init; }

        // Copies the leading bodies while the caller holds this publication.
        internal void CopyTo(Span<Body> destination) {
            if (Volatile.Read(ref m_entries) is not { } held) { throw new InvalidOperationException("The reader no longer holds this body publication."); }
            held.AsSpan(start: 0, length: destination.Length).CopyTo(destination);
        }

        /// <summary>Retains the body storage while the caller owns this publication.</summary>
        public void Retain() => _ = Interlocked.Increment(ref m_readers);

        /// <summary>Returns the body storage once all owners have released the publication.</summary>
        public void Release() {
            if (Interlocked.Decrement(ref m_readers) != 0) { return; }
            var held = Interlocked.Exchange(ref m_entries, null)!;

            System.Buffers.ArrayPool<Body>.Shared.Return(held, clearArray: true);
        }
        /// <summary>Gets the slots whose body is active, in ascending order.</summary>
        public ImmutableArray<int> Active { get; init; } = [];
        /// <summary>Gets the slots whose body differs from the image this one was captured over
        /// (<see cref="CaptureBodyImage"/>'s previous image), in ascending order, or the default array when it was
        /// captured over none, or over another authority's, so every slot may differ.</summary>
        public ImmutableArray<int> Changed { get; init; }
    }

    /// <summary>One slot from a retained body image, with its look and collider resolved against the delivered document.</summary>
    public readonly record struct Body(bool Active, WorldEntityAddress Address, int PoseEpoch, string? PlacementId,
        Vector3 PreviousPosition, Quaternion PreviousOrientation, Vector3 CurrentPosition, Quaternion CurrentOrientation,
        Vector3 Color, WorldLook Look, byte CatalogRig, FixedWorldCollider? Collider, WorldBodyContactMode Contact);

    // Publishes the entity table, inputs and arrival history as one complete image (see m_images). Called by a writer
    // holding m_snapshotWriteGate once its writes are done.
    private void PublishImage() => m_images.Publish(state: this, fill: static (ref SnapshotImage image, WorldSessionMirror mirror) => mirror.FillImage(image: image));
    // Brings an older image current: the slots the body journal names since the frame it was filled through, and every
    // slot past its own extent, or every slot below m_extent when the journal cannot prove the window. Invariant: every
    // write to a field an image slot copies is journaled by the delivery that makes it (ApplySnapshot's moved test, a
    // whole frame for an active-set change, a seeded route or another authority), and the image's frame is the one
    // closed beside it (PublishBodyChanges before PublishImage), so an incremental fill copies every slot that differs.
    // Slots at or above m_extent are still as constructed.
    private void FillImage(SnapshotImage image) {
        var extent = m_extent;
        var extras = m_extras;
        var serial = m_bodySerial;
        var rows = m_poses.Capacity;
        var runs = m_imageRuns;
        var whole = (image.Entries.Length < extent) || !m_bodyJournal.TryCollect(since: image.Serial, through: serial, rows: rows, into: runs);

        if (image.Entries.Length < extent) {
            image.Entries = new ImageEntry[((int)(((uint)extent)).NextPowerOfTwo())];
        }
        if (whole) {
            for (var index = 0; (index < extent); index++) { image.Entries[index] = Entry(index: index, extras: extras); }
        } else {
            for (var run = 0; (run < runs.Count); run++) {
                var end = Math.Min(val1: (runs.Start(index: run) + runs.Length(index: run)), val2: extent);
                for (var index = runs.Start(index: run); (index < end); index++) { image.Entries[index] = Entry(index: index, extras: extras); }
            }
            for (var index = image.Extent; (index < extent); index++) { image.Entries[index] = Entry(index: index, extras: extras); }
        }
        image.Serial = serial;
        image.Rows = rows;
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
        image.OwnBodies.Clear();
        foreach (var (owner, body) in m_ownBodies) { image.OwnBodies[owner] = body; }
        m_arrivals.CopyTo(array: image.Arrivals, index: 0);
        image.ArrivalCount = m_arrivalCount;
    }
    // One slot as the mirror holds it now. Under m_snapshotWriteGate.
    private ImageEntry Entry(int index, Extras extras) => new(
        Active: m_poses.IsActive(index: index),
        CatalogRig: extras.CatalogRig[index],
        Color: extras.BodyColor[index],
        CurrentOrientation: m_poses.CurrentOrientation(index: index),
        CurrentPosition: m_poses.CurrentPosition(index: index),
        Facts: extras.Facts[index],
        Generation: extras.Generation[index],
        Heading: extras.Heading[index],
        Kit: extras.Kit[index],
        Look: extras.Look[index],
        PlacementId: m_poses.PlacementId(index: index),
        PoseEpoch: m_poses.PoseEpoch(index: index),
        PreviousOrientation: m_poses.PreviousOrientation(index: index),
        PreviousPosition: m_poses.PreviousPosition(index: index)
    );
    // Grows the pose table and the extras to hold a slot before a writer touches it, and returns the extras the writer
    // writes. Under m_snapshotWriteGate; the index is a body index (WorldBodiesLimits.IsBodyIndex).
    private Extras Extend(int index) {
        if (index >= m_extent) { m_extent = (index + 1); }
        m_poses.EnsureCapacity(count: m_extent);

        var extras = m_extras;

        if (extras.Length < m_poses.Capacity) {
            extras = extras.Grown(length: m_poses.Capacity);
            Volatile.Write(location: ref m_extras, value: extras);
        }

        return extras;
    }
    private int GenerationOf(int index) {
        var generation = Volatile.Read(location: ref m_extras).Generation;

        return ((((uint)index) < ((uint)generation.Length)) ? Volatile.Read(location: ref generation[index]) : 0);
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
        // The body journal frame the entries are current through, and the journal's row count at it; zero before the
        // first fill, which a window from it can never prove.
        public long Serial;
        public int Rows;

        public string Authority = string.Empty;

        public ulong Tick;
        public int Revision;
        public float StepSeconds;

        public long ArrivalTimestamp = arrivalTimestamp;
        public readonly Dictionary<Principal, WorldInputPublication> Inputs = [];
        public readonly Dictionary<Principal, WorldInputAnswer> Answers = [];
        public readonly Dictionary<Principal, WorldOwnBody> OwnBodies = [];
        public readonly long[] Arrivals = new long[ArrivalHistory];

        public int ArrivalCount;
    }

    /// <summary>The entity's latest-tick render attitude (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion CurrentOrientation(int index) => m_poses.CurrentOrientation(index: index);
    /// <summary>The entity's latest-tick render position (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 CurrentPosition(int index) => m_poses.CurrentPosition(index: index);

    // Adopts a delivered definition through the state view, the mirror's one door for documents, and publishes it with
    // its version as one pair; the same pair delivered again is kept, so a repeated delivery allocates nothing. Returns
    // the published definition. valuesFollow is the state view's (WorldDocumentStateView.Adopt): the delivery kept the
    // state section's shape, so the view keeps the arena and field cells it holds, as a client's does.
    private WorldDefinition Publish(WorldDefinition definition, WorldDocumentVersion version, bool valuesFollow = false) {
        lock (m_followGate) {
            var adopted = m_stateView.Adopt(delivered: definition, valuesFollow: valuesFollow);

            m_previousDelivered = m_lastDelivered;
            m_lastDelivered = definition;
            var current = Volatile.Read(location: ref m_document);

            if (ReferenceEquals(objA: current.Definition, objB: adopted) && (current.Version == version)) {
                return adopted;
            }

            Volatile.Write(
                location: ref m_document,
                value: new WorldDeliveredDocument(
                    Definition: adopted,
                    Lifetime: (current.Lifetime + ((current.Version.Activation != version.Activation) ? 1 : 0)),
                    Version: version
                )
            );

            return adopted;
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
    /// <remarks>The rows apply to the held definition without reshaping the state view, which the rows never move.
    /// Rows that moved placement holdings alone, none of them on a row showing a creation that derives a facet, leave
    /// the composing revision to the scene that presents them, as a state delivery that moved them does.</remarks>
    public void DeliverPresentation(WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (!m_ingress.Admit(stream: in stream)) { return; }
        var started = Stopwatch.GetTimestamp();
        lock (m_snapshotWriteGate) {
            lock (m_followGate) {
                var held = Document.Definition;
                if (!rows.TryApply(held, out var changed)) { throw new InvalidOperationException("A presentation delivery must match the mirror's creation roster and placement rows."); }
                var adopted = Publish(changed, version, valuesFollow: true);
                _ = WorldPlacementResponse.CompareRows(after: adopted, before: held, holdings: out var holdings, rows: rows, solid: out _);
                _ = Interlocked.Increment(ref m_definitionRevision);
                if (!(holdings && rows.MovesHoldingsOnly)) { _ = Interlocked.Increment(location: ref m_composingRevision); }
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

        lock (m_snapshotWriteGate) {
            lock (m_followGate) {
                var adopted = Publish(
                    definition: definition,
                    version: version
                );

                Volatile.Write(
                    location: ref m_kitColliders,
                    value: CompileColliders(definition: adopted)
                );
                Volatile.Write(
                    location: ref m_kitBodyContacts,
                    value: CompileBodyContacts(definition: adopted)
                );
                _ = Interlocked.Increment(location: ref m_definitionRevision);
                _ = Interlocked.Increment(location: ref m_composingRevision);
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
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) => _ = m_ingress.Cut(stream: in stream, terminal: terminal);
    /// <inheritdoc/>
    /// <remarks>The mirror drops what it held exactly as for a terminal record, and takes records again only from a
    /// stream that never closed on it.</remarks>
    public void CloseSpan(ulong stream) => _ = m_ingress.Close(stream: stream);
    // Drops everything an ended span delivered: the undisclosed document in place of the delivered one, an empty table
    // in place of its bodies, and no route seed or queued cue, applied through the ordinary delivery paths so every
    // reader follows it. The undisclosed document, never the construction placeholder, which can be a delivered
    // document (a route's, or the authority's last). The ingress runs it on every span move, after the old span ended
    // and before the next opens (WorldStreamIngress).
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
            m_ownBodies.Clear();
            foreach (ref readonly var body in snapshot.OwnBodies.Span) { m_ownBodies[body.Owner] = body; }
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
            // Another authority's snapshot moves every entity's address.
            m_deliveredWhole |= !string.Equals(a: Volatile.Read(location: ref m_authority), b: snapshot.Authority, comparisonType: StringComparison.Ordinal);

            foreach (ref readonly var entry in snapshot.Entries.Span) {
                var index = entry.Index;

                if (
                    !WorldBodiesLimits.IsBodyIndex(index: index) ||
                    (preserveSeed && (seeded is { } preserved) && (index == preserved.Index) &&
                        (!string.Equals(
                            a: snapshot.Authority,
                            b: preserved.Authority,
                            comparisonType: StringComparison.Ordinal
                        ) || (entry.Generation != preserved.Generation)))
                ) {
                    continue;
                }

                var extras = Extend(index: index);
                // Every field a published image slot copies (ImageEntry), as it stood before the entry: the journal
                // frame this delivery closes names each slot it moved, which is what an image is filled by (FillImage)
                // and a body image copied by (CaptureBodyImage), and a superset of what a stamp source's presented
                // inputs read besides its look (BodyChanges).
                var moved = (
                    (m_poses.IsActive(index: index) != entry.Active) ||
                    (extras.Facts[index] != entry.Facts) ||
                    (extras.Generation[index] != entry.Generation) ||
                    (m_poses.CurrentPosition(index: index) != entry.Position) ||
                    (m_poses.CurrentOrientation(index: index) != entry.Orientation) ||
                    (m_poses.PreviousPosition(index: index) != m_poses.CurrentPosition(index: index)) ||
                    (m_poses.PreviousOrientation(index: index) != m_poses.CurrentOrientation(index: index)) ||
                    (extras.BodyColor[index] != entry.BodyColor) ||
                    (extras.Kit[index] != entry.Kit) ||
                    (extras.Look[index] != entry.Look) ||
                    (extras.CatalogRig[index] != entry.CatalogRig) ||
                    (extras.Heading[index] != entry.Heading) ||
                    !string.Equals(a: m_poses.PlacementId(index: index), b: entry.PlacementId, comparisonType: StringComparison.Ordinal)
                );

                extras.BodyColor[index] = entry.BodyColor;
                extras.Kit[index] = entry.Kit;
                extras.Look[index] = entry.Look;
                extras.CatalogRig[index] = entry.CatalogRig;
                Volatile.Write(
                    location: ref extras.Generation[index],
                    value: entry.Generation
                );
                extras.Heading[index] = entry.Heading;
                extras.Facts[index] = entry.Facts;
                extras.Rides[index] = (entry.Mount + 1);
                // The pose table publishes the entry's active flag last, after every field above, so a reader on
                // another thread (a federated observer delivers on its socket task) that acquires IsActive sees the
                // completed record.
                // An entry the pose table refuses still wrote the extras above, so a moved one is journaled either way.
                var admitted = m_poses.TryAdmit(
                    admission: out var admission,
                    easeCorrections: false,
                    entry: in entry
                );
                if (moved || (admitted && (admission.Step != WorldPoseStep.Interpolated))) {
                    _ = m_deliveredChanges.Add(index: index);
                }
            }

            m_poses.Complete(preserved: ((preserveSeed && (seeded is { } kept))
                ? kept.Index
                : -1
            ));
            // A body that left is in no entry, so a delivery that changed which bodies are active owes every body.
            m_deliveredWhole |= m_poses.ActiveChanged;
            PublishBodyChanges();

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
            var arrival = WorldDeliveryClock.Arrival;

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
                        WorldBodiesLimits.IsBodyIndex(index: entry.Index) &&
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
            var document = Volatile.Read(location: ref m_document);
            var previous = document.Definition;
            // The values follow in the snapshots, under the same state section, exactly when WorldClient.DeliverState
            // keeps its view's shape: a value-only delivery within one activation.
            var adopted = Publish(
                definition: definition,
                valuesFollow: (stamp.ArenaValues && (version.Activation == document.Version.Activation)),
                version: version
            );

            // A delivery that moved placement rows is a scene change to this mirror's readers, which watch the definition
            // revision; one that moved holdings alone leaves the composing revision to the scene that presents them.
            if (WorldPlacementResponse.ComparePlacements(after: adopted, before: previous, holdings: out _, solid: out _) != WorldPlacementChange.None) {
                var holdings = ((WorldPlacementResponse.ComparePlacements(after: definition, before: m_previousDelivered, holdings: out var held, solid: out _) == WorldPlacementChange.Appearance) && held);

                _ = Interlocked.Increment(location: ref m_definitionRevision);
                if (!holdings) { _ = Interlocked.Increment(location: ref m_composingRevision); }
            }

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
    /// <remarks>A fork's partitions read the state mirror without a lock, and this is its one writer, so a call from
    /// inside a partition (<see cref="Puck.Hosting.Jobs.JobPool.InPartition"/>) returns the mirror as the last follow
    /// outside one left it. A delivery that arrives on a socket task while a frame's fork runs therefore waits for the
    /// next follow, and nothing writes the state mirror during the fork whichever thread delivers.</remarks>
    public WorldStateMirror FollowState() {
        lock (m_followGate) {
            if ((m_state is { } followed) && Puck.Hosting.Jobs.JobPool.InPartition) {
                return followed;
            }

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
    public BodyFacts Facts(int index) => ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Facts, index: index);
    /// <summary>Gets the kit row an entity runs in the latest snapshot (<see cref="EntitySnapshot.Kit"/>).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public int Kit(int index) => ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Kit, index: index);
    /// <summary>Gets the index of the body an entity rides in the latest snapshot (<see cref="EntitySnapshot.Mount"/>),
    /// or -1 when it rides nothing or was never delivered.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public int Mount(int index) => (ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Rides, index: index) - 1);
    /// <summary>Gets an entity's latest authoritative heading in radians about world up (<see cref="EntitySnapshot.Heading"/>)
    /// — not interpolated; the composition seam that reads it wants the authority's current value.</summary>
    public float Heading(int index) => ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Heading, index: index);
    /// <summary>Whether the entity at <paramref name="index"/> was active (drawn) in the latest snapshot.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_poses.IsActive(index: index);
    /// <summary>Gets the number of entity slots the mirror holds: the highest body a delivery or a seeded route named,
    /// rounded up as the mirror grows with the destination. A slot past it is inactive.</summary>
    public int EntityCapacity => m_poses.Capacity;
    /// <summary>Appends the index of every entity the latest snapshot left active, in ascending order: the pose table's
    /// live set (<see cref="WorldPoseTable.Live"/>), read between two deliveries, never the capacity.</summary>
    /// <param name="bodies">The list the indices are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bodies"/> is <see langword="null"/>.</exception>
    public void CopyActiveBodies(List<int> bodies) {
        ArgumentNullException.ThrowIfNull(argument: bodies);

        var first = bodies.Count;

        lock (m_snapshotWriteGate) {
            foreach (var index in m_poses.Live) {
                if (m_poses.IsActive(index: index)) {
                    bodies.Add(item: index);
                }
            }
        }

        bodies.Sort(comparer: null, count: (bodies.Count - first), index: first);
    }
    /// <summary>Collects the entities a delivery changed since a cursor's last commit, as a stamp source's body journal
    /// reads them (<c>IWorldStampSource.BodyChanges</c>): one journal frame per delivery names each entity whose active
    /// flag, facts, generation, endpoints or pose epoch it moved, or whose interpolated pose it moved by stopping it, and
    /// a delivery that changed which entities are active, another authority's, or a seeded route owes every entity. A
    /// moving entity's interpolated pose also moves between deliveries as the presentation alpha does
    /// (<see cref="CopyMovingBodies"/>). The caller commits the cursor once it has consumed the runs.</summary>
    /// <param name="cursor">The consumer's cursor, used on one thread.</param>
    /// <param name="into">The runs of entity indices, cleared first.</param>
    /// <returns><see langword="true"/> when the answer owes every entity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> or <paramref name="into"/> is
    /// <see langword="null"/>.</exception>
    public bool CollectBodyChanges(GpuRowCursor cursor, GpuUploadRuns into) {
        // Deliveries publish the journal, possibly on a socket task; it holds its own lock.
        return m_bodyJournal.Collect(cursor: cursor, into: into, rows: m_poses.Capacity, through: -1L);
    }
    /// <summary>Appends the index of every entity whose interpolation endpoints differ in the latest delivery, the
    /// entities whose interpolated pose moves with the presentation alpha between deliveries.</summary>
    /// <param name="bodies">The list the indices are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bodies"/> is <see langword="null"/>.</exception>
    public void CopyMovingBodies(List<int> bodies) {
        ArgumentNullException.ThrowIfNull(argument: bodies);

        lock (m_snapshotWriteGate) {
            bodies.AddRange(collection: m_moving);
        }
    }
    // Closes one journal frame over the delivery just applied and finds the entities it left moving. Under
    // m_snapshotWriteGate.
    private void PublishBodyChanges() {
        m_bodySerial = m_bodyJournal.Publish(everything: m_deliveredWhole, owed: m_deliveredChanges.Members, rows: m_poses.Capacity);
        m_deliveredChanges.Clear();
        m_deliveredWhole = false;
        m_moving.Clear();
        foreach (var index in m_poses.Live) {
            if ((m_poses.PreviousPosition(index: index) != m_poses.CurrentPosition(index: index)) ||
                (m_poses.PreviousOrientation(index: index) != m_poses.CurrentOrientation(index: index))) {
                m_moving.Add(item: index);
            }
        }
    }

    // The delivery journal (CollectBodyChanges): the entities the delivery in progress changed and whether it owes every
    // entity, the journal, and the entities the latest delivery left moving. Under m_snapshotWriteGate.
    private readonly MarkedIndexSet m_deliveredChanges = new();
    private bool m_deliveredWhole = true;
    private readonly GpuSharedChangedRows m_bodyJournal = new(stableRows: true);
    private readonly List<int> m_moving = [];
    // The journal frame the delivery last closed, which the image published beside it is current through, and the
    // runs FillImage collects into. Under m_snapshotWriteGate.
    private long m_bodySerial;
    private readonly GpuUploadRuns m_imageRuns = new(capacity: 64);
    // A capturing reader's runs: CaptureBodyImage runs on any reader's thread, outside every gate.
    [ThreadStatic] private static GpuUploadRuns? CaptureRuns;

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
        index: ArrayCapacity.ElementOr(array: Volatile.Read(location: ref m_extras).Look, index: index)
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

        if (!WorldBodiesLimits.IsBodyIndex(index: index)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(route),
                message: $"route entity {index} is outside 0..{(WorldBodiesLimits.IndexLimit - 1)}, the body-index format bound"
            );
        }

        lock (m_snapshotWriteGate) {
            ApplyDefinition(
                definition: route.Definition,
                version: route.Version
            );
            var position = route.Position.ToVector3();
            var orientation = route.Orientation.ToQuaternion();
            var extras = Extend(index: index);

            // A route carries one attitude; its yaw is the heading.
            extras.Heading[index] = VectorFunctions.YawOf(attitude: orientation);
            extras.BodyColor[index] = route.BodyColor;
            extras.Kit[index] = route.Kit;
            extras.Look[index] = route.Look;
            extras.CatalogRig[index] = route.CatalogRig;
            // A route describes no action state, so a new occupant of the slot reads none until its first snapshot
            // entry, and a reader takes its own default (a body's scale reads one) in place of a stale or zero value.
            if (extras.Generation[index] != route.Entity.Generation) {
                lock (m_followGate) {
                    m_stateView.ForgetBodyState(body: index);

                    var forgotten = m_stateView.TakeMovedBodyState(moved: m_movedBodyState);

                    if (forgotten > 0) {
                        lock (m_stampGate) {
                            for (var moved = 0; (moved < forgotten); moved++) {
                                NoteRow(ordinal: m_movedBodyState[moved]);
                            }
                        }
                    }
                }
            }
            Volatile.Write(
                location: ref extras.Generation[index],
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
            m_deliveredWhole = true;
            PublishBodyChanges();
            m_seededRouteEntity = route.Entity;
            m_seededRouteTick = route.Tick;
            PublishImage();
        }
    }
}
