using System.CommandLine;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Puck.Cli.NuGet;

/// <summary>
/// The GitHub side of a NuGet publication: the ref gate, the version tag, the release record, and the CLI pin
/// adopted once the published package installs. <c>gh</c> and <c>git</c> supply authentication; GITHUB_SHA,
/// GITHUB_REF, GITHUB_REPOSITORY, GITHUB_RUN_ID, and GITHUB_RUN_ATTEMPT come from the workflow.
/// </summary>
internal static class NuGetReleaseCommand {
    private const string Package = "byteterrace.puck.cli";

    public static Command Gate() {
        var command = new Command(description: "Refuse a release from any ref other than main or the shared version's own tag, or from a commit outside main's history, or at a version tag bound to another commit.", name: "gate");

        command.SetAction(action: (_, _) => GateAsync());
        return command;
    }
    public static Command Tag() {
        var command = new Command(description: "Bind v<version> to GITHUB_SHA, or verify the existing tag already binds it.", name: "tag");

        command.SetAction(action: (_, _) => TagAsync());
        return command;
    }
    public static Command Release() {
        var manifestArgument = new Argument<string>(name: "manifest") { Description = "The batch's release.json." };
        var command = new Command(description: "Create the GitHub Release for v<version> when missing, then upload the batch manifest named for the run and attempt.", name: "release") { manifestArgument };

        command.SetAction(action: (parseResult, _) => ReleaseAsync(manifest: parseResult.GetRequiredValue(argument: manifestArgument)));
        return command;
    }
    public static Command Pin() {
        var versionArgument = new Argument<string>(name: "version") { Description = "The published CLI version to adopt." };
        var command = new Command(description: "Install a published CLI version in isolation to prove it, then pin it in the tool manifest.", name: "pin") { versionArgument };

        command.SetAction(action: (parseResult, _) => PinAsync(version: parseResult.GetRequiredValue(argument: versionArgument)));
        return command;
    }
    public static Command PinPublished() {
        var packagesArgument = new Argument<string>(name: "packages") { Description = "The pushed batch directory; the pin runs only when it holds the CLI package." };
        var patchArgument = new Argument<string>(name: "patch") { Description = "Where to write the tool-manifest diff for adoption." };
        var command = new Command(description: "Wait for the just-published CLI to become installable, pin it, and write the manifest diff.", name: "pin-published") { packagesArgument, patchArgument };

        command.SetAction(action: (parseResult, _) => PinPublishedAsync(packages: parseResult.GetRequiredValue(argument: packagesArgument), patch: parseResult.GetRequiredValue(argument: patchArgument)));
        return command;
    }
    public static Command Smoke() {
        var packagesArgument = new Argument<string>(name: "packages") { Description = "The directory holding the packed CLI." };
        var command = new Command(description: "Install the packed CLI into a temporary tool path from an exclusive feed and exercise its verbs, including the Roslyn workspace host over a probe project.", name: "smoke") { packagesArgument };

        command.SetAction(action: (parseResult, _) => SmokeAsync(packages: parseResult.GetRequiredValue(argument: packagesArgument)));
        return command;
    }

