using System.CommandLine;
using System.Globalization;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders cache prune</c>: removes every file of a shader compiler cache that no compile has used for a
/// given number of minutes (<see cref="ShaderCompiler.Prune"/>). Every compile stamps the entries it reads and writes,
/// so a CI job that restores a cache, builds and prunes with a bound longer than the job keeps exactly what the job's
/// builds used. Exit 0 pruned (a missing cache holds nothing), 2 an invalid bound.</summary>
public static class CacheCommand {
    private const string Verb = "shaders cache prune";

    /// <summary>Prunes a cache directory and reports what it removed and kept, one line.</summary>
    /// <param name="cacheDirectory">The cache directory.</param>
    /// <param name="unusedMinutes">How many minutes a file may go unused and stay, at least 1.</param>
    /// <param name="nowUtc">The current time, in UTC.</param>
    /// <returns>0 pruned, 2 an invalid bound.</returns>
    public static int Run(string cacheDirectory, int unusedMinutes, DateTime nowUtc) {
        if (unusedMinutes < 1) {
            return CliExit.Refuse(
                verb: Verb,
                what: $"--unused-minutes {unusedMinutes.ToString(provider: CultureInfo.InvariantCulture)}",
                why: "the bound must be at least 1 minute, or a build running beside the prune could lose what it just read."
            );
        }

        var result = ShaderCompiler.Prune(
            cacheDirectory: cacheDirectory,
            cutoffUtc: nowUtc.AddMinutes(value: -unusedMinutes)
        );

        Console.Out.WriteLine(value: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{Verb}: removed {result.Removed} file(s) ({(result.RemovedBytes / 1048576.0):0.0} MB) unused for {unusedMinutes} minute(s), kept {result.Kept} ({(result.KeptBytes / 1048576.0):0.0} MB) in {cacheDirectory}."
        ));

        return CliExit.Success;
    }
    /// <summary>Creates <c>puck shaders cache</c> and its <c>prune</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var cache = new Option<string?>("--cache") { Description = "The cache directory; the per-user shader cache the build and the CLI share when omitted." };
        var unused = new Option<int>("--unused-minutes") { Description = "Remove every file no compile has used for this many minutes, at least 1.", Required = true };
        var prune = new Command(
            description: """
            Remove every file of a shader compiler cache that no compile has used for --unused-minutes.

            A compile stamps each cache entry and duration record it reads, and publishing writes a
            new one, so a file's last write time is when a compile last used it. Prune removes each
            entry (*.spv, *.dxil), each abandoned staged publication (*.tmp) and each duration record
            older than the bound, and leaves anything else in the directory alone. CI restores a
            cache, builds, then prunes with a bound longer than the job before it saves, so the saved
            cache holds what that job's builds used. Exit 0 pruned (a missing cache holds nothing),
            2 an invalid bound.
            """,
            name: "prune"
        ) { cache, unused };

        prune.SetAction(action: result => Run(
            cacheDirectory: Path.GetFullPath(path: (result.GetValue(option: cache) ?? ShaderCompiler.DefaultCacheDirectory)),
            nowUtc: DateTime.UtcNow,
            unusedMinutes: result.GetValue(option: unused)
        ));

        var command = new Command(
            description: "Maintain the shader compiler cache.",
            name: "cache"
        );

        command.Subcommands.Add(item: prune);

        return command;
    }
}
