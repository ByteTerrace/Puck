using Puck.Abstractions.Collections;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Predicts each remote seat's own body with the authority's own body step: the seat's authority delivers the exact
/// continuation of the body after each delivered tick (<see cref="WorldOwnBody"/>, disclosed to the seat alone); the
/// prediction restores it into a private population built from the document that authority delivered, then steps it
/// through the batches the seat sent that the authority had not yet taken, to the authority tick an input sent now
/// lands at. The stepped body is kept between predictions as a path, and each prediction steps only the ticks new since
/// the last: a delivery that agrees with the path at its tick leaves it standing, and one that disagrees replaces it.
/// </summary>
/// <remarks>
/// <para>The invariant: a prediction runs the one body step (<see cref="WorldPopulation.AdvanceSeats"/> over
/// <see cref="WorldBody"/>) on the one input fold (<see cref="WorldAcceptedInput.Fold"/>, taken through the body's one
/// owner-input write) every authority runs, so equal continuations and equal inputs give equal bodies, and a
/// correction is a real divergence, never rounding. What enforces it: the client project cannot name the step, and this
/// type, in the composition root, holds no motion code of its own.</para>
/// <para>The path's invariant: the body a seat's path holds at its tip is the body a replay from the latest
/// continuation would reach, restored and stepped through the same batches on the same ticks. The path is kept only
/// while the latest continuation agrees with it at its tick, in what a restore reads
/// (<see cref="WorldPopulation.PredictedBodyDigest(int)"/>, the kit, the body's sections, registers and the attached
/// solid rows) and in its engine tick, and while every batch the path took on every tick from there to its tip is the
/// one a replay takes now (one schedule rule, which the step and the check both read). What enforces it: one private
/// door decides on every prediction whether the path stands, and the population is this type's own,
/// written only by a restore and the step. What the population holds outside the predicted slot and the attached rows
/// is not compared; a restore never reset it either.</para>
/// <para>Where it stops: the step also reads what lies outside the body. The static ground, gravity, kits and motion
/// programs come from the delivered document and the moving solids' poses from the continuation, so they are exact at
/// its tick; other bodies, a moving solid's motion after that tick and rule writes to the body's registers are not
/// predicted, and show as a correction at the next delivery. A seat riding another body is not predicted, and neither is
/// one whose predicted steps pass through a portal's glass (<see cref="WorldFaceRegion.Sweep"/>): the crossing is the
/// authority's, so the seat steers from what was delivered until the crossing is delivered.</para>
/// <para>Timing is on the authority's clock alone: each sent batch carries the authority tick it was sent at, as the
/// seat's clock maps it from the deliveries seen (<see cref="WorldAuthorityClock"/>), and the lead from that tick to the
/// tick that took it is a difference of two authority ticks, the link's delay. A prediction reaches as far as the sent
/// history holds the batch its continuation holds: a correction costs one replay across the round trip, and no step
/// count switches prediction off.</para>
/// <para>Nothing a prediction does reaches an authority or the delivered mirror: its population and arena are its own.
/// Every member runs on the host pump.</para>
/// </remarks>
/// <param name="client">The presentation client whose seats are predicted.</param>
/// <param name="packageDocument">Returns the client's own whole copy of the document a documentId names, from the package it
/// shipped with, or <see langword="null"/> when it holds none; a prediction then builds from the document delivered.</param>
public sealed class WorldSeatPrediction(WorldClient client, Func<string?, WorldDefinition?> packageDocument) : IWorldSeatPrediction {
    /// <summary>The authority ticks a seat's predicted path keeps, oldest dropped first: a continuation further behind the
    /// path's tip than this is replayed from rather than checked against the path.</summary>
    public const int PathTicks = 512;

    // The serial of no batch: a path or continuation that has taken none of the held batches.
    private const long NoBatch = long.MinValue;

    // One tick of a seat's predicted path, after its step: the engine tick it completed at, the last batch taken by its
    // serial (WorldSeatSentInput.FirstSerial), the body's digest (WorldPopulation.PredictedBodyDigest) and its pose.
    private readonly record struct PathTick(ulong Tick, ulong EngineTick, long Applied, ulong Digest, FixedVector3 Position, FixedQuaternion Orientation);

