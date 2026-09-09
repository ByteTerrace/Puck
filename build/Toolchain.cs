#!/usr/bin/env dotnet
#:property PublishAot=false

using System.Text.Json.Nodes;
using System.Xml.Linq;
using static Puck.AutomationProcess;

try {
    return await Toolchain.ExecuteAsync(args: args);
} catch (Exception error) {
    Console.Error.WriteLine(value: $"toolchain: {error.Message}");
    return 1;
}

internal static class Toolchain {
    private const string Package = "byteterrace.puck.cli";

    internal static async Task<int> ExecuteAsync(string[] args) {
        var root = (Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));
        var manifestPath = Path.Combine(path1: root, path2: ".config/dotnet-tools.json");
        var bootstrapPath = Path.Combine(path1: root, path2: ".config/puck-bootstrap.json");
        var config = Path.Combine(path1: root, path2: "nuget.config");
        var project = Path.Combine(path1: root, path2: "src/Puck.Cli/Puck.Cli.csproj");
        var manifest = Read(path: manifestPath);
        var tools = manifest["tools"]!.AsObject();

        if (args is ["install-candidate", var candidateFeed]) {
            var candidatePackage = Directory.GetFiles(candidateFeed, "ByteTerrace.Puck.Cli.*.nupkg").Single();
            var version = Path.GetFileName(candidatePackage)["ByteTerrace.Puck.Cli.".Length..^".nupkg".Length];
            var directory = Path.Combine(root, ".tmp/puck-ci");
            if (Directory.Exists(directory)) { throw new IOException($"Use a fresh tool directory: {directory}"); }
            await RunAsync("dotnet", ["tool", "install", Package, "--version", version, "--tool-path", directory, "--configfile", CandidateConfig(root, candidateFeed), "--no-http-cache"]);
            await VerifyVersionAsync(Path.Combine(directory, OperatingSystem.IsWindows() ? "puck.exe" : "puck"), version);
            ExportPath(directory);
            return 0;
        }

