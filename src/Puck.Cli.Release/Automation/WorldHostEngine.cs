using System.Globalization;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.Cli.Automation;

/// <summary>An installed host release: the install root a host release was installed into through the release channel's
/// installer, and the version its release record names current. Its identity is that version's payload identity, which
/// its install record carries and nothing else computes, so an engine is named by what was installed, never by a path's
/// spelling.</summary>
public static class WorldHostEngine {
    /// <summary>Reads the payload identity of an install root's current version.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <returns>The current version's directory and payload identity, or <see langword="null"/> when the root holds no
    /// current version with a valid install record.</returns>
    public static (string Version, string Identity)? Current(string installRoot) {
        if (ReleasePolicy.ReadUnlocked(installRoot: installRoot).Current is not { } current) { return null; }
        var version = Path.Combine(path1: installRoot, path2: InstallLayout.VersionsDirectoryName, path3: current);

        return ((Of(version: version) is { } identity) ? (version, identity) : null);
    }
    /// <summary>Reads the payload identity of an installed version directory.</summary>
    /// <param name="version">The version directory.</param>
    /// <returns>The identity its install record carries, or <see langword="null"/> when it has no valid record.</returns>
    public static string? Of(string version) {
        using var directory = ConfinedDirectory.OpenAnchor(create: false, path: version);

        return (((directory is not null) && (InstallRecord.TryRead(version: directory) is { } record)) ? record.PayloadPin : null);
    }
    /// <summary>Resolves an install root to the version directory of the engine a release names.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="expected">The engine identity the release names.</param>
    /// <returns>The current version's directory.</returns>
    /// <exception cref="InvalidDataException">The root holds no installed engine, or another one.</exception>
    public static string Resolve(string installRoot, string expected) {
        var current = (Current(installRoot: Path.GetFullPath(path: installRoot)) ?? throw new InvalidDataException(message: $"{installRoot} holds no installed host release"));

        return ((current.Identity == expected)
            ? current.Version
            : throw new InvalidDataException(message: $"{installRoot} runs the engine {current.Identity}, not the release's {expected}"));
    }
}
/// <summary>Runs a qualification leg's packaged engine. An implementation throws when it cannot start the engine, which is
/// a refusal; the exit code and captured output of the packaged command are returned, because a nonzero code is a leg that
/// failed and its output names why.</summary>
public interface IWorldReleaseQualificationEngines {
    /// <summary>Resolves an install root to the version directory of the engine a release names.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="expected">The engine identity the release names.</param>
    /// <returns>The engine's version directory.</returns>
    /// <exception cref="InvalidDataException">The root holds no installed engine, or another one.</exception>
    string Resolve(string installRoot, string expected);
    /// <summary>Runs <c>world release exercise</c> from an installed engine over a leg's fixture.</summary>
    /// <param name="engine">The installed engine's version directory.</param>
    /// <param name="fixture">The leg's fixture directory, the only state the engine reads or writes.</param>
    /// <param name="steps">The exact continuation steps the exercise runs.</param>
    /// <param name="cancellationToken">The leg's deadline.</param>
    /// <returns>The packaged command's exit code and its captured standard output and error.</returns>
    Task<ChildProcessResult> ExerciseAsync(string engine, string fixture, int steps, CancellationToken cancellationToken);
}
/// <summary>Runs qualification legs as processes of the installed engine itself: its own CLI, from its own version
/// directory, over the leg's fixture copy. The fixture's silo document names only paths inside the fixture and a directory
/// store, and the exercise is handed no identity, so a leg reaches no deployment's state.</summary>
internal sealed class WorldReleaseQualificationProcesses : IWorldReleaseQualificationEngines {
    public string Resolve(string installRoot, string expected) => WorldHostEngine.Resolve(expected: expected, installRoot: installRoot);
    public async Task<ChildProcessResult> ExerciseAsync(string engine, string fixture, int steps, CancellationToken cancellationToken) {
        var executable = Path.Combine(path1: engine, path2: InstallLayout.ExecutableFileName(name: WorldHostLayout.Executable));

        if (!File.Exists(path: executable)) { throw new InvalidOperationException(message: $"the installed engine {engine} carries no {WorldHostLayout.Executable}"); }
        return await CliProcess.RunAsync(
            arguments: ["world", "release", "exercise", fixture, "--steps", steps.ToString(provider: CultureInfo.InvariantCulture)],
            cancellationToken: cancellationToken,
            fileName: executable,
            workingDirectory: engine
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}
