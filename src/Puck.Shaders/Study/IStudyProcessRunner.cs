namespace Puck.Shaders.Study;

/// <summary>The one process-spawning seam <see cref="StudyShaderCompiler"/> calls through — real tool invocation in
/// production, a call-counting fake in a cache-hit test that must observe zero invocations.</summary>
internal interface IStudyProcessRunner {
    /// <summary>Runs <paramref name="fileName"/> to completion with <paramref name="arguments"/>, capturing both
    /// output streams. Throws <see cref="System.ComponentModel.Win32Exception"/> when the OS cannot locate
    /// <paramref name="fileName"/> (bare-name resolution failing on the search path) — the caller translates that
    /// into <see cref="StudyToolMissingException"/>.</summary>
    StudyProcessResult Run(string fileName, IReadOnlyList<string> arguments);
}
/// <summary>The captured result of one <see cref="IStudyProcessRunner.Run"/> call.</summary>
internal readonly record struct StudyProcessResult(int ExitCode, string Stdout, string Stderr);