        if (args is ["smoke", var feed]) {
            var smokePackage = Directory.GetFiles(path: feed, searchPattern: "ByteTerrace.Puck.Cli.*.nupkg").Single();
            var smokeVersion = Path.GetFileName(path: smokePackage)[("ByteTerrace.Puck.Cli.".Length)..^".nupkg".Length];
            var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-package-smoke-{Guid.NewGuid():N}");

            try {
                await RunAsync(executable: "dotnet", arguments: ["tool", "install", Package, "--version", smokeVersion, "--tool-path", directory, "--configfile", CandidateConfig(root, feed), "--no-http-cache"]);
                var executable = Path.Combine(path1: directory, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck"));

                await VerifyVersionAsync(executable: executable, expected: smokeVersion);
                await RunAsync(executable: executable, arguments: ["nuget", "--help"]);
                await RunAsync(executable: executable, arguments: ["nuget", "version"]);
                await RunAsync(executable: executable, arguments: ["search", "PackAsTool", project, "-M", "0"]);
                await RunAsync(executable: executable, arguments: ["declarations", Path.Combine(path1: root, path2: "src/Puck.Cli/NuGet/NuGetCommand.cs"), "--members"]);
                // Exercise workspace build-host packaging against a tiny standalone project.
                var probe = Path.Combine(path1: directory, path2: "probe");

                Directory.CreateDirectory(path: probe);
                File.WriteAllText(path: Path.Combine(path1: probe, path2: "Probe.csproj"), contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(path: Path.Combine(path1: probe, path2: "Probe.cs"), contents: "public sealed class Probe { public int Value() => 1; public int Read() => Value(); }");
                await RunAsync(executable: "dotnet", arguments: ["restore", Path.Combine(path1: probe, path2: "Probe.csproj"), "--configfile", config]);
                await RunAsync(executable: "dotnet", arguments: ["build", Path.Combine(path1: probe, path2: "Probe.csproj"), "-c", "Release", "--no-restore"]);
                var references = await RunAsync(executable: executable, arguments: ["references", "Value", "--project", Path.Combine(path1: probe, path2: "Probe.csproj")], capture: true);

                if (!references.Contains(comparisonType: StringComparison.Ordinal, value: "Probe.cs")) { throw new InvalidDataException(message: "The packaged Roslyn workspace host did not resolve the probe."); }
            } finally { if (Directory.Exists(path: directory)) { Directory.Delete(path: directory, recursive: true); } }
            return 0;
        }
        if (args is ["version"]) {
            var version = XDocument.Load(uri: Path.Combine(path1: root, path2: "build/Packaging.targets")).Descendants(name: "Version").Single().Value;

            Console.WriteLine(value: version);
            if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } output) {
                File.AppendAllText(contents: $"version={version}\n", path: output);
            }
            return 0;
        }
        if (args is ["pin", var versionToPin]) {
            if (!System.Text.RegularExpressions.Regex.IsMatch(input: versionToPin, pattern: "\\A[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9a-z.-]+)?\\z")) {
                throw new ArgumentException(message: "Expected an exact release version.");
            }
            var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-official-install-{Guid.NewGuid():N}");

            try {
                // Test the published package in an empty location before changing tracked configuration.
                await RunAsync(executable: "dotnet", arguments: ["tool", "install", Package, "--version", versionToPin, "--tool-path", directory, "--configfile", config, "--no-http-cache"]);
                await VerifyVersionAsync(executable: Path.Combine(path1: directory, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck")), expected: versionToPin);
                await RunAsync(executable: Path.Combine(path1: directory, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck")), arguments: ["nuget", "--help"]);
                tools[Package] = new JsonObject { ["version"] = versionToPin, ["commands"] = new JsonArray("puck"), ["rollForward"] = false };
                Write(path: manifestPath, value: manifest);
                Write(path: bootstrapPath, value: new JsonObject { ["enabled"] = false });
            } finally {
                if (Directory.Exists(path: directory)) { Directory.Delete(path: directory, recursive: true); }
            }
            return 0;
        }
        if (args is not ["setup" or "candidate"] and not ["setup" or "candidate", _]) {
            Console.WriteLine(value: "dotnet run -c Release --file build/Toolchain.cs -- <setup [tool-directory]|candidate [tool-directory]|install-candidate PACKAGE_DIRECTORY|version|pin VERSION|smoke PACKAGE_DIRECTORY>");
            return ((args is ["--help" or "-h"]) ? 0 : 2);
        }
        var candidate = (args[0] == "candidate");
        var toolDirectory = ((args.Length == 2) ? Path.GetFullPath(path: args[1]) : Path.Combine(path1: root, path2: ".tmp/puck-ci"));

        if (Directory.Exists(path: toolDirectory)) { throw new IOException(message: $"Use a fresh tool directory: {toolDirectory}"); }
        if (!candidate && tools.ContainsKey(propertyName: Package)) {
            var pinned = ((string)tools[Package]!["version"]!);

            await RunAsync(executable: "dotnet", arguments: ["tool", "install", Package, "--version", pinned, "--tool-path", toolDirectory, "--configfile", config]);
            await VerifyVersionAsync(executable: Path.Combine(path1: toolDirectory, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck")), expected: pinned);
            ExportPath(directory: toolDirectory);
            return 0;
        }
        if (!candidate && (((bool?)Read(path: bootstrapPath)["enabled"]) != true)) {
            throw new InvalidDataException(message: "Puck CLI has no official pin and initial bootstrapping is disabled.");
        }
        Console.WriteLine(value: (candidate ? "Installing the candidate CLI for source-dependent operations." : "Initial CLI bootstrap: no official release is pinned yet."));
        var packages = Path.Combine(path1: root, path2: ".tmp", path3: $"puck-tool-pack-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: packages);
        // The bootstrap uses the SDK directly and never calls puck to manufacture puck.
        await RunAsync(executable: "dotnet", arguments: ["restore", project, "--locked-mode"]);
        await RunAsync(executable: "dotnet", arguments: ["pack", project, "-c", "Release", "--no-restore", "--output", packages]);
        var package = Directory.GetFiles(path: packages, searchPattern: "*.nupkg").Single();
        var packedVersion = Path.GetFileName(path: package)[("ByteTerrace.Puck.Cli.".Length)..^".nupkg".Length];
        // Isolate candidate packages from the official tool manifest and NuGet's local-tool cache.
        await RunAsync(executable: "dotnet", arguments: ["tool", "install", Package, "--version", packedVersion, "--tool-path", toolDirectory, "--configfile", CandidateConfig(root, packages), "--no-http-cache"]);
        await VerifyVersionAsync(executable: Path.Combine(path1: toolDirectory, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck")), expected: packedVersion);
        ExportPath(directory: toolDirectory);
        return 0;
    }

    private static string CandidateConfig(string root, string feed) {
        // An exclusive feed and private cache prevent a same-version official package replacing the artifact.
        var directory = Path.Combine(root, ".tmp", "puck-package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "nuget.config");
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", Path.GetFullPath(feed)))),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", Path.Combine(directory, "packages"))))))
            .Save(path);
        return path;
    }
    private static void ExportPath(string directory) {
        Console.WriteLine(value: $"Puck CLI installed at {directory}");
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_PATH") is { Length: > 0 } path) {
            File.AppendAllText(contents: (directory + "\n"), path: path);
        }
    }
    private static async Task VerifyVersionAsync(string executable, string expected) {
        var actual = await RunAsync(executable: executable, arguments: ["--version"], capture: true);

        if ((actual != expected) && !actual.StartsWith(comparisonType: StringComparison.Ordinal, value: (expected + "+"))) {
            throw new InvalidDataException(message: $"Installed CLI reports {actual}; expected {expected}.");
        }
        Console.WriteLine(value: $"Puck CLI {actual}");
    }
}
