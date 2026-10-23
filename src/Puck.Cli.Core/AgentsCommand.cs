using System.CommandLine;
using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>Connects native agent discovery to the checkout's shared skills.</summary>
public static class AgentsCommand {
    /// <summary>Creates the agent setup commands.</summary>
    /// <returns>The agents command.</returns>
    public static Command Create() {
        var check = CliOptions.Check(description: "Inspect the skills link without creating it.");
        var link = new Command(description: "Link .agents/skills to this checkout's .claude/skills; --check only inspects it.", name: "link") { check };

        link.SetAction(action: async (parse, cancellationToken) => await LinkAsync(root: RepositoryPaths.RequireRoot(), check: parse.GetValue(option: check), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
        return new Command(description: "Set up the checkout's agent tools.", name: "agents") { link };
    }
    /// <summary>Creates or checks the one skill-directory link without replacing existing content.</summary>
    /// <param name="root">The checkout root.</param>
    /// <param name="check">Whether to inspect without writing.</param>
    /// <param name="cancellationToken">Cancels link creation.</param>
    /// <returns>Zero for a correct link, one for check drift, or two for a creation refusal.</returns>
    public static async Task<int> LinkAsync(string root, bool check, CancellationToken cancellationToken = default) {
        var target = Path.GetFullPath(path: Path.Combine(path1: root, path2: ".claude/skills"));
        var path = Path.GetFullPath(path: Path.Combine(path1: root, path2: ".agents/skills"));
        var link = new DirectoryInfo(path: path);

        int Refuse(string what, string why) {
            CliExit.Refuse(verb: "agents link", what: what, why: why);
            return (check ? 1 : 2);
        }

        if (!Directory.Exists(path: target)) { return Refuse(what: CliPaths.ToDisplay(fullPath: target), why: "the checkout has no .claude/skills directory."); }
        if (link.LinkTarget is { } existing) {
            var resolved = Path.GetFullPath(path: existing, basePath: Path.GetDirectoryName(path: path)!);

            if (!PuckPaths.Comparer.Equals(x: Path.TrimEndingDirectorySeparator(path: resolved), y: Path.TrimEndingDirectorySeparator(path: target))) {
                return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "the existing link points elsewhere; it was not replaced.");
            }
        } else if (Path.Exists(path: path)) {
            return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "an ordinary file or directory already exists; it was not replaced.");
        } else if (check) {
            return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "the skills link is missing; run puck agents link.");
        } else {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            if (OperatingSystem.IsWindows()) {
                // Literal PowerShell strings retain spaces, quotes and shell metacharacters in checkout names.
                var script = $"New-Item -ItemType Junction -Path '{path.Replace(newValue: "''", oldValue: "'")}' -Target '{target.Replace(newValue: "''", oldValue: "'")}' -ErrorAction Stop | Out-Null";

                await CliProcess.RunCheckedAsync(fileName: "pwsh", arguments: ["-NoProfile", "-NonInteractive", "-Command", script], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } else {
                Directory.CreateSymbolicLink(path: path, pathToTarget: target);
            }
            return await LinkAsync(cancellationToken: cancellationToken, check: true, root: root).ConfigureAwait(continueOnCapturedContext: false);
        }
        Console.WriteLine(value: "agents link: .agents/skills -> .claude/skills (current checkout).");
        return 0;
    }
}
