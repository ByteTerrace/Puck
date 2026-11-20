using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The music read-backs, composed in every boot shape since none needs an audio device: <c>music.state</c> (the score
/// this listener hears, from <see cref="WorldAudioDirector"/>, beside the authority's music clock) and
/// <c>instrument.state</c> (the diegetic instrument the routed seat drives the clock with). The authority halves are
/// routed through seat 1's currently claimed <see cref="WorldAuthorityEndpoint.Submissions"/> — never the boot
/// instance's own injected link directly — so they track a transferred seat the same way
/// <see cref="PlayerCommandModule"/>'s drive-a-player verbs do.
/// </summary>
internal sealed class WorldMusicCommandModule(WorldAudioDirector director, WorldSeatAuthorityRouter seatRouter) : ICommandModule {
    // Answers a world-scoped music query off seat 1's CURRENTLY CLAIMED authority (WorldSeatAuthorityRouter's own
    // one-writer table). Seat 1's route is published for every boot, so this never throws for want of a claim. The
    // query carries the routed endpoint's OWN 1-based entity index (never the local roster display number) — its
    // Observe grant check narrows to that body, the one subject a routed seat is always seeded with, so a transferred
    // seat never needs a standing world-wide grant just to read the clock.
    private CommandResult RoutedQuery(Func<int, WorldQuery> query, Principal caller) => (seatRouter.TryRouteQuery(
        caller: caller,
        factory: query,
        result: out var result,
        slot: 0,
        tagInstance: false
    )
        ? result
        : CommandResult.Error(output: "[query: seat 1 has no authority claim]")
    );
    private CommandResult MusicState(CommandContext context) {
        var clock = RoutedQuery(caller: context.Principal, query: static index => new WorldQuery.MusicClock(Index: index));
        var text = $"{director.DescribeMusicState()} {clock.Output}";

        return (clock.IsError ? CommandResult.Error(output: text) : new CommandResult(Output: text));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "music.state",
            description: "Reads the score this listener hears, stepped from what its seat is delivered — the current segment, any pending transition, transition count, the tick/from/to of the most recent committed transition (none= before the first one), each sounding layer as <tuneId>@<tick it joined on>, and the patch/tick of the most recent embellishment (none delivered before a score arrives) — then, from seat 1's currently claimed authority, the music clock every score steps on: elapsed clock ticks and the live $clock phaseError, beat and bar (none declared when the world has no music). A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: MusicState,
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.Verb(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "instrument.state",
            description: "Reads which diegetic instrument screen (if any) the routed seat holds a screen application to, authoritatively off seat 1's currently claimed authority — screen index, whether the booted machine there carries the instrument-clock capability, its authored tempo in engine ticks per beat, and whether it is driving the world's music clock (holding the application is the whole gate — see world.instrument-clock). Follows a transferred seat the same way music.state does. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: context => RoutedQuery(caller: context.Principal, query: static index => new WorldQuery.InstrumentState(Index: index)),
            routing: CommandRouting.Immediate
        );
    }
}
