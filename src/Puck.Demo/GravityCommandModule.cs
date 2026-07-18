using System.CommandLine;
using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.Gravity;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>The host seam <see cref="GravityCommandModule"/> drives — mirrors <see cref="RtsCommandModule"/>'s
/// <c>IRtsHost</c> pattern exactly: the module reaches the live walker through the root node, never by holding its
/// own reference to <see cref="OverworldFrameSource"/>.</summary>
public interface IGravityHost {
    /// <summary>Spawns/resets the walker at a start longitude and activates the fullscreen planetoid takeover.</summary>
    /// <param name="longitudeDegrees">The starting equatorial longitude, degrees.</param>
    void SpawnWalker(double longitudeDegrees);

    /// <summary>Queues ticks of scripted walk intent.</summary>
    /// <param name="ticks">How many ticks to apply <paramref name="move"/> for.</param>
    /// <param name="move">The tangent-plane move vector (X = strafe, Y = forward).</param>
    void WalkWalker(int ticks, Vector2 move);

    /// <summary>The walker's current state, or an inactive default when the root is not ready/nothing is spawned.</summary>
    OverworldWorld.FieldWalkerSnapshot WalkerState();
}

/// <summary>
/// Console verbs for the gravity scenario: <c>planet.spawn</c> seats the walker
/// on the planetoid and opens the fullscreen takeover, <c>planet.walk</c> drives scripted forward motion so a piped
/// script can circumnavigate without a pad, and <c>planet.list</c> echoes the walker's pose — position/up/facing/
/// grounded/longitude — the scripted assertion surface (the antipode's up ≈ -(start up) demonstrates that
/// gravity is the field gradient, walked all the way around).
/// </summary>
internal sealed class GravityCommandModule(IRenderNode rootNode) : ICommandModule {
    // The render node is at its analyzer coupling ceiling and cannot implement IGravityHost itself — every authoring
    // surface reaches its composition point through ICreatorModeHost.CreatorFrameSource instead, adapted here
    // (mirrors RtsCommandModule.FrameSourceHost).
    private sealed class FrameSourceHost(Overworld.ICreatorModeHost creatorHost) : IGravityHost {
        /// <inheritdoc/>
        public void SpawnWalker(double longitudeDegrees) =>
            creatorHost.CreatorFrameSource?.SpawnGravityWalker(longitudeDegrees: longitudeDegrees);

        /// <inheritdoc/>
        public void WalkWalker(int ticks, Vector2 move) =>
            creatorHost.CreatorFrameSource?.WalkGravityWalker(ticks: ticks, move: move);

        /// <inheritdoc/>
        public OverworldWorld.FieldWalkerSnapshot WalkerState() =>
            (creatorHost.CreatorFrameSource?.GravityWalkerState() ?? default);
    }

    private readonly IGravityHost? m_host = (((rootNode as Overworld.ICreatorModeHost) is { } creatorHost) ? new FrameSourceHost(creatorHost: creatorHost) : null);

