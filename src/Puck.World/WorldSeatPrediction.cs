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
/// lands at. A delivery that disagrees with the last prediction replaces it, since every prediction starts again from
/// the latest continuation.
/// </summary>
/// <remarks>
/// <para>The invariant: a prediction runs the one body step (<see cref="WorldPopulation.AdvanceSeats"/> over
/// <see cref="WorldBody"/>) on the one input fold (<see cref="WorldAcceptedInput.Fold"/>, taken through the body's one
/// owner-input write) every authority runs, so equal continuations and equal inputs give equal bodies, and a
/// correction is a real divergence, never rounding. What enforces it: the client project cannot name the step, and this
/// type, in the composition root, holds no motion code of its own.</para>
/// <para>Where it stops: the step also reads what lies outside the body. The static ground, gravity, kits and motion
/// programs come from the delivered document and the moving solids' poses from the continuation, so they are exact at
/// its tick; other bodies, a moving solid's motion after that tick and rule writes to the body's registers are not
/// predicted, and show as a correction at the next delivery. A seat riding another body is not predicted.</para>
/// <para>Nothing a prediction does reaches an authority or the delivered mirror: its population and arena are its own.
/// Every member runs on the host pump.</para>
/// </remarks>
/// <param name="client">The presentation client whose seats are predicted.</param>
/// <param name="packageDocument">Returns the client's own whole copy of the document a documentId names, from the package it
/// shipped with, or <see langword="null"/> when it holds none; a prediction then builds from the document delivered.</param>
public sealed class WorldSeatPrediction(WorldClient client, Func<string?, WorldDefinition?> packageDocument) : IWorldSeatPrediction {
    /// <summary>The most authority ticks a prediction steps past its continuation: a round trip longer than this is
    /// not predicted, and the seat steers from what was delivered.</summary>
    public const int MaxSteps = 90;

    private sealed class Seat {
        public WorldDefinition? Definition;
        public WorldPopulation? Population;
        public ReadOnlyMemory<byte> EntryBytes;
        public WorldPopulationEntryCheckpoint Entry;
        public WorldAttachedSolidCheckpoint[] Attached = [];
        public bool Decoded;
        // The authority tick an input sent at seat clock tick zero lands at: an answered batch's answer tick less the seat
        // clock tick it was sent at, the latest one answered.
        public WorldAuthorityEndpoint? Endpoint;
        // The tip of each prediction by the authority tick it reached, and the distance by which the authority last missed one.
        public readonly (ulong Tick, FixedVector3 Position)[] Trail = new (ulong Tick, FixedVector3 Position)[64];
        public ulong ConfirmedAt = ulong.MaxValue;
        public FixedQ4816 Error = FixedQ4816.MaxValue;
        public long Offset;
        public readonly long[] Samples = new long[16];
        public int SampleNext;
        public int SampleCount;
        public bool HasOffset;
        public uint OffsetSequence;
        public ulong OffsetEpoch;
        public double PredictedAt = double.NaN;
        public bool Predicted;
        public string Reason = "no prediction asked";
        public string? PopulationRefusal;
        public WorldPredictedBody Body;
    }

    private readonly Seat[] m_seats = [.. Enumerable.Range(start: 0, count: PlayerRoster.MaxSlots).Select(selector: static _ => new Seat())];
    private readonly bool[] m_edges = new bool[WorldBodiesLimits.LocalSeatCount];

    /// <summary>Gets the predictions made since boot.</summary>
    public long Predictions { get; private set; }
    /// <summary>Gets the authority ticks stepped by every prediction since boot.</summary>
    public long StepsTaken { get; private set; }
    /// <summary>Gets the private populations built since boot, one for each delivered document a seat predicted in.</summary>
    public long PopulationsBuilt { get; private set; }

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

