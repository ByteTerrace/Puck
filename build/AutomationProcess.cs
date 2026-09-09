using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck;

// SDK-only process and file operations shared by the bootstrap/orchestration apps.
// Commands are argument vectors; credentials may travel on stdin, never in a shell expression.
internal static class AutomationProcess {
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? directory = null, bool capture = false, string? input = null) {
        var command = arguments.ToList();

        if (OperatingSystem.IsWindows() && (executable is "az" or "npm")) {
            var launcher = FindCommand(name: (executable + ".cmd"));
            var home = Path.GetDirectoryName(path: launcher)!;

            if (executable == "az") {
                executable = Path.GetFullPath(path: Path.Combine(path1: home, path2: "../python.exe"));
                command.InsertRange(collection: ["-I", "-B", "-X", "utf8", "-m", "azure.cli"], index: 0);
            } else {
                executable = Path.Combine(path1: home, path2: "node.exe");
                command.Insert(index: 0, item: Path.Combine(path1: home, path2: "node_modules/npm/bin/npm-cli.js"));
            }
        }
        var info = new ProcessStartInfo(fileName: executable) {
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = (input is not null),
            UseShellExecute = false,
            WorkingDirectory = (directory ?? (RepositoryPaths.FindRoot() ?? Environment.CurrentDirectory)),
        };

        if (capture) { info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8; }
        foreach (var argument in command) { info.ArgumentList.Add(item: argument); }
        using var process = (Process.Start(startInfo: info) ?? throw new IOException(message: $"Cannot start {executable}."));
        var output = (capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(result: ""));
        var errors = (capture ? process.StandardError.ReadToEndAsync() : Task.FromResult(result: ""));

        if (input is not null) { await process.StandardInput.WriteAsync(value: input); process.StandardInput.Close(); }
        await process.WaitForExitAsync();
        var text = await output;
        var error = await errors;
        // Captured output can contain credentials; do not repeat it on a failed command.
        if (process.ExitCode != 0) { throw new IOException(message: $"{Path.GetFileName(path: executable)} exited with code {process.ExitCode}."); }
        if (capture && !string.IsNullOrWhiteSpace(value: error)) { Console.Error.WriteLine(value: error); }
        return text.Trim();
    }
    internal static async Task<string> PuckAsync(params string[] arguments) {
        var root = RepositoryPaths.FindRoot()!;
        var local = Path.Combine(path1: root, path2: ".tmp/puck-ci", path3: (OperatingSystem.IsWindows() ? "puck.exe" : "puck"));
        if (!File.Exists(local)) {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") {
                throw new FileNotFoundException("Install this run's CLI artifact before invoking deployment automation; CI consumers cannot bootstrap another build.");
            }
            await RunAsync("dotnet", ["run", "-c", "Release", "--file", Path.Combine(root, "build/Toolchain.cs"), "--", "setup"]);
        }
        return await RunAsync(executable: local, arguments: arguments);
    }
    internal static JsonNode Read(string path) {
        return (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"Empty JSON: {path}"));
    }
    internal static void Write(string path, JsonNode value) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
        File.WriteAllText(path: path, contents: (value.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n"));
    }
    internal static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(path: destination);
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: destination, path2: Path.GetRelativePath(path: file, relativeTo: source));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(destFileName: target, sourceFileName: file);
        }
    }

    private static string FindCommand(string name) {
        foreach (var directory in (Environment.GetEnvironmentVariable(variable: "PATH") ?? "").Split(Path.PathSeparator)) {
            var path = Path.Combine(path1: directory.Trim(trimChar: '"'), path2: name);

            if (File.Exists(path: path)) { return path; }
        }
        throw new FileNotFoundException(message: $"Cannot find {name} on PATH.");
    }
}