    // Why a path does not stand for the latest continuation.
    private enum PathCause : byte {
        Agrees,
        // No path covers the continuation's tick: none yet, another document or population, or a tick outside it.
        Restart,
        // The continuation's body or engine tick is not the path's at that tick.
        State,
        // A batch the path took, on some tick from the continuation to the tip, is not the one a replay takes.
        Input,
    }

    private sealed class Seat {
        public WorldDefinition? Definition;
        public WorldPopulation? Population;
        public ReadOnlyMemory<byte> EntryBytes;
        public WorldPopulationEntryCheckpoint Entry;
        public WorldAttachedSolidCheckpoint[] Attached = [];
        public bool Decoded;
        // The decoded continuation's digest, taken when first compared.
        public ulong? EntryDigest;
        public WorldAuthorityEndpoint? Endpoint;
        // The tip of each prediction by the authority tick it reached, and the distance by which the authority last missed one.
        public readonly (ulong Tick, FixedVector3 Position)[] Trail = new (ulong Tick, FixedVector3 Position)[64];
        public ulong ConfirmedAt = ulong.MaxValue;
        public FixedQ4816? Error;
        // The ticks from the authority tick a batch is sent at to the one that takes it, each answered batch's answer
        // tick less its send tick (WorldSeatSentInput.Sent.Tick): the least of the recent ones, since a batch held up in a
        // burst lands later than the link's own delay, and predicting that far ahead would carry the body past where the
        // authority takes the next input. Both ticks are the authority's, so the lead is the link's delay alone.
        public readonly WindowedExtremes Leads = new(length: 16);
        public uint LeadSequence;
        public ulong LeadEpoch;
        // The owner incarnation (WorldSeatSentInput.Incarnation) the leads were timed under.
        public long Incarnation;
        public double PredictedAt = double.NaN;
        public bool Predicted;
        public string Reason = "no prediction asked";
        public string? PopulationRefusal;
        public WorldPredictedBody Body;
        // The path: the population's local slot 0 holds PathBody, restored from the continuation at BaseTick in PathSource
        // and stepped through TipTick, and Path holds every tick of it from BaseTick (the latest PathTicks of them).
        public readonly PathTick[] Path = new PathTick[PathTicks];
        public bool PathLive;
        public WorldDefinition? PathSource;
        public WorldBody? PathBody;
        public ulong BaseTick;
        public ulong TipTick;
        // The tick whose step carried the body through a door's glass, which is the path's tip, and what it reached.
        public ulong? CrossedAt;
        public string? CrossedReason;
    }

    private readonly Seat[] m_seats = [.. Enumerable.Range(start: 0, count: PlayerRoster.MaxSlots).Select(selector: static _ => new Seat())];
    private readonly bool[] m_edges = new bool[WorldBodiesLimits.LocalSeatCount];

    /// <summary>Gets the predictions made since boot.</summary>
    public long Predictions { get; private set; }
    /// <summary>Gets the authority ticks stepped since boot: each tick a path advanced past its tip, and each tick a restore
    /// replayed.</summary>
    public long StepsTaken { get; private set; }
    /// <summary>Gets the private populations built since boot, one for each delivered document a seat predicted in.</summary>
    public long PopulationsBuilt { get; private set; }
    /// <summary>Gets the paths restored from a continuation since boot: a seat's first, each correction, and each path
    /// that no longer covered the continuation's tick.</summary>
    public long Restores { get; private set; }
    /// <summary>Gets the restores since boot whose continuation disagreed with the path's own body at its tick.</summary>
    public long StateCorrections { get; private set; }
    /// <summary>Gets the restores since boot whose path took, on some tick from the continuation to its tip, another batch
    /// than a replay takes: a lead that moved, a clock that re-anchored, or a batch the authority took on another tick.</summary>
    public long InputCorrections { get; private set; }
    /// <summary>Gets the restores since boot whose restored body digested otherwise than the continuation it was restored
    /// from: a restore that is not exact makes every later delivery a correction.</summary>
    public long InexactRestores { get; private set; }
    /// <summary>Gets the predictions since boot that <see cref="CheckReplays"/> replayed from their continuation.</summary>
    public long ReplayChecks { get; private set; }
    /// <summary>Gets the checked predictions since boot whose path disagreed with the replay from their continuation, in
    /// the body at the tip or in the crossing that stopped it.</summary>
    public long ReplayMismatches { get; private set; }
    /// <summary>Gets or sets a value indicating whether each prediction that continues a seat's path also replays from its
    /// continuation and compares the two tips (<see cref="ReplayChecks"/>, <see cref="ReplayMismatches"/>): the check that
    /// the path is the replay. A checked prediction costs a whole replay.</summary>
    public bool CheckReplays { get; set; }