    private const string HostUnavailable = "[planet: unavailable — the overworld is not the active root]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Spawns/resets the walker on the planetoid and opens the fullscreen takeover: planet.spawn [longitudeDegrees] (default 0).",
            handler: WithHostArgs(handler: Spawn),
            name: "planet.spawn"
        );
        yield return WithArgs(
            description: "Queues scripted forward motion: planet.walk <ticks|Ns> [direction] (direction: forward (default) / back / left / right). Combine with `step <n>` to actually advance the ticks.",
            handler: WithHostArgs(handler: Walk),
            name: "planet.walk"
        );
        yield return Plain(
            description: "Echoes the walker's state: position, up, facing, grounded, longitude.",
            handler: WithHost(handler: List),
            name: "planet.list"
        );
    }

    private Func<CommandContext, CommandResult> WithHost(Func<IGravityHost, string> handler) =>
        CommandAvailability.WithTarget(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private Func<CommandContext, string[], CommandResult> WithHostArgs(Func<IGravityHost, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private static string Spawn(IGravityHost host, string[] args) {
        var longitude = 0.0;

        if ((args.Length > 0) && !double.TryParse(s: args[0], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out longitude)) {
            return "[planet.spawn: usage — planet.spawn [longitudeDegrees]]";
        }

        host.SpawnWalker(longitudeDegrees: longitude);

        var state = host.WalkerState();

        return $"[planet.spawn: walker seated at longitude {longitude:0.0}°, pos={FormatPosition(state: state)}]";
    }

    // The live host's fixed sim tick rate (Puck.Launcher.LauncherWindowHostedService.TargetUpdateRate) — the SAME
    // 240 Hz OverworldReplayCapture.TickSeconds already documents for a scripted/replayed run.
    private const float LiveTicksPerSecond = 240.0f;

    private static readonly (string Name, Vector2 Move)[] Directions = [
        ("forward", new Vector2(x: 0f, y: 1f)),
        ("back", new Vector2(x: 0f, y: -1f)),
        ("left", new Vector2(x: -1f, y: 0f)),
        ("right", new Vector2(x: 1f, y: 0f)),
    ];

    private static string Walk(IGravityHost host, string[] args) {
        if (args.Length == 0) {
            return "[planet.walk: usage — planet.walk <ticks|Ns> [direction] (direction: forward/back/left/right, default forward)]";
        }

        if (!TryParseTicks(text: args[0], ticks: out var ticks)) {
            return "[planet.walk: usage — planet.walk <ticks|Ns> [direction] (an integer tick count, or a number suffixed 's' for seconds)]";
        }

        var move = Directions[0].Move;

        if (args.Length > 1) {
            var named = false;

            foreach (var (name, candidate) in Directions) {
                if (string.Equals(a: name, b: args[1], comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    move = candidate;
                    named = true;

                    break;
                }
            }

            if (!named) {
                return "[planet.walk: usage — direction must be forward/back/left/right]";
            }
        }

        host.WalkWalker(ticks: ticks, move: move);

        return $"[planet.walk: queued {ticks} tick(s) of {DirectionName(move: move)} intent]";
    }
    private static bool TryParseTicks(string text, out int ticks) {
        ticks = 0;

        if (text.EndsWith(value: "s", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (!double.TryParse(s: text[..^1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var seconds) || (seconds < 0.0)) {
                return false;
            }

            ticks = (int)Math.Round(a: (seconds * LiveTicksPerSecond));

            return true;
        }

        return (int.TryParse(s: text, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out ticks) && (ticks >= 0));
    }
    private static string DirectionName(Vector2 move) {
        foreach (var (name, candidate) in Directions) {
            if (candidate == move) {
                return name;
            }
        }

        return $"({move.X:0.0},{move.Y:0.0})";
    }
    private static string List(IGravityHost host) {
        var state = host.WalkerState();

        if (!state.Active) {
            return "[planet.list: no walker — planet.spawn [longitudeDegrees] seats one]";
        }

        var longitude = GravityScenario.LongitudeDegrees(position: state.Position);

        return $"[planet.list: pos={FormatPosition(state: state)} up=({(double)(float)state.Up.X:0.00},{(double)(float)state.Up.Y:0.00},{(double)(float)state.Up.Z:0.00}) longitude={longitude:0.0}° facing={(double)(float)state.FacingAngle:0.00} grounded={state.Grounded}]";
    }
    private static string FormatPosition(OverworldWorld.FieldWalkerSnapshot state) {
        if (!state.Active) {
            return "n/a";
        }

        var delta = state.Position.Delta(origin: GravityScenario.PlanetCenter);

        return $"({(double)(float)delta.X:0.00},{(double)(float)delta.Y:0.00},{(double)(float)delta.Z:0.00})";
    }

    // A no-argument console verb (mirrors RtsCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors RtsCommandModule.WithArgs).
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
