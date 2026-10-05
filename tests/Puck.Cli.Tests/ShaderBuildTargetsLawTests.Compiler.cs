using System.Diagnostics;
using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class ShaderBuildTargetsLawTests {
    [Fact]
    public async Task CompilerWorkersOverlapWithinTheRequestedBoundAndPublishOnlyAfterJoining() {
        using var fixture = new Fixture();
        fixture.ParallelProject(mode: "hold");
        var build = Task.Run(function: () => fixture.Run(target: "Build", properties: ["PuckShaderCompileJobs=2"]), cancellationToken: TestContext.Current.CancellationToken);
        try {
            _ = WaitFor(find: () => (fixture.Started().Length >= 2 || build.IsCompleted ? "two compiler children or an early exit" : null));
            Assert.False(condition: build.IsCompleted, userMessage: "The compiler workers did not hold at the real-process barrier.");
            Assert.Equal(expected: 2, actual: fixture.Started().Length);
            Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
            fixture.Write(path: "release", text: "release every compiler child");
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
    public async Task AFailedCompilerCancelsAndJoinsItsPeerBeforeRemovingTemporaryOutputs() {
        using var fixture = new Fixture();
        fixture.ParallelProject(mode: "fail-peer");
        var run = Task.Run(function: () => fixture.Run(target: "Build", properties: ["PuckShaderCompileJobs=2"]), cancellationToken: TestContext.Current.CancellationToken);
        try {
            _ = WaitFor(find: () => (fixture.Started().Length >= 2 || run.IsCompleted ? "two compiler peers or an early exit" : null));
            // Cancellation has an observed process barrier. If it is missing, release the held peer after the
            // liveness bound so the law can inspect the wrong admission/completion rather than hang forever.
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(value: 10), TestContext.Current.CancellationToken)) != run) { fixture.Write(path: "release", text: "watchdog release"); }
        } finally {
            if (!run.IsCompleted) { fixture.Write(path: "release", text: "release the test-owned compiler peer"); }
        }
        var build = await run;
        Assert.NotEqual(expected: 0, actual: build.ExitCode);
        Assert.False(condition: build.TimedOut, userMessage: build.Stdout + build.Stderr);
        Assert.Contains(expectedSubstring: "deliberate compiler failure", actualString: build.Stdout + build.Stderr);
        Assert.Equal(expected: 2, actual: fixture.Started().Length);
        fixture.RequireChildrenExited();
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }

    [Fact]
    public void CancellationStopsAdmissionAndJoinsTheActualCompilerChildren() {
        using var fixture = new Fixture();
        fixture.ParallelProject(mode: "hold");
        // The wrapper compiles the production task's exact source and calls its public cancellation seam after two
        // real children reach the barrier. A watchdog releases them only if cancellation fails, allowing an intended
        // assertion failure instead of an indefinitely hung test process.
        var production = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "build/PuckCompileShaderBytecode.cs"));
        fixture.Write(path: "cancel-task.cs", text: "using System.Linq;\n" + production + """

            public sealed class CancelCompilerFixture : Microsoft.Build.Utilities.Task {
                public override bool Execute() {
                    var compiler = new PuckCompileShaderBytecode { BuildEngine = BuildEngine, WorkingDirectory = System.Environment.CurrentDirectory, Token = "cancel-fixture", Jobs = 2 };
                    compiler.BytecodeFiles = System.IO.Directory.GetFiles("Assets/Shaders", "*.comp.hlsl").Select(path => {
                        var item = new Microsoft.Build.Utilities.TaskItem(System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(path), ".spv"));
                        item.SetMetadata("SourcePath", System.IO.Path.GetFullPath(path));
                        item.SetMetadata("Recipe", "\"" + System.IO.File.ReadAllText("compiler-path.txt") + "\"");
                        return (Microsoft.Build.Framework.ITaskItem)item;
                    }).ToArray();
                    using (var completed = new System.Threading.ManualResetEventSlim()) {
                        var cancellation = new System.Threading.Thread(() => {
                            var deadline = System.Diagnostics.Stopwatch.StartNew();
                            while (System.IO.Directory.GetFiles("started").Length < 2 && !completed.IsSet && deadline.Elapsed < System.TimeSpan.FromSeconds(30)) { System.Threading.Thread.Sleep(10); }
                            if (!completed.IsSet) { compiler.Cancel(); }
                            if (!completed.Wait(System.TimeSpan.FromSeconds(10))) { System.IO.File.WriteAllText("release", "watchdog release"); }
                        });
                        cancellation.Start();
                        try { return compiler.Execute(); }
                        finally { completed.Set(); cancellation.Join(); }
                    }
                }
            }
            """);
        var project = XDocument.Load(uri: fixture.PathOf(path: "fixture.proj"));
        project.Root!.Add(new XElement("UsingTask", new XAttribute("TaskName", "CancelCompilerFixture"), new XAttribute("TaskFactory", "RoslynCodeTaskFactory"), new XAttribute("AssemblyFile", "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll"), new XElement("Task", new XElement("Reference", new XAttribute("Include", "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll")), new XElement("Using", new XAttribute("Namespace", "System.Linq")), new XElement("Code", new XAttribute("Type", "Class"), new XAttribute("Language", "cs"), new XAttribute("Source", "cancel-task.cs")))));
        project.Root.Add(new XElement("Target", new XAttribute("Name", "CancelCompilers"), new XElement("CancelCompilerFixture")));
        fixture.Write(path: "fixture.proj", text: project.ToString());
        var build = fixture.Run(target: "CancelCompilers");
        Assert.NotEqual(expected: 0, actual: build.ExitCode);
        Assert.False(condition: build.TimedOut, userMessage: build.Stdout + build.Stderr);
        Assert.Equal(expected: 2, actual: fixture.Started().Length);
        fixture.RequireChildrenExited();
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }

    internal sealed partial class Fixture {
        private void WriteCompiler(string mode) {
            var compiler = (OperatingSystem.IsWindows() ? "fixture-dxc.cmd" : "fixture-dxc.sh");
            CompilerPath = Puck.Abstractions.PuckPaths.Normalize(path: PathOf(path: compiler));
            Write(path: "compiler-path.txt", text: CompilerPath);
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
            foreach (var name in new[] { "a", "b", "c" }) { Write(path: $"Assets/Shaders/{name}.comp.hlsl", text: "source"); }
            ShaderProject(body: "<ItemGroup><ComputeShaderSource Include=\"Assets/Shaders/*.comp.hlsl\" /></ItemGroup><Target Name=\"ResolveProjectReferences\" />", dxc: mode);
        }
        public string[] Started() => Directory.GetFiles(path: PathOf(path: "started"));
        public void RequireChildrenExited() {
            foreach (var path in Started()) {
                var pid = int.Parse(s: Path.GetFileName(path), provider: System.Globalization.CultureInfo.InvariantCulture);
                try {
                    using var process = Process.GetProcessById(processId: pid);
                    Assert.True(condition: process.HasExited, userMessage: $"Compiler child {pid} is still alive after the task returned.");
                } catch (ArgumentException) { }
            }
        }

        private const string WindowsCompiler = """
            if ($args -contains '--version') { Write-Output 'fixture compiler'; exit 0 }
            $fo = [Array]::IndexOf($args, '-Fo')
            $output = $args[$fo + 1]
            $source = $args[$args.Length - 1]
            $mode = [IO.File]::ReadAllText('compiler-mode.txt')
            [IO.File]::WriteAllText("started/$PID", $source)
            if ($mode -eq 'hold' -or $mode -eq 'fail-peer') {
                while (!(Test-Path release) -and ($mode -ne 'fail-peer' -or (Get-ChildItem started).Count -lt 2)) { Start-Sleep -Milliseconds 10 }
                if ($mode -eq 'fail-peer' -and $source.EndsWith('a.comp.hlsl')) {
                    [IO.File]::WriteAllText($output, 'unpublished failure')
                    Write-Error 'deliberate compiler failure'
                    exit 1
                }
                if ($mode -eq 'fail-peer') { while (!(Test-Path release)) { Start-Sleep -Milliseconds 10 } }
            }
            [IO.File]::WriteAllText($output, $(if ($mode -eq 'process') { "compiled by process $PID" } else { 'compiled bytecode' }))
            $stream = $null
            while ($null -eq $stream) {
                try { $stream = [IO.File]::Open('compiles.txt', [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None) }
                catch [IO.IOException] { Start-Sleep -Milliseconds 10 }
            }
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($output + "`n"); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
            if ($mode -eq 'normal' -and $source.EndsWith('b.comp.hlsl')) { Write-Error 'deliberate compiler failure'; exit 1 }
            exit 0
            """;
        private const string UnixCompiler = """
            #!/bin/sh
            if [ "$1" = '--version' ]; then printf 'fixture compiler\n'; exit 0; fi
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
                    case "$source" in *a.comp.hlsl) printf 'unpublished failure' > "$output"; printf 'deliberate compiler failure\n' >&2; exit 1;; esac
                    while [ ! -f release ]; do sleep 0.01; done
                fi
            fi
            if [ "$mode" = 'process' ]; then printf 'compiled by process %s' "$$" > "$output"; else printf 'compiled bytecode' > "$output"; fi
            while ! mkdir compiler-log-lock 2>/dev/null; do sleep 0.01; done
            printf '%s\n' "$output" >> compiles.txt
            rmdir compiler-log-lock
            if [ "$mode" = 'normal' ]; then case "$source" in *b.comp.hlsl) printf 'deliberate compiler failure\n' >&2; exit 1;; esac; fi
            """;
    }
}
