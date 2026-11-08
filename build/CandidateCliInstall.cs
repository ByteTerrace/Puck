#pragma warning disable IDE0005 // The same source is a standalone bootstrap without implicit imports.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Puck.Cli.NuGet;

// Compiled into Puck.Cli.Release and runnable as a file-based bootstrap before puck is installed. Both surfaces
// execute this implementation; the bootstrap supplies only process I/O in place of the CLI's process host.
public static class CandidateCliInstall {
    public const string Package = "byteterrace.puck.cli";

    public static async Task<string> InstallAsync(string configFile, string tool, string version, Func<string, IEnumerable<string>, Task<string>> run) {
        if (System.IO.Directory.Exists(path: tool) || File.Exists(path: tool)) { throw new IOException(message: $"Use a fresh tool directory: {tool}"); }
        _ = await run("dotnet", ["tool", "install", Package, "--version", version, "--tool-path", tool, "--configfile", configFile, "--no-http-cache"]).ConfigureAwait(false);
        var executable = Path.Combine(path1: tool, path2: (OperatingSystem.IsWindows() ? "puck.exe" : "puck"));
        var actual = (await run(executable, ["--version"]).ConfigureAwait(false)).Trim();
        if (actual != version && !actual.StartsWith(value: version + "+", comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"Installed CLI reports {actual}; expected {version}.");
        }
        Console.WriteLine(value: $"Puck CLI {actual}");
        return executable;
    }

    public static async Task<string> CandidateAsync(string feed, string tool, string directory, Func<string, IEnumerable<string>, Task<string>> run) {
        var package = System.IO.Directory.GetFiles(path: feed, searchPattern: "ByteTerrace.Puck.Cli.*.nupkg").Single();
        var version = Path.GetFileName(path: package)["ByteTerrace.Puck.Cli.".Length..^".nupkg".Length];
        _ = System.IO.Directory.CreateDirectory(path: directory);
        var config = Path.Combine(path1: directory, path2: "nuget.config");
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", Path.GetFullPath(path: feed)))),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", Path.GetFullPath(path: Path.Combine(path1: directory, path2: "packages")))))))
            .Save(fileName: config);
        return await InstallAsync(configFile: config, tool: tool, version: version, run: run).ConfigureAwait(false);
    }

    public static async Task<int> Main(string[] args) {
        if (args.Length != 2) { Console.Error.WriteLine(value: "Usage: CandidateCliInstall.cs <package-directory> <fresh-tool-directory>"); return 2; }
        var scratch = Path.Combine(path1: Path.GetTempPath(), path2: "puck-candidate-" + Guid.NewGuid().ToString(format: "N"));
        try {
            _ = await CandidateAsync(feed: args[0], tool: args[1], directory: scratch, run: RunAsync).ConfigureAwait(false);
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: exception.Message);
            return 1;
        } finally {
            // This invocation created the random scratch directory; no user-supplied path is removed.
            if (System.IO.Directory.Exists(path: scratch)) { System.IO.Directory.Delete(path: scratch, recursive: true); }
        }
    }

    private static async Task<string> RunAsync(string file, IEnumerable<string> arguments) {
        var start = new ProcessStartInfo(fileName: file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) { start.ArgumentList.Add(item: argument); }
        using var process = Process.Start(startInfo: start) ?? throw new IOException(message: $"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        Console.Error.Write(value: await error.ConfigureAwait(false));
        if (process.ExitCode != 0) { throw new IOException(message: $"{file} exited {process.ExitCode}: {text}"); }
        return text;
    }
}
