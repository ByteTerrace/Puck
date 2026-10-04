using System.Globalization;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>timeline</c> read-back and session preview controls: <c>world.timeline</c> reports every named clock with its source (the tick, with its
/// period and start in exact engine ticks, or a state row), its span, and, for a tick clock, its phase and reading at
/// the authority's completed engine tick, then how many keyed values the presentation has resolved
/// (<see cref="WorldStateMirror.KeyedResolutions"/>), which rises only while a clock a key reads moves. The section is
/// authored through <c>world.row.set timeline</c>; hold, run, at and rate change only the presentation mirror.
/// </summary>
/// <param name="authority">The console authority whose world's clocks are reported.</param>
/// <param name="presentation">Resolves the addressed authority's presentation mirror, or returns
/// <see langword="null"/> when that authority has no local presentation.</param>
public sealed class WorldTimelineCommandModule(IWorldConsoleAuthority authority, Func<WorldServer, WorldStateMirror?>? presentation = null) : ICommandModule {
    private static string Number(double value) => value.ToString(
        format: "0.######",
        provider: CultureInfo.InvariantCulture
    );

    /// <summary>Returns the <c>world.timeline</c> echo of a definition's clocks at an engine tick.</summary>
    /// <param name="definition">The definition whose <c>timeline</c> section is echoed.</param>
    /// <param name="engineTick">The completed engine tick a tick clock's phase is read at.</param>
    /// <param name="keyedResolutions">The keyed values the presentation has resolved, or <see langword="null"/> when
    /// nothing presents.</param>
    /// <param name="mirror">The addressed world's presentation mirror, including session clock previews.</param>
    /// <returns>The echo line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static string Describe(WorldDefinition definition, ulong engineTick, long? keyedResolutions = null, WorldStateMirror? mirror = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var clocks = (definition.Timeline.Clocks ?? []);
        var echo = CommandEcho.Open(verb: "world.timeline")
            .Field(
            key: "clocks",
            value: clocks.Count
        )
            .Field(
            key: "engineTick",
            value: engineTick.ToString(provider: CultureInfo.InvariantCulture)
        )
            .Field(
            key: "keyedResolutions",
            value: ((keyedResolutions is { } count)
                ? count.ToString(provider: CultureInfo.InvariantCulture)
                : "none")
        );

        foreach (var clock in clocks) {
            echo = echo
                .Segment()
                .Head(head: clock.Name)
                .Field(
                key: "span",
                value: Number(value: clock.Span)
            );

            if (mirror is not null) {
                var held = mirror.ClockHeld(name: clock.Name, rate: out var rate);
                var tick = mirror.ClockTick(name: clock.Name);

                echo = echo.Field(key: "held", value: held).Field(key: "rate", value: Number(value: rate))
                    .Field(key: "presentedTick", value: $"{tick.Whole}+{Number(value: tick.Fraction)}");
                if (mirror.TryReadPhase(clock.Name, out _, out var presentedPhase)) {
                    echo = echo.Field(key: "presentedPhase", value: Number(value: presentedPhase)).Field(key: "presentedReading", value: Number(value: (presentedPhase * clock.Span)));
                }
            }

            if (clock.State is { } state) {
                echo = echo.Field(
                    key: "state",
                    value: state
                );

                continue;
            }

            var phase = WorldClocks.Phase(
                clock: clock,
                tick: new PresentedTick(
                    Fraction: 0d,
                    Whole: engineTick
                )
            );

            echo = echo
                .Field(
                key: "periodTicks",
                value: WorldClocks.PeriodTicks(clock: clock).ToString(provider: CultureInfo.InvariantCulture)
            )
                .Field(
                key: "startTicks",
                value: WorldClocks.StartTicks(clock: clock).ToString(provider: CultureInfo.InvariantCulture)
            )
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
        yield return CommandDefinition.WithWireArgs(
            name: "world.timeline",
            routing: CommandRouting.Immediate,
            bindability: CommandBindability.Unbindable,
            description: "world.timeline [hold|run <clock> | at <clock> <engine-tick> | rate <clock> <rate>] — reports authored clocks and their presentation readings. Hold and at freeze only the named presentation clock; run continues from it; rate is finite and nonnegative. Previews never change simulation state or save to source.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(context: context, error: out var error, server: out var server, verb: "world.timeline")) { return error; }
                var mirror = presentation?.Invoke(server);

                if (args.Count > 0) {
                    if (mirror is null) { return CommandResult.Error(output: "[world.timeline: no presentation of this authority]"); }
                    WorldTimelineOperation operation;
                    var tick = 0UL;
                    var rate = 1d;

                    if ((args.Count == 2) && args.Is(index: 0, value: "hold")) { operation = WorldTimelineOperation.Hold; } else if ((args.Count == 2) && args.Is(index: 0, value: "run")) { operation = WorldTimelineOperation.Run; } else if ((args.Count == 3) && args.Is(index: 0, value: "at") && ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out tick)) { operation = WorldTimelineOperation.At; } else if ((args.Count == 3) && args.Is(index: 0, value: "rate") && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && double.IsFinite(d: rate) && (rate >= 0d)) { operation = WorldTimelineOperation.Rate; } else { return CommandResult.Usage(form: "hold|run <clock> | at <clock> <engine-tick> | rate <clock> <rate>", verb: "world.timeline"); }
                    if (!mirror.ControlClock(args[1].ToString(), operation, tick, rate, out var refusal)) { return CommandResult.Error(output: $"[world.timeline: {refusal}]"); }
                }
                return new CommandResult(Describe(definition: server.Definition, engineTick: server.CompletedEngineTicks, keyedResolutions: mirror?.KeyedResolutionCount, mirror: mirror));
            });
    }
}
