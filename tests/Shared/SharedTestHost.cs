using System.Runtime.CompilerServices;

namespace Puck.Testing;

/// <summary>
/// Refuses a suite started through its apphost, before any law runs. Windows Firewall asks about a process that listens
/// on an interface once per executable image path, msquic opens a QUIC listener's UDP port on every interface whatever
/// address it names, and each checkout builds its own <c>&lt;suite&gt;.exe</c>, so an apphost run in a new worktree raises
/// a new prompt. Under the shared host, <c>dotnet</c>, one decision covers every worktree and run: <c>dotnet test</c>
/// reaches it through the <c>RunCommand</c> Directory.Build.targets sets, and every <c>puck</c> runner and CI job
/// starts <c>dotnet &lt;suite&gt;.dll</c>.
/// </summary>
internal static class SharedTestHost {
    /// <summary>The exit code of a refused apphost run, distinct from every Microsoft.Testing.Platform exit code.</summary>
    public const int RefusedExitCode = 87;

    /// <summary>Whether <paramref name="processPath"/> is the shared <c>dotnet</c> host rather than a suite's apphost.</summary>
    /// <param name="processPath">The running process's executable path.</param>
    /// <returns><see langword="true"/> for <c>dotnet</c> or <c>dotnet.exe</c>, and for an unknown path.</returns>
    public static bool IsSharedHost(string? processPath) => ((processPath is null) || string.Equals(
        a: Path.GetFileNameWithoutExtension(path: processPath),
        b: "dotnet",
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));

    [ModuleInitializer]
    internal static void RefuseTheApphost() {
        var processPath = Environment.ProcessPath;

        if (IsSharedHost(processPath: processPath)) {
            return;
        }

        var assembly = Path.ChangeExtension(extension: ".dll", path: processPath!);

        Console.Error.WriteLine(value: $"Refused: {Path.GetFileName(path: processPath)} is this checkout's apphost, a new executable path Windows Firewall asks about once a law listens. Run `dotnet test --project <suite>` or `dotnet \"{assembly}\"` with the same options instead.");
        Environment.Exit(exitCode: RefusedExitCode);
    }
}
