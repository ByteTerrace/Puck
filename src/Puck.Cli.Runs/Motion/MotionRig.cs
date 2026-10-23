using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Motion;

/// <summary>One foot effector of a <c>body.rig</c> read: whether its foot is planted, and the ground normal it solved against.</summary>
/// <param name="Name">The effector's name, such as <c>foot-left</c>.</param>
/// <param name="Planted">The plant state: <c>yes</c>, <c>no</c> or <c>stepping</c>.</param>
/// <param name="Weight">The effector's eased weight, from 0 to 1.</param>
/// <param name="Target">The world point the boot is asked for, as the World prints it, or <c>none</c>.</param>
/// <param name="Normal">The contact normal the sole aligns to, as the World prints it.</param>
public sealed record MotionFoot(string Name, string Planted, double Weight, string Target, string Normal);
/// <summary>What a <c>body.rig</c> read says about a body's gait at one tick: its eased speed and each foot effector.</summary>
/// <param name="Speed">The eased rendered speed, in metres a second.</param>
/// <param name="Feet">The effectors whose name holds <c>foot</c>.</param>
public sealed record MotionRigReading(double Speed, IReadOnlyList<MotionFoot> Feet);
/// <summary>Reads the <c>body.rig</c> answer: the presentation-side gait state nothing else can read.</summary>
public static partial class MotionRig {
    [GeneratedRegex(pattern: @"speed=(?<speed>-?[\d.]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex SpeedPattern();
    [GeneratedRegex(pattern: @"effector:(?<name>\S*foot\S*) weight=(?<weight>-?[\d.]+) planted=(?<planted>\w+) target=(?<target>none|\([^)]*\)) align=\S+ normal=(?<normal>\([^)]*\))", options: RegexOptions.CultureInvariant)]
    private static partial Regex FootPattern();
    [GeneratedRegex(pattern: @"pos=\((?<x>-?[\d.]+), (?<y>-?[\d.]+), (?<z>-?[\d.]+)\) yaw=(?<yaw>-?[\d.]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex PosePattern();
    [GeneratedRegex(pattern: @"facts=(?<facts>[^\s\]]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex FactsPattern();

    /// <summary>Reads a <c>body.rig</c> answer.</summary>
    /// <param name="answer">The answer, <c>[body.rig: body:0 creation=… speed=… effector:foot-left weight=… planted=… …]</c>.</param>
    /// <param name="reading">The reading, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the answer names a speed.</returns>
    public static bool TryParse(string answer, [NotNullWhen(returnValue: true)] out MotionRigReading? reading) {
        var speed = SpeedPattern().Match(input: answer);

        if (!speed.Success) {
            reading = null;

            return false;
        }

        reading = new MotionRigReading(
            Feet: [.. FootPattern().Matches(input: answer).Select(selector: static match => new MotionFoot(
                Name: match.Groups["name"].Value,
                Normal: match.Groups["normal"].Value,
                Planted: match.Groups["planted"].Value,
                Target: match.Groups["target"].Value,
                Weight: double.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["weight"].Value)
            ))],
            Speed: double.Parse(provider: CultureInfo.InvariantCulture, s: speed.Groups["speed"].Value)
        );

        return true;
    }
    /// <summary>Reads the pose a <c>body.where</c> answer prints.</summary>
    /// <param name="answer">The answer, <c>[body.where: body:0 pos=(x, y, z) yaw=D° …]</c>.</param>
    /// <param name="position">The world position, in metres.</param>
    /// <param name="yawDegrees">The heading, in degrees; 0 faces -Z and positive turns left.</param>
    /// <returns><see langword="true"/> when the answer prints both.</returns>
    public static bool TryPoseOf(string answer, out System.Numerics.Vector3 position, out double yawDegrees) {
        var match = PosePattern().Match(input: answer);

        if (!match.Success) {
            position = default;
            yawDegrees = 0d;

            return false;
        }

        position = new System.Numerics.Vector3(
            x: float.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["x"].Value),
            y: float.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["y"].Value),
            z: float.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["z"].Value)
        );
        yawDegrees = double.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["yaw"].Value);

        return true;
    }
    /// <summary>Reads the fact set a <c>body.where</c> answer prints.</summary>
    /// <param name="answer">The answer, <c>[body.where: … facts=grounded|… …]</c>.</param>
    /// <returns>The facts, such as <c>grounded</c> or <c>airborne|falling</c>, or empty when the answer prints none.</returns>
    public static string FactsOf(string answer) {
        var match = FactsPattern().Match(input: answer);

        return (match.Success ? match.Groups["facts"].Value : string.Empty);
    }
    /// <summary>Formats the second line of a still's label: each foot's plant state, the eased speed and the body's facts.</summary>
    /// <param name="rig">The gait reading, or <see langword="null"/> when the read never answered.</param>
    /// <param name="facts">The body's facts at the tick, or empty.</param>
    /// <returns>The line, such as <c>L plant R step | spd 2.50 | grounded</c>.</returns>
    public static string Describe(MotionRigReading? rig, string facts) {
        if (rig is null) {
            return facts;
        }

        var feet = string.Join(separator: ' ', values: rig.Feet.Select(selector: static foot => $"{Side(name: foot.Name)} {State(planted: foot.Planted)}"));
        var speed = $"spd {rig.Speed.ToString(format: "0.00", provider: CultureInfo.InvariantCulture)}";

        return string.Join(separator: " | ", values: new[] { feet, speed, facts }.Where(predicate: static part => (part.Length != 0)));
    }

    private static string Side(string name) => (name.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "left")
        ? "L"
        : (name.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "right") ? "R" : name)
    );
    private static string State(string planted) => planted switch {
        "yes" => "plant",
        "stepping" => "step",
        _ => "free",
    };
}
