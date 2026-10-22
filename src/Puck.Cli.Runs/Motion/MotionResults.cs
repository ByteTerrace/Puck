namespace Puck.Cli.Motion;

/// <summary>What one still came to.</summary>
/// <param name="Offset">The capture tick, counted from the scenario's tick 0.</param>
/// <param name="Tick">The absolute World tick.</param>
/// <param name="Hash">The capture-scope state hash, or <see langword="null"/> when the World announced no still.</param>
/// <param name="Pose">The pose-scope state hash, or <see langword="null"/> when the World announced no still.</param>
/// <param name="Where">The <c>body.where 0</c> answer read at the still's tick, or empty.</param>
/// <param name="Slots">Each camera's file, by camera, as a path relative to the run's directory.</param>
public sealed record MotionStillResult(int Offset, ulong Tick, string? Hash, string? Pose, string Where, IReadOnlyDictionary<string, string> Slots);
/// <summary>What one scenario came to.</summary>
/// <param name="Planned">The scenario as laid out on ticks.</param>
/// <param name="Stills">Its stills, by tick.</param>
/// <param name="ObservedForm">Which creation body 0 wore at tick 0 (<c>moth</c> or <c>moth-pilot</c>), or empty when the read never came back.</param>
/// <param name="Sheets">The contact sheets, by name (<c>combined</c> and each camera), as paths relative to the run's directory.</param>
/// <param name="Problems">What went wrong: a refused line, a missing still, a body in the wrong form. Empty when nothing did.</param>
public sealed record MotionScenarioResult(MotionPlannedScenario Planned, IReadOnlyList<MotionStillResult> Stills, string ObservedForm, IReadOnlyDictionary<string, string> Sheets, IReadOnlyList<string> Problems);
/// <summary>One whole run's outcome.</summary>
/// <param name="Error">Why the run could not complete, or empty. A run with an error holds no scenarios.</param>
/// <param name="TooLateStart">The earliest start tick a retry needs, when the World had passed the start the run asked for; otherwise <see langword="null"/>.</param>
/// <param name="BootTick">The tick the World had reached when its bakes settled and lockstep was on.</param>
/// <param name="StartTick">The tick the run's first line was pinned to.</param>
/// <param name="Seconds">How long the run took, boot to exit.</param>
/// <param name="Directory">The run's directory: stills, sheets and logs.</param>
/// <param name="Cameras">The camera names of the layout, in slot order.</param>
/// <param name="Scenarios">The scenarios' outcomes.</param>
/// <param name="Notes">Warnings that do not fail the run, such as bakes that did not settle.</param>
public sealed record MotionRunResult(string Error, ulong? TooLateStart, ulong BootTick, ulong StartTick, double Seconds, string Directory, IReadOnlyList<string> Cameras, IReadOnlyList<MotionScenarioResult> Scenarios, IReadOnlyList<string> Notes) {
    /// <summary>Gets a value indicating whether the run finished and no scenario reported a problem.</summary>
    public bool Clean => ((Error.Length == 0) && Scenarios.All(predicate: static scenario => (scenario.Problems.Count == 0)));
}
/// <summary>What a run is given.</summary>
/// <param name="Artifact">The World build's entry assembly.</param>
/// <param name="WorkingDirectory">The directory the World runs in.</param>
/// <param name="World">The world to boot: a package folder or a document.</param>
/// <param name="Directory">The run's directory: stills, sheets and logs go here.</param>
/// <param name="StateDirectory">The World's own state root.</param>
/// <param name="Set">The scenario file's content.</param>
/// <param name="Selected">The scenarios to run, in order.</param>
/// <param name="StartTick">The start tick to pin to, or <see langword="null"/> to choose one past the boot tick.</param>
/// <param name="Backend">The graphics backend, or <see langword="null"/> for the World's default.</param>
/// <param name="Columns">The most frames in a band of a contact sheet.</param>
/// <param name="CellWidth">The width of each frame of a contact sheet, in pixels.</param>
/// <param name="Deadline">The clock the whole run must finish before.</param>
public sealed record MotionRunRequest(string Artifact, string WorkingDirectory, string World, string Directory, string StateDirectory, MotionScenarioSet Set, IReadOnlyList<MotionScenario> Selected, ulong? StartTick, string? Backend, int Columns, int CellWidth, TimeSpan Deadline);