    private static string Version(string root) => ("v" + NuGetCommand.ExpectedVersion(root: root));
    private static async Task<string> GitAsync(string root, params string[] arguments) => (await CliProcess.RunCheckedAsync(arguments: arguments, capture: true, executable: "git", root: root)).Trim();
    private static async Task<string?> TagCommitAsync(string root, string tag) {
        var listed = await GitAsync(root, "tag", "--list", tag);

        return ((listed.Length == 0) ? null : await GitAsync(root, "rev-parse", (tag + "^{commit}")));
    }
    private static async Task<int> GateAsync() {
        var root = NuGetCommand.Root();
        var tag = Version(root: root);
        var reference = CliGitHub.EnvironmentVariable(name: "GITHUB_REF");
        var commit = CliGitHub.EnvironmentVariable(name: "GITHUB_SHA");

        if ((reference != "refs/heads/main") && (reference != ("refs/tags/" + tag))) {
            throw new InvalidOperationException(message: "Release from main, or from the existing version tag for retries and additional batches.");
        }
        await GitAsync(root, "merge-base", "--is-ancestor", commit, "origin/main");
        if ((await TagCommitAsync(root: root, tag: tag) is { } bound) && (bound != commit)) {
            throw new InvalidOperationException(message: $"{tag} is already bound to another commit. Run from {tag} or bump the shared version.");
        }
        return 0;
    }
    private static async Task<int> TagAsync() {
        var root = NuGetCommand.Root();
        var tag = Version(root: root);
        var commit = CliGitHub.EnvironmentVariable(name: "GITHUB_SHA");

        if (await TagCommitAsync(root: root, tag: tag) is { } bound) {
            if (bound != commit) { throw new InvalidOperationException(message: $"{tag} is bound to {bound}, not this commit."); }
            return 0;
        }
        await CliProcess.RunCheckedAsync(arguments: ["api", "--method", "POST", $"repos/{CliGitHub.EnvironmentVariable(name: "GITHUB_REPOSITORY")}/git/refs", "--raw-field", ("ref=refs/tags/" + tag), "--raw-field", ("sha=" + commit)], executable: "gh", root: root);
        return 0;
    }
    private static async Task<int> ReleaseAsync(string manifest) {
        var root = NuGetCommand.Root();
        var tag = Version(root: root);
        var existing = CliProcess.RunCapturedRaw(arguments: ["release", "view", tag, "--json", "tagName"], fileName: "gh");

        if ((existing.ExitCode != 0) && !existing.Stderr.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "not found")) {
            throw new InvalidOperationException(message: $"gh release view {tag} failed: {existing.Stderr.Trim()}");
        }
        if (existing.ExitCode != 0) {
            string[] prerelease = (tag.Contains(value: '-') ? ["--prerelease"] : []);

            await CliProcess.RunCheckedAsync(arguments: ["release", "create", tag, "--verify-tag", "--title", tag, "--generate-notes", .. prerelease], executable: "gh", root: root);
        }
        // An existing release receives each additional batch as its own manifest asset.
        var copy = $"release-{CliGitHub.EnvironmentVariable(name: "GITHUB_RUN_ID")}-{CliGitHub.EnvironmentVariable(name: "GITHUB_RUN_ATTEMPT")}.json";