        return (seat.Predicted
            ? string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"predicted {seat.Body.Steps} tick(s) past tick {seat.Body.ContinuationTick}, last missed by {(double)seat.Error:0.###} m; offset {seat.Offset}; {Predictions} prediction(s), {StepsTaken} step(s), {PopulationsBuilt} population(s)")
            : $"off({seat.Reason})");
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
            seat.SampleCount = 0;
            seat.ConfirmedAt = ulong.MaxValue;
            seat.Error = FixedQ4816.MaxValue;
            seat.HasOffset = false;
        }
        if (!ReferenceEquals(objA: sent.Endpoint, objB: endpoint)) {
            return "no input is sent to the seat's authority yet";
        }

        if (mirror.TryInputAnswer(answer: out var answer, owner: owner) && ((answer.Sequence != seat.OffsetSequence) || (answer.Epoch != seat.OffsetEpoch)) &&
            sent.TryFindSent(clientTick: out var sentAt, epoch: answer.Epoch, sequence: answer.Sequence)) {
            // The least of the recent samples: a batch held up in a burst lands later than the link's own delay, and
            // predicting that far ahead would carry the body past where the authority takes the next input.
            seat.Samples[seat.SampleNext] = ((long)answer.Tick - (long)sentAt);
            seat.SampleNext = ((seat.SampleNext + 1) % seat.Samples.Length);
            seat.SampleCount = Math.Min(val1: (seat.SampleCount + 1), val2: seat.Samples.Length);
            seat.Offset = long.MaxValue;
            foreach (var sample in seat.Samples.AsSpan(start: 0, length: seat.SampleCount)) {
                seat.Offset = Math.Min(val1: seat.Offset, val2: sample);
            }
            seat.OffsetSequence = answer.Sequence;
            seat.OffsetEpoch = answer.Epoch;
            seat.HasOffset = true;
        }
        if (!seat.HasOffset) {
            return "no answered input is timed yet";
        }

        var target = ((long)client.SeatClockTicks(rateHz: definition.SimulationRateHz) + seat.Offset);
        var steps = (target - (long)own.Tick);

        if ((steps < 0L) || (steps > MaxSteps)) {
            return $"the continuation is {steps} tick(s) from the tick an input sent now lands at";
        }
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

        var population = seat.Population!;
        WorldBody predicted;

        try {
            predicted = population.RestorePredicted(attached: seat.Attached, captured: in seat.Entry, defaults: source.PlayerDefaults, slot: 0);
        } catch (InvalidOperationException failure) {
            return $"the continuation does not restore: {failure.Message}";
        }

        var stepTicks = FixedTickConversion.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        var engineTick = own.EngineTick;
        var previous = predicted.FixedPosition;
        // The last batch the body has taken, by its position in the sent history: the authority takes a held batch again
        // on every tick no newer one arrives, its relative movement spent, so the prediction does too.
        var applied = -1;

        for (var position = 0; (position < sent.Count); position++) {
            if (((long)sent.At(position: position).ClientTick) > (((long)own.Tick) - seat.Offset)) {
                break;
            }
            applied = position;
        }
        for (var step = 1L; (step <= steps); step++) {
            var tick = (own.Tick + ((ulong)step));
            var sentBy = (((long)tick) - seat.Offset);
            var fresh = false;

            while (((applied + 1) < sent.Count) && (((long)sent.At(position: (applied + 1)).ClientTick) <= sentBy)) {
                applied++;
                fresh = true;
                TakeInput(body: predicted, held: false, population: population, submission: sent.At(position: applied).Submission, tick: tick);
            }
            if (!fresh && (applied >= 0)) {
                TakeInput(body: predicted, held: true, population: population, submission: sent.At(position: applied).Submission, tick: tick);
            }

            previous = predicted.FixedPosition;
            m_edges.AsSpan().Clear();
            population.AdvanceSeats(engageEdges: m_edges, engageProbeOrdinals: [], stepStartEngineTick: engineTick, stepTicks: stepTicks, tick: tick);
            population.CompleteStep(tick: tick);
            engineTick += stepTicks;
        }
        if (steps > 0L) {
            var tip = (own.Tick + ((ulong)steps));

            seat.Trail[(int)(tip % ((ulong)seat.Trail.Length))] = (tip, predicted.FixedPosition);
        }

        var rate = FixedQ4816.FromInteger(value: definition.SimulationRateHz);
        var velocity = ((steps > 0L) ? ((predicted.FixedPosition - previous) * rate) : default);

        Predictions++;
        StepsTaken += steps;
        body = new WorldPredictedBody(
            ContinuationTick: own.Tick,
            Endpoint: endpoint,
            Index: route.EntityIndex,
            Orientation: predicted.FixedOrientation,
            Position: predicted.FixedPosition,
            Tick: (own.Tick + ((ulong)steps)),
            Velocity: velocity,
            Error: seat.Error
        );
        return null;
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
        return seat.Decoded;
    }
}