    /// <inheritdoc/>
    public bool TryPredict(int slot, out WorldPredictedBody body) {
        var seat = m_seats[slot];
        var clock = client.SeatClockSeconds;

        if (seat.PredictedAt != clock) {
            seat.PredictedAt = clock;
            seat.Reason = (Predict(body: out seat.Body, seat: seat, slot: slot) ?? string.Empty);
            seat.Predicted = (seat.Reason.Length == 0);
        }

        body = seat.Body;
        return seat.Predicted;
    }

    /// <inheritdoc/>
    public string Describe(int slot) {
        var seat = m_seats[slot];
        var counts = string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{Predictions} prediction(s), {StepsTaken} step(s), {Restores} restore(s), {(StateCorrections + InputCorrections)} correction(s) ({StateCorrections} of state, {InputCorrections} of input), {InexactRestores} inexact restore(s), {PopulationsBuilt} population(s)");

        if (CheckReplays || (ReplayChecks != 0L)) {
            counts = string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{counts}; {ReplayChecks} replay check(s), {ReplayMismatches} mismatch(es){(CheckReplays ? string.Empty : ", checks off")}");
        }
        if (!seat.Predicted) {
            return $"off({seat.Reason}); {counts}";
        }
        var missed = ((seat.Error is { } error)
            ? string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"last missed by {(double)error:0.###} m")
            : "no prediction answered yet");

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"predicted {seat.Body.Steps} tick(s) past tick {seat.Body.ContinuationTick}, {missed}; lead {seat.Leads.Minimum} tick(s), the authority's clock moved {client.SeatSentInput(slot: slot).Clock.Moved} tick(s) against the seat's; {counts}");
    }
    // Predicts one seat's body, answering null, or why it predicted nothing.
    private string? Predict(Seat seat, int slot, out WorldPredictedBody body) {
        body = default;
        if (!client.TrySeatInputOwner(owner: out var owner, route: out var route, slot: slot)) {
            return "the seat's input is not admitted at its authority";
        }
        if (route.Endpoint.ClockOwnedHere || (route.EntityIndex < 0)) {
            return "the seat's authority runs in this process";
        }

        var endpoint = route.Endpoint;
        var mirror = endpoint.Mirror;
        var definition = endpoint.Definition;

        if (ReferenceEquals(objA: definition, objB: WorldProjection.Undisclosed)) {
            return "no document is delivered";
        }
        if (!mirror.TryOwnBody(body: out var own, owner: owner)) {
            return $"no continuation is delivered for {owner.Describe()}";
        }
        if ((own.Body != route.EntityIndex) || (own.Generation != route.Entity.Generation)) {
            return "the delivered continuation is another body's";
        }
        if (mirror.Mount(index: route.EntityIndex) >= 0) {
            return "the body rides another";
        }

        var sent = client.SeatSentInput(slot: slot);

        // Every authority numbers its own ticks: a seat that crossed to another times its input against it afresh.
        if (!ReferenceEquals(objA: seat.Endpoint, objB: endpoint)) {
            seat.Endpoint = endpoint;
            seat.Leads.Clear();
            seat.ConfirmedAt = ulong.MaxValue;
            seat.Error = null;
            seat.PathLive = false;
        }
        if (!ReferenceEquals(objA: sent.Endpoint, objB: endpoint) || !sent.Clock.IsAnchored) {
            return "no input is sent to the seat's authority yet";
        }

        var answered = mirror.TryInputAnswer(answer: out var answer, owner: owner);

        // Every owner incarnation numbers its input epochs and sequences afresh (a crossing rebuilds the seat's input,
        // even at the same authority), so its batches are timed only against answers given after it began: the leads
        // of the incarnation before are forgotten, and the answer standing when it began, which answers that one's
        // input, is taken as already seen.
        if (seat.Incarnation != sent.Incarnation) {
            seat.Incarnation = sent.Incarnation;
            seat.Leads.Clear();
            (seat.LeadSequence, seat.LeadEpoch) = (answered ? (answer.Sequence, answer.Epoch) : (0U, 0UL));
            seat.PathLive = false;
        }
        var answeredBatch = -1;

        if (answered && sent.TryFindSent(answeredAt: answer.Tick, epoch: answer.Epoch, sequence: answer.Sequence, position: out var answeredPosition)) {
            answeredBatch = answeredPosition;
        }
        if ((answeredBatch >= 0) && ((answer.Sequence != seat.LeadSequence) || (answer.Epoch != seat.LeadEpoch))) {
            seat.Leads.Add(value: ((long)answer.Tick - sent.At(position: answeredBatch).Tick));
            seat.LeadSequence = answer.Sequence;
            seat.LeadEpoch = answer.Epoch;
        }
        if (seat.Leads.Count == 0) {
            return "no answered input is timed yet";
        }

        var lead = seat.Leads.Minimum;
        var now = client.SeatClockTicks(rateHz: definition.SimulationRateHz);

        // A delivery that arrived since the last batch was sent (an authority catching up runs several ticks at once)
        // anchors the clock too, so the tick an input sent now lands at is never behind the continuation.
        sent.Clock.Observe(clientTick: now, deliveredTick: Math.Max(val1: mirror.Tick, val2: own.Tick));

        var steps = ((sent.Clock.TickAt(clientTick: now) + lead) - (long)own.Tick);

        if (steps < 0L) {
            return $"the continuation is {steps} tick(s) from the tick an input sent now lands at, {lead} tick(s) after it is sent";
        }
        var target = (own.Tick + ((ulong)steps));
        // The step's code (kits, motion programs, field programs) is content the client ships: the client's own copy of a document
        // the authority runs is whole, where the delivered one is what the authority disclosed.
        var source = (packageDocument(arg: definition.DocumentId) ?? definition);

        if (TryPopulation(definition: source, seat: seat) is { } refusal) {
            return refusal;
        }

        if (!TryDecode(own: in own, seat: seat)) {
            return "the continuation does not decode";
        }

        // The authority's answer to an earlier prediction: what was predicted for the continuation's tick against where the
        // body stood after it. A prediction is relied on only while the authority keeps confirming it.
        var reached = seat.Trail[(int)(own.Tick % ((ulong)seat.Trail.Length))];

        if ((reached.Tick == own.Tick) && (own.Tick != seat.ConfirmedAt)) {
            seat.ConfirmedAt = own.Tick;
            seat.Error = (reached.Position - seat.Entry.Body.Pose.Position).Length;
        }

        // The last batch the body has taken, by its position in the sent history: the authority takes a held batch again
        // on every tick no newer one arrives, its relative movement spent, so the prediction does too. The authority's
        // answer names the batch it last took and the tick it took it in, so a continuation at or after that tick holds
        // that batch and none sent after it that carries events: a batch a stalled link still holds is replayed from the
        // continuation (on the first step, where its sent tick and the lead are already past, as the burst that delivers
        // it is taken at once) rather than counted as taken because the least lead has passed. Without an answer for the
        // continuation's tick, the lead decides.
        var applied = -1;

        if ((answeredBatch >= 0) && (answer.Tick <= own.Tick)) {
            applied = answeredBatch;
        } else {
            for (var held = 0; (held < sent.Count); held++) {
                if ((sent.At(position: held).Tick + lead) > ((long)own.Tick)) {
                    break;
                }
                applied = held;
            }
        }

        var start = ((applied >= 0) ? (sent.FirstSerial + applied) : NoBatch);

        // Batches recorded since the continuation's held one may have pushed it out of the history: past that, which batch
        // the body holds is unknown, and the prediction stops rather than guess.
        if (!Holds(applied: start, sent: sent)) {
            return $"the batch the continuation at tick {own.Tick} holds has left the seat's last {WorldSeatSentInput.Capacity} sent batches";
        }

        var population = seat.Population!;
        // The doors the authority's own portal scan tests, from the one derivation both read (built once per document).
        var doors = WorldFaceCatalog.For(definition: source).PortalDoors(definition: source);
        var stepTicks = FixedTickConversion.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        var cause = Disagreement(lead: lead, own: in own, population: population, seat: seat, sent: sent, source: source, start: start);

        if (cause == PathCause.State) {
            StateCorrections++;
        } else if (cause == PathCause.Input) {
            InputCorrections++;
        }
        if ((cause != PathCause.Agrees) && (Restore(counted: true, own: in own, population: population, seat: seat, source: source, start: start) is { } restoreRefusal)) {
            return restoreRefusal;
        }

        var crossing = Extend(counted: true, doors: doors, lead: lead, own: in own, population: population, seat: seat, sent: sent, source: source, stepTicks: stepTicks, target: target);

        if (CheckReplays && (cause == PathCause.Agrees)) {
            crossing = CheckReplay(crossing: crossing, doors: doors, lead: lead, own: in own, population: population, seat: seat, sent: sent, source: source, start: start, stepTicks: stepTicks, target: target);
        }
        if (crossing is not null) {
            return crossing;
        }

        var tip = seat.Path[PathIndex(tick: target)];
        var rate = FixedQ4816.FromInteger(value: definition.SimulationRateHz);
        var velocity = ((steps > 0L) ? ((tip.Position - seat.Path[PathIndex(tick: (target - 1UL))].Position) * rate) : default);

        if (steps > 0L) {
            seat.Trail[(int)(target % ((ulong)seat.Trail.Length))] = (target, tip.Position);
        }

        Predictions++;
        body = new WorldPredictedBody(
            ContinuationTick: own.Tick,
            Endpoint: endpoint,
            Index: route.EntityIndex,
            Orientation: tip.Orientation,
            Position: tip.Position,
            Tick: target,
            Velocity: velocity,
            Error: seat.Error
        );
        return null;
    }
    // Whether a batch serial names a batch the history still holds, or names none while no batch has been dropped: the
    // next batch to take, and the one taken again while none newer arrives, are then both held.
    private static bool Holds(long applied, WorldSeatSentInput sent) => ((applied == NoBatch)
        ? (sent.FirstSerial == sent.ForgottenSerial)
        : (applied >= sent.FirstSerial));
    // The last batch the body has taken after a tick, from the last it had taken before: every next held batch whose sent
    // tick and the lead have reached the tick, in order. The one rule a step takes input by and a path is checked by.
    private static long Schedule(WorldSeatSentInput sent, long applied, long lead, ulong tick) {
        for (var next = ((applied == NoBatch) ? sent.ForgottenSerial : (applied + 1L)); ((next - sent.FirstSerial) < sent.Count); next++) {
            if ((sent.At(position: (int)(next - sent.FirstSerial)).Tick + lead) > ((long)tick)) {
                break;
            }
            applied = next;
        }

        return applied;
    }
    private static int PathIndex(ulong tick) => ((int)(tick % PathTicks));
    // Decides whether the seat's path stands for the latest continuation: it covers the continuation's tick, its body
    // there digests as the continuation does at the same engine tick, and on every tick from there to its tip it took the
    // batch a replay from the continuation takes now. The one door every prediction passes before stepping on.
    private static PathCause Disagreement(Seat seat, in WorldOwnBody own, long start, long lead, WorldSeatSentInput sent, WorldDefinition source, WorldPopulation population) {
        if (!seat.PathLive || !ReferenceEquals(objA: seat.PathSource, objB: source) ||
            (own.Tick < seat.BaseTick) || (own.Tick > seat.TipTick) || ((seat.TipTick - own.Tick) >= PathTicks) ||
            !Holds(applied: seat.Path[PathIndex(tick: seat.TipTick)].Applied, sent: sent)) {
            return PathCause.Restart;
        }

        var at = seat.Path[PathIndex(tick: own.Tick)];

        if (at.Tick != own.Tick) {
            return PathCause.Restart;
        }
        seat.EntryDigest ??= population.PredictedBodyDigest(attached: seat.Attached, captured: in seat.Entry);
        if ((at.EngineTick != own.EngineTick) || (at.Digest != seat.EntryDigest)) {
            return PathCause.State;
        }
        if (at.Applied != start) {
            return PathCause.Input;
        }

        var applied = start;

        for (var tick = (own.Tick + 1UL); (tick <= seat.TipTick); tick++) {
            applied = Schedule(applied: applied, lead: lead, sent: sent, tick: tick);
            if (seat.Path[PathIndex(tick: tick)].Applied != applied) {
                return PathCause.Input;
            }
        }

        return PathCause.Agrees;
    }
    // Starts the seat's path afresh from the continuation, answering null, or why it does not restore. The path's first
    // tick holds the continuation's own digest, which is the restored body's by construction; a restore that digests
    // otherwise is counted.
    private string? Restore(Seat seat, in WorldOwnBody own, long start, WorldDefinition source, WorldPopulation population, bool counted) {
        WorldBody predicted;

        try {
            predicted = population.RestorePredicted(attached: seat.Attached, captured: in seat.Entry, defaults: source.PlayerDefaults, slot: 0);
        } catch (InvalidOperationException failure) {
            seat.PathLive = false;
            return $"the continuation does not restore: {failure.Message}";
        }

        var digest = (seat.EntryDigest ??= population.PredictedBodyDigest(attached: seat.Attached, captured: in seat.Entry));

        if (counted) {
            Restores++;
            if (population.PredictedBodyDigest(slot: 0) != digest) {
                InexactRestores++;
            }
        }
        seat.Path[PathIndex(tick: own.Tick)] = new PathTick(Applied: start, Digest: digest, EngineTick: own.EngineTick, Orientation: predicted.FixedOrientation, Position: predicted.FixedPosition, Tick: own.Tick);
        seat.PathLive = true;
        seat.PathSource = source;
        seat.PathBody = predicted;
        seat.BaseTick = own.Tick;
        seat.TipTick = own.Tick;
        seat.CrossedAt = null;
        seat.CrossedReason = null;
        return null;
    }
    // Steps the seat's path from its tip to the target tick, each tick taking its batches by the schedule, answering null,
    // or the crossing that stops it at or before the target. A crossing at or before the continuation's tick is one a
    // replay from the continuation never steps through, so the path goes on past it.
    private string? Extend(Seat seat, in WorldOwnBody own, WorldSeatSentInput sent, long lead, ulong target, WorldDefinition source, WorldPopulation population, IReadOnlyList<WorldPortalDoor> doors, ulong stepTicks, bool counted) {
        if ((seat.CrossedAt is { } before) && (before <= own.Tick)) {
            seat.CrossedAt = null;
            seat.CrossedReason = null;
        }

        var predicted = seat.PathBody!;
        var tip = seat.Path[PathIndex(tick: seat.TipTick)];
        var applied = tip.Applied;
        var position = tip.Position;
        var engineTick = tip.EngineTick;

        for (var tick = (seat.TipTick + 1UL); ((tick <= target) && (seat.CrossedAt is null)); tick++) {
            var taken = Schedule(applied: applied, lead: lead, sent: sent, tick: tick);

            if (taken != applied) {
                for (var serial = ((applied == NoBatch) ? sent.ForgottenSerial : (applied + 1L)); (serial <= taken); serial++) {
                    TakeInput(body: predicted, held: false, population: population, submission: sent.At(position: (int)(serial - sent.FirstSerial)).Submission, tick: tick);
                }
            } else if (applied != NoBatch) {
                TakeInput(body: predicted, held: true, population: population, submission: sent.At(position: (int)(applied - sent.FirstSerial)).Submission, tick: tick);
            }
            applied = taken;

            m_edges.AsSpan().Clear();
            population.AdvanceSeats(engageEdges: m_edges, engageProbeOrdinals: [], stepStartEngineTick: engineTick, stepTicks: stepTicks, tick: tick);
            population.CompleteStep(tick: tick);
            engineTick += stepTicks;
            if (counted) {
                StepsTaken++;
            }

            // A step through a door's glass is a crossing only the authority makes (it carries the body to the door's
            // counterpart, perhaps resized or into another world), so a prediction past one would steer the seat from a
            // body standing in the wall behind the glass: the seat steers from what was delivered until the crossing is.
            for (var index = 0; (index < doors.Count); index++) {
                var door = doors[index];

                if (WorldFaceRegion.Sweep(aperture: door.Aperture, from: position, to: predicted.FixedPosition).Crossed) {
                    seat.CrossedAt = tick;
                    seat.CrossedReason = $"the prediction reaches {source.Placements[door.Row].Id}/{door.Face.Face}, whose crossing the authority makes";
                    break;
                }
            }
            position = predicted.FixedPosition;
            seat.Path[PathIndex(tick: tick)] = new PathTick(Applied: applied, Digest: population.PredictedBodyDigest(slot: 0), EngineTick: engineTick, Orientation: predicted.FixedOrientation, Position: position, Tick: tick);
            seat.TipTick = tick;
        }

        return (((seat.CrossedAt is { } crossed) && (crossed <= target)) ? seat.CrossedReason : null);
    }
    // Replays the prediction just made from its continuation, uncounted, and compares the replay's tip with the path's:
    // the same crossing, or the same body digest and pose at the target. The replay becomes the path. Answers the
    // replay's crossing, or null.
    private string? CheckReplay(string? crossing, Seat seat, in WorldOwnBody own, long start, WorldSeatSentInput sent, long lead, ulong target, WorldDefinition source, WorldPopulation population, IReadOnlyList<WorldPortalDoor> doors, ulong stepTicks) {
        var path = seat.Path[PathIndex(tick: target)];

        if (Restore(counted: false, own: in own, population: population, seat: seat, source: source, start: start) is { } refusal) {
            ReplayChecks++;
            ReplayMismatches++;
            return refusal;
        }

        var replayed = Extend(counted: false, doors: doors, lead: lead, own: in own, population: population, seat: seat, sent: sent, source: source, stepTicks: stepTicks, target: target);
        var tip = seat.Path[PathIndex(tick: target)];

        ReplayChecks++;
        if (!string.Equals(a: crossing, b: replayed, comparisonType: StringComparison.Ordinal) ||
            ((replayed is null) && ((tip.Digest != path.Digest) || (tip.Applied != path.Applied) || (tip.Position != path.Position) || (tip.Orientation != path.Orientation)))) {
            ReplayMismatches++;
        }

        return replayed;
    }
    // Takes one sent batch as the authority takes an admitted batch of the body's owner: folded onto the body's channels
    // and written through the body's one owner-input door. A held batch, taken again on a tick no newer one arrived,
    // carries no relative movement.
    private static void TakeInput(WorldBody body, WorldPopulation population, IntentSubmission submission, ulong tick, bool held) {
        var mappings = population.InputCatalog.Mappings;

        if (submission.Input.Mapping >= mappings.Length) {
            return;
        }

        var admitted = submission with { Tick = tick, Input = submission.Input with { Tick = tick } };
        var applied = WorldAcceptedInput.Fold(mapping: mappings[submission.Input.Mapping].Channels, submission: in admitted, target: body.Channels);

        _ = body.TakeOwnerInput(applied: (held ? applied.WithoutRelative(target: body.Channels) : applied));
    }
    // The private population of the delivered document: built once for each document whose contact, kits or motion
    // differ, never for a delivery that moved only state.
    // A document that builds none is not tried again until another is delivered. Answers null, or why none is built.
    private string? TryPopulation(WorldDefinition definition, Seat seat) {
        if (ReferenceEquals(objA: seat.Definition, objB: definition)) {
            return ((seat.Population is not null) ? null : seat.PopulationRefusal);
        }

        if ((seat.Population is { } built) && (seat.Definition is { } before) &&
            WorldDocumentInputs.Changed(after: definition, before: before).Intersect(other: built.BuiltFrom).IsEmpty) {
            seat.Definition = definition;
            return null;
        }

        seat.PathLive = false;
        try {
            var population = new WorldPopulation(definition: definition, fields: false);

            population.BindActionStateLane(
                arena: new StateArena(catalog: definition.StateCatalog, section: definition.StateRaw, time: ArenaTime.Origin),
                definition: definition
            );
            seat.Population = population;
            seat.Definition = definition;
            seat.EntryBytes = default;
            seat.Decoded = false;
            PopulationsBuilt++;
            return null;
        } catch (Exception failure) {
            seat.Population = null;
            seat.Definition = definition;
            seat.PopulationRefusal = $"the delivered document builds no population: {failure.GetType().Name}: {failure.Message}";
            return seat.PopulationRefusal;
        }
    }
    private static bool TryDecode(in WorldOwnBody own, Seat seat) {
        if (seat.Decoded && own.Entry.Equals(other: seat.EntryBytes)) {
            return true;
        }

        seat.Decoded = WorldAuthorityCheckpointCodec.TryDecodeOwnBody(attached: out seat.Attached, bytes: own.Entry.Span, entry: out seat.Entry, reason: out _);
        seat.EntryBytes = own.Entry;
        seat.EntryDigest = null;
        return seat.Decoded;
    }
}
