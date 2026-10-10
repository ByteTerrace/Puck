using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Testing;

/// <summary>An isolated project over the shipped shader build targets: the real build host, the real shader build in
/// the generator, and a CPU-only stand-in for DXC that records every compile it runs and can hold or fail one. It
/// compiles into a shader cache of its own unless a law shares one on purpose.</summary>
internal sealed partial class ShaderBuildFixture : IDisposable {
    /// <summary>The stand-in mode that compiles every source without holding or failing.</summary>
    public const string FakeDxc = "normal";
    /// <summary>The project body that compiles every compute source under <c>Assets/Shaders</c> and resolves no
    /// project reference.</summary>
    public const string Sources = """
        <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
        <Target Name="ResolveProjectReferences" />
        """;

    // The generator the targets run is the one this law's own build produced, in its configuration.
    private static readonly string Configuration = (typeof(ShaderBuildFixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release");

    private readonly TemporaryDirectory? m_directory;

    public ShaderBuildFixture(string? root = null) {
        m_directory = ((root is null) ? new TemporaryDirectory(prefix: "puck-shader-targets-") : null);
        Root = (root ?? m_directory!.RootPath);
        _ = Directory.CreateDirectory(path: Root);
        _ = Directory.CreateDirectory(path: PathOf(path: "started"));
        // A fixture over a tree that already carries the SDK pin (a law proof's clone of a pinned checkout) keeps it,
        // so the fixture leaves that tree as it found it.
        if (!File.Exists(path: PathOf(path: "global.json"))) {
            CliScratchDirectories.PinSdk(directory: Root);
        }
    }

    public string Root { get; }
    public string CompilerPath { get; private set; } = "";

    public string PathOf(string path) => Path.Combine(path1: Root, path2: path);
    public void Write(string path, string text) {
        var fullPath = PathOf(path: path);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
        File.WriteAllText(contents: text, path: fullPath);
    }
    // The compiles the fake DXC finished in this fixture.
    public int Compiles() => CompiledSources().Length;
    // The stage source each finished compile read, by file name, in completion order.
    public string[] CompiledSources() => (File.Exists(path: PathOf(path: "compiles.txt"))
        ? [.. File.ReadAllLines(path: PathOf(path: "compiles.txt")).Where(predicate: static line => (line.Length != 0)).Select(selector: static line => Path.GetFileName(path: line.Trim()))]
        : []);
    public void ShaderProject(string body, string buildDependencies = "ResolveProjectReferences", string dxc = FakeDxc) {
        var project = XElement.Parse(text: $"<Project>{body}</Project>");

        WriteCompiler(mode: dxc);
        // The output items are evaluated by the import, so the compiler, the backends and the fixture's own cache are
        // selected before it.
        project.AddFirst(content: new XElement("PropertyGroup",
            new XElement(content: "false", name: "PuckComputeShaderDxilEnabled"),
            new XElement("DxcCommand", CompilerPath),
            new XElement("PuckShaderCacheDirectory", PathOf(path: "cache"))));
        project.Add(content: new XElement(name: "Import", content: new XAttribute(name: "Project", value: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"))));
        project.Add(content: new XElement(name: "Target", content: [new XAttribute(name: "Name", value: "Build"), new XAttribute(name: "DependsOnTargets", value: buildDependencies)]));
        Write(path: "fixture.proj", text: project.ToString());
    }
    public CliProcessResult Run(string target, string[]? properties = null, ManualResetEventSlim? waiting = null) => CliProcess.RunCaptured(
        arguments: ["msbuild", "--disable-build-servers", PathOf(path: "fixture.proj"), "-nologo", "-v:n", "-m:4", "-nodeReuse:false", $"-t:{target}", $"-p:Configuration={Configuration}", .. (properties ?? []).Select(selector: static property => $"-p:{property}")],
        cancellationToken: TestContext.Current.CancellationToken,
        fileName: "dotnet",
        input: string.Empty,
        onOutput: output => {
            if (output.Line.Contains(comparisonType: StringComparison.Ordinal, value: "Waiting for another build's shader publication to finish") &&
                output.Line.Contains(value: Path.GetFullPath(path: PathOf(path: "obj/shader-publish.lock")), comparisonType: StringComparison.OrdinalIgnoreCase)) {
                waiting?.Set();
            }
        },
        timeout: TimeSpan.FromMinutes(value: 2),
        workingDirectory: Root
    );
    // Evaluates the project, building nothing, and returns the file name of every item of one type it plans.
    public string[] Evaluate(string item, string[]? properties = null) {
        var run = CliProcess.RunCaptured(
            arguments: ["msbuild", "--disable-build-servers", PathOf(path: "fixture.proj"), "-nologo", "-nodeReuse:false", $"-getItem:{item}", $"-p:Configuration={Configuration}", .. (properties ?? []).Select(selector: static property => $"-p:{property}")],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(value: 2),
            workingDirectory: Root
        );

        RequireSuccess(run: run);

        using var document = System.Text.Json.JsonDocument.Parse(json: run.Stdout);

        return [.. (document.RootElement.GetProperty(propertyName: "Items").TryGetProperty(propertyName: item, value: out var items)
                ? items.EnumerateArray().Select(selector: static entry => Path.GetFileName(path: entry.GetProperty(propertyName: "Identity").GetString()!))
                : [])
            .Order(comparer: StringComparer.Ordinal)];
    }
    public void RequireSuccess(CliProcessResult run) => Assert.True(condition: (run.ExitCode == 0), userMessage: $"{run.Stdout}\n{run.Stderr}");
    public void Dispose() => m_directory?.Dispose();

    private void WriteCompiler(string mode) {
        var compiler = (OperatingSystem.IsWindows() ? "fixture-dxc.cmd" : "fixture-dxc.sh");

        CompilerPath = Puck.Abstractions.PuckPaths.Normalize(path: PathOf(path: compiler));
        Write(path: "compiler-mode.txt", text: mode);
        _ = Directory.CreateDirectory(path: PathOf(path: "started"));
        if (OperatingSystem.IsWindows()) {
            Write(path: "fixture-dxc.cmd", text: "@echo off\r\n\"%SystemRoot%/System32/WindowsPowerShell/v1.0/powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fixture-dxc.ps1\" %*\r\nexit /b %errorlevel%\r\n");
            Write(path: "fixture-dxc.ps1", text: WindowsCompiler);
        } else {
            Write(path: compiler, text: UnixCompiler);
            File.SetUnixFileMode(path: CompilerPath, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void ParallelProject(string mode) {
        foreach (var name in new[] { "a", "b", "c" }) { Write(path: $"Assets/Shaders/{name}.comp.hlsl", text: $"source {name}"); }
        ShaderProject(body: Sources, dxc: mode);
    }
    public string[] Started() => Directory.GetFiles(path: PathOf(path: "started"));
    public void RequireChildrenExited() {
        foreach (var path in Started()) {
            var pid = int.Parse(s: Path.GetFileName(path: path), provider: System.Globalization.CultureInfo.InvariantCulture);

            try {
                using var process = Process.GetProcessById(processId: pid);

                Assert.True(condition: process.HasExited, userMessage: $"Compiler child {pid} is still alive after the build returned.");
            } catch (ArgumentException) { }
        }
    }

    // The stand-in for DXC. The compiler runs it in the project directory over a snapshot of the stage source, so
    // the last argument is the snapshot's path, whose file name is the source's. It records each compile's source in
    // compiles.txt, writes bytes that depend on the source's text, and in its hold and fail modes meets the law at
    // a barrier.
    private const string WindowsCompiler = """
        $fo = [Array]::IndexOf($args, '-Fo')
        $output = $args[$fo + 1]
        $source = $args[$args.Length - 1]
        $mode = [IO.File]::ReadAllText('compiler-mode.txt')
        [IO.File]::WriteAllText("started/$PID", $source)
        if ($mode -eq 'hold' -or $mode -eq 'fail-peer') {
            while (!(Test-Path release) -and ($mode -ne 'fail-peer' -or (Get-ChildItem started).Count -lt 2)) { Start-Sleep -Milliseconds 10 }
            if ($mode -eq 'fail-peer' -and $source.EndsWith('a.comp.hlsl')) {
                [IO.File]::WriteAllText($output, 'unpublished failure')
                [Console]::Error.WriteLine("${source}:1:1: error: deliberate compiler failure")
                exit 1
            }
            if ($mode -eq 'fail-peer') { while (!(Test-Path release)) { Start-Sleep -Milliseconds 10 } }
        }
        [IO.File]::WriteAllText($output, 'compiled ' + [IO.File]::ReadAllText($source))
        $stream = $null
        while ($null -eq $stream) {
            try { $stream = [IO.File]::Open('compiles.txt', [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None) }
            catch [IO.IOException] { Start-Sleep -Milliseconds 10 }
        }
        try { $bytes = [Text.Encoding]::UTF8.GetBytes($source + "`n"); $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
        exit 0
        """;
    private const string UnixCompiler = """
        #!/bin/sh
        output=''
        while [ "$#" -gt 0 ]; do
            if [ "$1" = '-Fo' ]; then shift; output="$1"; else source="$1"; fi
            shift
        done
        mode=$(cat compiler-mode.txt)
        printf '%s' "$source" > "started/$$"
        if [ "$mode" = 'hold' ] || [ "$mode" = 'fail-peer' ]; then
            while [ ! -f release ]; do
                if [ "$mode" = 'fail-peer' ] && [ "$(find started -type f | wc -l)" -ge 2 ]; then break; fi
                sleep 0.01
            done
            if [ "$mode" = 'fail-peer' ]; then
                case "$source" in *a.comp.hlsl) printf 'unpublished failure' > "$output"; printf '%s:1:1: error: deliberate compiler failure\n' "$source" >&2; exit 1;; esac
                while [ ! -f release ]; do sleep 0.01; done
            fi
        fi
        { printf 'compiled '; cat "$source"; } > "$output"
        while ! mkdir compiler-log-lock 2>/dev/null; do sleep 0.01; done
        printf '%s\n' "$source" >> compiles.txt
        rmdir compiler-log-lock
        """;
}
