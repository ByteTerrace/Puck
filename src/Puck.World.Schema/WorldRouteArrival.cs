using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.World;

/// <summary>One body pose a route's arrival is judged against.</summary>
/// <param name="X">The body's x, in metres.</param>
/// <param name="Y">The body's y, in metres.</param>
/// <param name="Z">The body's z, in metres.</param>
/// <param name="Grounded">Whether the body stands on support.</param>
public readonly record struct WorldRoutePose(double X, double Y, double Z, bool Grounded);
/// <summary>
/// Judges a played chapter's arrival from a body's pose: the one judge of the two gate forms a pose answers, a body
/// position axis compared with a number and the body's grounded fact. A live run and every reader of a played route
/// call it; any other gate is judged by the headless test alone and returned unjudged.
/// </summary>
public static partial class WorldRouteArrival {
    [GeneratedRegex(pattern: @"^bodyPosition\(body, (?<body>\d+), (?<axis>[xyz])\) (?<op>>=|<=|==|!=|>|<) (?<value>-?\d+(?:\.\d+)?)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex AxisGate();
    [GeneratedRegex(pattern: @"^fact\(body, (?<body>\d+), Grounded\) == 1$", options: RegexOptions.CultureInvariant)]
    private static partial Regex GroundedGate();

    /// <summary>Returns the body a chapter's arrival gates read: the first body a pose gate names, or 0.</summary>
    /// <param name="gates">The arrival gates as written.</param>
    /// <returns>The 0-based body index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gates"/> is <see langword="null"/>.</exception>
    public static int BodyOf(IReadOnlyList<string> gates) {
        ArgumentNullException.ThrowIfNull(argument: gates);

        foreach (var gate in gates) {
            if (((AxisGate().Match(input: gate) is { Success: true } axis) ? axis : GroundedGate().Match(input: gate)) is { Success: true } match) {
                return int.Parse(s: match.Groups["body"].Value, provider: CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    /// <summary>Judges the gates against the pose of the body each names.</summary>
    /// <param name="gates">The arrival gates as written.</param>
    /// <param name="pose">The reader of a body's pose by 0-based index; it returns <see langword="null"/> for a body
    /// whose pose was not read.</param>
    /// <param name="unjudged">The gates a pose cannot judge, and those naming a body with no pose.</param>
    /// <returns>Each gate the pose fails, with the value it read; empty when the body arrived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gates"/> or <paramref name="pose"/> is
    /// <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Judge(IReadOnlyList<string> gates, Func<int, WorldRoutePose?> pose, out IReadOnlyList<string> unjudged) {
        ArgumentNullException.ThrowIfNull(argument: gates);
        ArgumentNullException.ThrowIfNull(argument: pose);

        var failed = new List<string>();
        var skipped = new List<string>();

        foreach (var gate in gates) {
            if ((AxisGate().Match(input: gate) is { Success: true } axis) && (pose(arg: int.Parse(s: axis.Groups["body"].Value, provider: CultureInfo.InvariantCulture)) is { } posed)) {
                var read = (axis.Groups["axis"].Value switch { "x" => posed.X, "y" => posed.Y, _ => posed.Z });
                var value = double.Parse(s: axis.Groups["value"].Value, provider: CultureInfo.InvariantCulture);
                var holds = (axis.Groups["op"].Value switch { ">" => (read > value), ">=" => (read >= value), "<" => (read < value), "<=" => (read <= value), "==" => (read == value), _ => (read != value) });

                if (!holds) {
                    failed.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{gate} (read {read})"));
                }
            } else if ((GroundedGate().Match(input: gate) is { Success: true } grounded) && (pose(arg: int.Parse(s: grounded.Groups["body"].Value, provider: CultureInfo.InvariantCulture)) is { } standing)) {
                if (!standing.Grounded) {
                    failed.Add(item: $"{gate} (not grounded)");
                }
            } else {
                skipped.Add(item: gate);
            }
        }

        unjudged = skipped;

        return failed;
    }
}