        File.Copy(destFileName: copy, overwrite: true, sourceFileName: manifest);
        await CliProcess.RunCheckedAsync(arguments: ["release", "upload", tag, copy], executable: "gh", root: root);
        return 0;
    }
    private static async Task<int> PinAsync(string version) {
        var root = NuGetCommand.Root();
        var directory = CliScratchDirectories.CreateRunDirectory(scratchPrefix: "puck-official-install-");

        try {
            // The published package is proven in an empty tool path before tracked configuration changes.
            var executable = await InstallAsync(configFile: Path.Combine(path1: root, path2: "nuget.config"), directory: directory, root: root, version: NuGetCommand.ValidateVersion(version: version));

            await CliProcess.RunCheckedAsync(arguments: ["nuget", "--help"], executable: executable, root: root);
            var manifestPath = Path.Combine(path1: root, path2: ".config/dotnet-tools.json");
            var manifest = CliFiles.ReadJson(path: manifestPath);

            manifest["tools"]![Package] = new JsonObject { ["version"] = version, ["commands"] = new JsonArray("puck"), ["rollForward"] = false };
            CliFiles.WriteJson(path: manifestPath, value: manifest);
        } finally { if (Directory.Exists(path: directory)) { Directory.Delete(path: directory, recursive: true); } }
        return 0;
    }
    private static async Task<int> PinPublishedAsync(string packages, string patch) {
        var root = NuGetCommand.Root();
        var version = NuGetCommand.ExpectedVersion(root: root);

        if (!File.Exists(path: Path.Combine(path1: packages, path2: $"ByteTerrace.Puck.Cli.{version}.nupkg"))) {
            Console.WriteLine(value: "The batch does not publish the CLI; no pin to prepare.");
            return 0;
        }
        for (var attempt = 1; ; attempt++) {
            try { await PinAsync(version: version); break; } catch (Exception error) when ((attempt < 30)) {
                Console.Error.WriteLine(value: $"Publication is not installable yet ({error.Message}); attempt {attempt}/30.");
                await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 10));
            }
        }
        File.WriteAllText(contents: await GitAsync(root, "diff", "--", ".config/dotnet-tools.json"), path: patch);
        return 0;
    }
    private static async Task<int> SmokeAsync(string packages) {
        var root = NuGetCommand.Root();
        var package = Directory.GetFiles(path: packages, searchPattern: "ByteTerrace.Puck.Cli.*.nupkg").Single();
        var version = Path.GetFileName(path: package)["ByteTerrace.Puck.Cli.".Length..^".nupkg".Length];
        var directory = CliScratchDirectories.CreateRunDirectory(scratchPrefix: "puck-package-smoke-");

        try {
            var executable = await InstallAsync(configFile: CandidateConfig(directory: directory, feed: packages), directory: directory, root: root, version: version);
            var project = Path.Combine(path1: root, path2: "src/Puck.Cli/Puck.Cli.csproj");

            await CliProcess.RunCheckedAsync(arguments: ["nuget", "--help"], executable: executable, root: root);
            await CliProcess.RunCheckedAsync(arguments: ["nuget", "version"], executable: executable, root: root);
            await CliProcess.RunCheckedAsync(arguments: ["search", "PackAsTool", project, "-M", "0"], executable: executable, root: root);
            await CliProcess.RunCheckedAsync(arguments: ["declarations", Path.Combine(path1: root, path2: "src/Puck.Cli/NuGet/NuGetCommand.cs"), "--members"], executable: executable, root: root);
            // Workspace build-host packaging is exercised against a tiny standalone project.
            var probe = Path.Combine(path1: directory, path2: "probe");

            Directory.CreateDirectory(path: probe);
            File.WriteAllText(contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", path: Path.Combine(path1: probe, path2: "Probe.csproj"));
            File.WriteAllText(contents: "public sealed class Probe { public int Value() => 1; public int Read() => Value(); }", path: Path.Combine(path1: probe, path2: "Probe.cs"));
            await CliProcess.RunCheckedAsync(arguments: ["restore", Path.Combine(path1: probe, path2: "Probe.csproj"), "--configfile", Path.Combine(path1: root, path2: "nuget.config")], executable: "dotnet", root: root);
            await CliProcess.RunCheckedAsync(arguments: ["build", Path.Combine(path1: probe, path2: "Probe.csproj"), "-c", "Release", "--no-restore"], executable: "dotnet", root: root);
            var references = await CliProcess.RunCheckedAsync(arguments: ["references", "Value", "--project", Path.Combine(path1: probe, path2: "Probe.csproj")], capture: true, executable: executable, root: root);

            if (!references.Contains(comparisonType: StringComparison.Ordinal, value: "Probe.cs")) { throw new InvalidDataException(message: "The packaged Roslyn workspace host did not resolve the probe."); }
        } finally { if (Directory.Exists(path: directory)) { Directory.Delete(path: directory, recursive: true); } }
        return 0;
    }
    private static async Task<string> InstallAsync(string configFile, string directory, string root, string version) {
        var tool = Path.Combine(path1: directory, path2: "tool");

        await CliProcess.RunCheckedAsync(arguments: ["tool", "install", Package, "--version", version, "--tool-path", tool, "--configfile", configFile, "--no-http-cache"], executable: "dotnet", root: root);
        var executable = Path.Combine(path1: tool, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck"));
        var actual = (await CliProcess.RunCheckedAsync(arguments: ["--version"], capture: true, executable: executable, root: root)).Trim();

        if ((actual != version) && !actual.StartsWith(comparisonType: StringComparison.Ordinal, value: (version + "+"))) { throw new InvalidDataException(message: $"Installed CLI reports {actual}; expected {version}."); }
        Console.WriteLine(value: $"Puck CLI {actual}");
        return executable;
    }
    private static string CandidateConfig(string directory, string feed) {
        // An exclusive feed and a private cache keep a same-version official package from replacing the candidate.
        var path = Path.Combine(path1: directory, path2: "nuget.config");

        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement(name: "clear"), new XElement("add", new XAttribute(name: "key", value: "candidate"), new XAttribute(name: "value", value: Path.GetFullPath(path: feed)))),
            new XElement("config", new XElement("add", new XAttribute(name: "key", value: "globalPackagesFolder"), new XAttribute(name: "value", value: Path.Combine(path1: directory, path2: "packages"))))))
            .Save(fileName: path);
        return path;
    }
}
