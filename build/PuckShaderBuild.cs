using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Hosts one project's shader build: runs <c>Puck.Shaders.Generator compile</c> or <c>check</c> over the project's
/// outputs and answers the compiler's core requests from MSBuild's own core budget (<see cref="IBuildEngine9"/>), so
/// every shader project of a parallel build shares the machine's cores instead of each starting its own number of
/// compilers. Everything else the build does, it does in the generator, through the one <c>ShaderCompiler</c>.
/// </summary>
/// <remarks>
/// The protocol is line-based. On standard output, <c>##puck-cores request &lt;n&gt;</c> asks for up to n more cores,
/// answered on standard input by <c>granted &lt;k&gt;</c>, and <c>##puck-cores release &lt;n&gt;</c> returns cores.
/// Requests are served on a thread of their own, since a later request waits until another holder releases a core,
/// while releases are applied at once. Every other line is a message, and a line in MSBuild's canonical error format is
/// logged as an error. Whatever the build still holds when the compiler exits, a late grant included, is released
/// before the task returns. Cancelling the build kills the compiler's process tree.
/// </remarks>
public sealed class PuckShaderBuild : Task, ICancelableTask {
    private const string Prefix = "##puck-cores ";

    private readonly object m_gate = new object();
    private Process m_process;
    private bool m_cancelled;
    private int m_held;

    /// <summary>The <c>dotnet</c> host that runs the generator.</summary>
    [Required]
    public string Host { get; set; } = "";
    /// <summary>The generator assembly.</summary>
    [Required]
    public string Tool { get; set; } = "";
    /// <summary><c>compile</c> or <c>check</c>.</summary>
    [Required]
    public string Mode { get; set; } = "";
    /// <summary>The project's directory.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = "";
    /// <summary>The project's shader publication lock.</summary>
    [Required]
    public string LockFile { get; set; } = "";
    /// <summary>Where the request lines are written for the generator to read.</summary>
    [Required]
    public string RequestFile { get; set; } = "";
    /// <summary>Every output, with its <c>Stage</c>, <c>Backend</c> (a <c>ShaderTarget</c>) and <c>SourcePath</c> metadata.</summary>
    [Required]
    public ITaskItem[] Outputs { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The shader cache directory, or empty for the compiler's per-user default.</summary>
    public string CacheDirectory { get; set; } = "";
    /// <summary>The <c>dxc</c> command.</summary>
    public string DxcCommand { get; set; } = "";

    /// <inheritdoc/>
    public override bool Execute() {
        var request = new StringBuilder();

        foreach (var output in Outputs) {
            request.Append(output.GetMetadata("Stage")).Append('\t').Append(output.GetMetadata("Backend")).Append('\t')
                .Append(Path.GetFullPath(output.GetMetadata("SourcePath"))).Append('\t').Append(output.GetMetadata("FullPath")).Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(RequestFile)));
        File.WriteAllText(RequestFile, request.ToString());

        var start = new ProcessStartInfo(Host) {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = ProjectDirectory,
        };

        var arguments = new StringBuilder();

        foreach (var argument in new[] { Tool, Mode, "--project", ProjectDirectory, "--request", Path.GetFullPath(RequestFile), "--lock", LockFile, "--cache", CacheDirectory, "--dxc", DxcCommand }) {
            arguments.Append(arguments.Length == 0 ? "" : " ").Append(Quote(argument));
        }
        start.Arguments = arguments.ToString();

        var requests = new BlockingCollection<int>();
        var server = new Thread(() => Serve(requests)) { IsBackground = true, Name = "Puck shader cores" };

        lock (m_gate) {
            if (m_cancelled) {
                return false;
            }
            m_process = Process.Start(start);
        }
        try {
            m_process.ErrorDataReceived += (_, received) => { if (received.Data != null) { Log.LogMessageFromText(received.Data, MessageImportance.High); } };
            m_process.BeginErrorReadLine();
            server.Start();

            string line;

            while ((line = m_process.StandardOutput.ReadLine()) != null) {
                if (line.StartsWith(Prefix + "request ", StringComparison.Ordinal)) {
                    requests.Add(int.Parse(line.Substring((Prefix + "request ").Length)));
                } else if (line.StartsWith(Prefix + "release ", StringComparison.Ordinal)) {
                    Release(int.Parse(line.Substring((Prefix + "release ").Length)));
                } else {
                    Log.LogMessageFromText(line, line.StartsWith("Compiled ", StringComparison.Ordinal) || (line.IndexOf(" shader output(s): ", StringComparison.Ordinal) >= 0) ? MessageImportance.High : MessageImportance.Normal);
                }
            }
            m_process.WaitForExit();
        } finally {
            requests.CompleteAdding();
            // Release before joining: a request the compiler abandoned can still be waiting on the server thread for a
            // core, and the cores this task holds may be the only ones that can answer it. Whatever it is granted
            // meanwhile is released after the join.
            ReleaseHeld();
            if (server.IsAlive) { server.Join(); }
            ReleaseHeld();
        }

        return ((m_process.ExitCode == 0) && !Log.HasLoggedErrors && !m_cancelled);
    }

    // Grants each request in order. A first request never waits; a later one waits for a core to free, which a release
    // the reader applies meanwhile can be. Without a core budget (an engine older than IBuildEngine9) the build runs on
    // the one core it has.
    private void Serve(BlockingCollection<int> requests) {
        var first = true;

        foreach (var count in requests.GetConsumingEnumerable()) {
            int granted;

            try {
                if (BuildEngine is IBuildEngine9 engine) {
                    granted = engine.RequestCores(count);
                    lock (m_gate) { m_held += granted; }
                } else {
                    granted = (first ? 1 : 0);
                }
            } catch (Exception exception) {
                Log.LogMessage(MessageImportance.Low, "Shader core request ended: {0}", exception.Message);
                granted = 0;
            }
            first = false;
            if (granted == 0) {
                // Nothing to grant: leave the compiler waiting until it exits or is killed.
                continue;
            }
            try {
                m_process.StandardInput.WriteLine("granted " + granted);
                m_process.StandardInput.Flush();
            } catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException || exception is InvalidOperationException) {
                // The compiler has exited; whatever it was granted is released when the task returns.
            }
        }
    }
    private void ReleaseHeld() {
        lock (m_gate) {
            if (m_held > 0) {
                ((IBuildEngine9)BuildEngine).ReleaseCores(m_held);
                m_held = 0;
            }
        }
    }
    private void Release(int count) {
        lock (m_gate) {
            count = Math.Min(count, m_held);
            if (count <= 0) { return; }
            ((IBuildEngine9)BuildEngine).ReleaseCores(count);
            m_held -= count;
        }
    }

    /// <summary>Kills the compiler's process tree; <see cref="Execute"/> then returns false.</summary>
    public void Cancel() {
        lock (m_gate) {
            m_cancelled = true;
            try {
                if ((m_process != null) && !m_process.HasExited) {
                    // Kill(bool) takes the compiler's own children with it; an engine on .NET Framework has only Kill().
                    var tree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });

                    if (tree != null) { tree.Invoke(m_process, new object[] { true }); } else { m_process.Kill(); }
                }
            } catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception || exception is System.Reflection.TargetInvocationException) { }
        }
    }

    // One argument as the C runtime's command-line parser reads it back: quoted, with each run of backslashes before a
    // quote, or before the closing quote, doubled.
    private static string Quote(string argument) {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument ?? "") {
            if (character == '\\') {
                backslashes++;
                continue;
            }
            quoted.Append('\\', (character == '"') ? ((backslashes * 2) + 1) : backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
