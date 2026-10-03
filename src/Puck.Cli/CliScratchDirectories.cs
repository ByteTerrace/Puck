namespace Puck.Cli;

/// <summary>Pins the SDK of the scratch projects CLI verbs and fixtures build outside the checkout. The directory itself
/// comes from the one run-directory policy (<see cref="RunDirectory"/>, or a law's <c>TemporaryDirectory</c>).</summary>
public static class CliScratchDirectories {
    /// <summary>Copies the checkout's <c>global.json</c> into <paramref name="directory"/>, so the SDK version and
    /// roll-forward policy that select the SDK there are the checkout's own.
    /// <para>The .NET host selects the SDK from the working directory's nearest <c>global.json</c>, and MSBuild resolves
    /// a project's SDK from the project's; an SDK command against a scratch project runs from this directory or a
    /// descendant so both agree on the pin.</para></summary>
    /// <param name="directory">The scratch project's existing root directory.</param>
    public static void PinSdk(string directory) => File.Copy(
        destFileName: Path.Combine(
            path1: directory,
            path2: "global.json"
        ),
        sourceFileName: RepositoryPaths.Resolve(relativePath: "global.json")
    );
}
