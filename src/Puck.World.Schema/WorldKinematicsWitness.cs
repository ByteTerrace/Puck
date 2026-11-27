namespace Puck.World;

/// <summary>One of the kinematic witnesses a body's own step keeps (<see cref="WorldRuleFacts.KinematicsPrefix"/>):
/// what its contact phase decided about stepping, snapping, leaving and meeting the ground, sliding off a slope,
/// pressing without moving and ending a solve inside a surface. Each is a total or a high-water mark over the body's
/// life since it was activated, never reset, so a reader takes a window by reading at both ends.</summary>
public enum WorldKinematicsWitness : byte {
    /// <summary>The step-ups taken: risers the body walked up onto.</summary>
    StepUps = 0,
    /// <summary>The step-ups refused: risers the body cleared at its step height and could not stand on beyond.</summary>
    StepRefusals,
    /// <summary>The tallest riser a step-up took, in metres.</summary>
    StepRise,
    /// <summary>The ground snaps taken: steps that followed walkable ground down within the snap reach.</summary>
    Snaps,
    /// <summary>The steps that left the ground: grounded before, airborne after.</summary>
    Leaves,
    /// <summary>The steps that met the ground: airborne before, grounded after.</summary>
    Landings,
    /// <summary>The time, in seconds, the current airborne run has lasted; zero while grounded or held on a face by a pull (a climb).</summary>
    Airborne,
    /// <summary>The longest airborne run, in seconds.</summary>
    AirborneLongest,
    /// <summary>The time, in seconds, the current run of steps the body was driven and moved less than the stuck
    /// distance has lasted; zero once a step moves it or it is not driven.</summary>
    Stuck,
    /// <summary>The longest run, in seconds, of steps the body was driven and moved less than the stuck distance.</summary>
    StuckLongest,
    /// <summary>The driven steps that ended pressed against a face that faces up but is too steep to stand on.</summary>
    SlopeRefusals,
    /// <summary>The push, in metres past the skin's resting band, the latest contact solve still applied in its last
    /// pass: how far inside a surface the latest step ended.</summary>
    Depth,
    /// <summary>The deepest push, in metres past the skin's resting band, a contact solve still applied in its last
    /// pass: how far inside a surface the body ever ended a step.</summary>
    Penetration,
}
/// <summary>Spells and reads <see cref="WorldKinematicsWitness"/> names as a world rule writes them
/// (<c>kinematics(body, 0, stepUps)</c>).</summary>
public static class WorldKinematicsVocabulary {
    private static readonly string[] Names = [
        "stepUps", "stepRefusals", "stepRise", "snaps", "leaves", "landings", "airborne", "airborneLongest", "stuck",
        "stuckLongest", "slopeRefusals", "depth", "penetration",
    ];

    /// <summary>Gets every witness name, in the order of <see cref="WorldKinematicsWitness"/>.</summary>
    public static IReadOnlyList<string> All => Names;

    /// <summary>Returns a witness's name as a rule spells it.</summary>
    /// <param name="witness">The witness.</param>
    /// <returns>The camel-case name.</returns>
    public static string NameOf(WorldKinematicsWitness witness) => Names[(int)witness];
    /// <summary>Resolves a witness by the name a rule spells it with, case-sensitively.</summary>
    /// <param name="name">The name.</param>
    /// <param name="witness">The witness, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when no witness has that name.</returns>
    public static bool TryResolve(string name, out WorldKinematicsWitness witness) {
        var index = Array.IndexOf(array: Names, value: name);

        witness = (WorldKinematicsWitness)Math.Max(val1: 0, val2: index);
        return (index >= 0);
    }
    /// <summary>Returns the kind of value a witness reads as: a count is <see cref="CellKind.Int"/>, a time or a length
    /// <see cref="CellKind.Fixed"/>.</summary>
    /// <param name="witness">The witness.</param>
    /// <returns>The cell kind.</returns>
    public static CellKind KindOf(WorldKinematicsWitness witness) => (witness switch {
        WorldKinematicsWitness.StepRise or WorldKinematicsWitness.Airborne or WorldKinematicsWitness.AirborneLongest or
            WorldKinematicsWitness.Stuck or WorldKinematicsWitness.StuckLongest or WorldKinematicsWitness.Depth or
            WorldKinematicsWitness.Penetration => CellKind.Fixed,
        _ => CellKind.Int,
    });
}
