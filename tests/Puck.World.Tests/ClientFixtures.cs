using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Tests;

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
            link: new SilentLink(definition: definition),
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
}
/// <summary>The narrowest link a <see cref="PlayerRoster"/> can be built over: it answers the one construction-time
/// channel query from <paramref name="definition"/> and drops everything else.</summary>
/// <param name="definition">The document whose channels the query answers with.</param>
internal sealed class SilentLink(WorldDefinition definition) : IServerLink {
    /// <inheritdoc/>
    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        if (query is WorldQuery.PopulationChannels) {
            completion(obj: new QueryAnswer(
                Payload: WorldChannelTable.Compile(channels: definition.Channels),
                Text: string.Empty
            ));
        }
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
