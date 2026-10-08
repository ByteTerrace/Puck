using System.Diagnostics;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class ShaderBuildTargetsLawTests {
    [Fact]
    public async Task CompilesRunConcurrentlyOnTheCoresTheBuildEngineGrants() {
        if (Environment.ProcessorCount < 2) {
            Assert.Skip(reason: "MSBuild grants one core on a one-processor machine, so no two compiles can overlap.");
        }

        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "hold");
        var build = Task.Run(function: () => fixture.Run(target: "Build"), cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length >= 2) || build.IsCompleted) ? "two compiler children or an early exit" : null));
            Assert.False(condition: build.IsCompleted, userMessage: "The compilers did not hold at the real-process barrier.");
            Assert.True(condition: (fixture.Started().Length >= 2));
            Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
        } finally {
            fixture.Write(path: "release", text: "release every compiler child");
            _ = await build;
        }
        fixture.RequireSuccess(run: await build);
        Assert.Equal(expected: 3, actual: fixture.Started().Length);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
    [Fact]
    public async Task AFailedCompileCancelsItsPeerAndPublishesNothing() {
        if (Environment.ProcessorCount < 2) {
            Assert.Skip(reason: "MSBuild grants one core on a one-processor machine, so no peer runs beside the failure.");
        }

        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "fail-peer");
        var run = Task.Run(function: () => fixture.Run(target: "Build"), cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length >= 2) || run.IsCompleted) ? "two compiler peers or an early exit" : null));
            // Cancellation has an observed process barrier. If it is missing, release the held peer after the
            // liveness bound so the law can inspect the wrong admission/completion rather than hang forever.
            if (await Task.WhenAny(task1: run, task2: Task.Delay(TimeSpan.FromSeconds(value: 30), TestContext.Current.CancellationToken)) != run) { fixture.Write(path: "release", text: "watchdog release"); }
        } finally {
            if (!run.IsCompleted) { fixture.Write(path: "release", text: "release the test-owned compiler peer"); }
        }
        var build = await run;

        Assert.NotEqual(expected: 0, actual: build.ExitCode);
        Assert.False(condition: build.TimedOut, userMessage: (build.Stdout + build.Stderr));
        Assert.Contains(expectedSubstring: "deliberate compiler failure", actualString: (build.Stdout + build.Stderr));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "release")), userMessage: "The failing compile did not cancel its held peer; the watchdog released it.");
        fixture.RequireChildrenExited();
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }

    internal sealed partial class Fixture {
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
}
