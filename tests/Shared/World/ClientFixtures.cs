using Puck.Commands;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Testing;

/// <summary>The suite's one construction of a presentation client that runs no server: its roster sits on
/// <see cref="SilentLink"/>, which answers only the construction-time channel query.</summary>
internal static class ClientFixtures {
    /// <summary>Builds a client over <paramref name="definition"/> with a fresh composition state and seat router.</summary>
    /// <param name="definition">The document the client presents.</param>
    /// <returns>The client.</returns>
    public static WorldClient Client(WorldDefinition definition) => new(
        composition: new WorldCompositionState(),
        definition: definition,
        roster: new PlayerRoster(
            definition: definition,
            link: new SilentLink(),
            seatBindings: new WorldSeatBindings(definition: definition)
        ),
        seatRouter: new WorldSeatAuthorityRouter()
    );
    /// <summary>Builds a state mirror over <paramref name="definition"/>, installed at a tick, the way a client's mirror
    /// stands after a delivery.</summary>
    /// <param name="definition">The document the mirror reads.</param>
    /// <param name="tick">The tick the values hold as of.</param>
    /// <param name="engineTick">The engine tick the values hold as of.</param>
    /// <returns>The installed mirror.</returns>
    public static WorldStateMirror StateMirror(WorldDefinition definition, ulong tick = 0UL, ulong engineTick = 0UL) {
        definition = WorldStateReader.Export(definition);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: engineTick,
            tick: tick
        );

        return mirror;
    }
    /// <summary>Builds one body's reads of a state mirror over <paramref name="definition"/> installed at a tick.</summary>
    /// <param name="definition">The document the mirror reads.</param>
    /// <param name="tick">The tick the values hold as of.</param>
    /// <param name="bodyIndex">The body a <c>$body</c> key names, or -1 for none.</param>
    /// <param name="engineTick">The engine tick the values hold as of.</param>
    /// <returns>The bound lease.</returns>
    public static WorldStateLease StateReads(WorldDefinition definition, ulong tick = 0UL, int bodyIndex = -1, ulong engineTick = 0UL) {
        var reads = new WorldStateLease();

        reads.Bind(
            bodyIndex: bodyIndex,
            mirror: StateMirror(
                definition: definition,
                engineTick: engineTick,
                tick: tick
            )
        );

        return reads;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<CreationDriverDocument>, CreationRig> DriverRigs = new();
    /// <summary>Returns the rig of a creation that declares only these drivers, the same rig for the same list.</summary>
    /// <param name="drivers">The drivers, or <see langword="null"/> for none.</param>
    /// <returns>The rig.</returns>
    public static CreationRig DriverRig(IReadOnlyList<CreationDriverDocument>? drivers) => DriverRigs.GetValue(
        key: (drivers ?? []),
        createValueCallback: static rows => CreationRig.Compile(document: new CreationDocument(Drivers: rows, Frames: null, Name: null, Palette: null, Schema: CreationDocument.CurrentSchema, Shapes: null))
    );
    /// <summary>Returns a view of <see cref="DriverRig"/>'s reads through a lease.</summary>
    /// <param name="drivers">The drivers.</param>
    /// <param name="lease">The lease state reads go through.</param>
    /// <returns>The view.</returns>
    public static WorldRigReads DriverReads(IReadOnlyList<CreationDriverDocument>? drivers, WorldStateLease lease) => new(lease: lease, rig: DriverRig(drivers: drivers));
    /// <summary>Returns whether an authored gate holds, compiled as a driver's gate.</summary>
    /// <param name="gate">The gate tokens.</param>
    /// <param name="facts">The body's facts.</param>
    /// <param name="moving">Whether the body is moving.</param>
    /// <param name="reads">The lease state tokens read through, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every token holds.</returns>
    public static bool HoldsGate(IReadOnlyList<string>? gate, Puck.Physics.Motion.BodyFacts facts, bool moving, WorldStateLease? reads = null) {
        var rig = DriverRig(drivers: [new CreationDriverDocument(Cadence: 0f, Name: "gate", Signal: CreationDriverDocument.SignalTime, When: gate)]);

        return WorldGaitDrivers.HoldsGate(facts: facts, gate: rig.Drivers[0].Gate, moving: moving, reads: ((reads is null) ? null : new WorldRigReads(lease: reads, rig: rig)));
    }
    /// <summary>Returns the terms one shape composes under a creation's drivers.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="drivers">The creation's drivers.</param>
    /// <returns>The shape's terms.</returns>
    public static CreationRigTerm[] Terms(ShapeDocument shape, IReadOnlyList<CreationDriverDocument>? drivers) =>
        CreationRig.Compile(document: new CreationDocument(Drivers: drivers, Frames: null, Name: null, Palette: null, Schema: CreationDocument.CurrentSchema, Shapes: [shape])).Terms(shape: 0).ToArray();
}
/// <summary>The narrowest link a <see cref="PlayerRoster"/> can be built over: it answers nothing and drops
/// everything.</summary>
internal sealed class SilentLink : IServerLink {
    /// <inheritdoc/>
    public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) {
    }
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => 0L;
    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) {
    }
    /// <inheritdoc/>
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
    }
}
