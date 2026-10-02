using System.Globalization;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>timeline</c> read-back: <c>world.timeline</c> reports every named clock with its source (the tick, with its
/// period and start in exact engine ticks, a state row, or another clock), its span, and the resolved phase and
/// reading of tick and phase clocks at the authority's completed engine tick. The section is authored through
/// <c>world.row.set timeline</c>.
/// </summary>
public sealed class WorldTimelineCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private static string Number(double value) => value.ToString(
        format: "0.######",
        provider: CultureInfo.InvariantCulture
    );

    /// <summary>Returns the <c>world.timeline</c> echo of a definition's clocks at an engine tick.</summary>
    /// <param name="definition">The definition whose <c>timeline</c> section is echoed.</param>
    /// <param name="engineTick">The completed engine tick a tick clock's phase is read at.</param>
    /// <returns>The echo line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static string Describe(WorldDefinition definition, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var clocks = (definition.Timeline.Clocks ?? []);
        var values = new WorldValueResolver(definition, new PresentedTick(Fraction: 0d, Whole: engineTick));
        var echo = CommandEcho.Open(verb: "world.timeline")
            .Field(
            key: "clocks",
            value: clocks.Count
        )
            .Field(
            key: "engineTick",
            value: engineTick.ToString(provider: CultureInfo.InvariantCulture)
        );

        foreach (var clock in clocks) {
            echo = echo
                .Segment()
                .Head(head: clock.Name)
                .Field(
                key: "span",
                value: Number(value: clock.Span)
            );

            if (clock.State is { } state) {
                echo = echo.Field(
                    key: "state",
                    value: state
                );

                continue;
            }

            if (clock.Phase?.Keys is { } keys) {
                echo = echo.Field(key: "clock", value: keys.Clock);
            } else {
                echo = echo
                    .Field(
                    key: "periodTicks",
                    value: WorldClocks.PeriodTicks(clock: clock).ToString(provider: CultureInfo.InvariantCulture)
                )
                    .Field(
                    key: "startTicks",
                    value: WorldClocks.StartTicks(clock: clock).ToString(provider: CultureInfo.InvariantCulture)
                );
            }

            var phase = values.Phase(clock: clock);

            echo = echo
                .Field(
                key: "phase",
                value: Number(value: phase)
            )
                .Field(
                key: "reading",
                value: Number(value: (phase * clock.Span))
            );
        }

        return echo.Close();
    }
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return authority.CreateServerQueryCommand(
            description: "Reports the timeline section's clocks (Immediate; the stdin barrier makes it read the settled state after any pending mutation): each clock's span and source, a tick clock's period and start in engine ticks, a phase clock's parent, and the resolved phase and reading of tick and phase clocks at the authority's completed engine tick, or a state clock's row.",
            describe: server => Describe(
                definition: server.Definition,
                engineTick: server.CompletedEngineTicks
            ),
            name: "world.timeline"
        );
    }
}
