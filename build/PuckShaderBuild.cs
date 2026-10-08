using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
/// Only the first request reaches MSBuild: it is guaranteed not to wait for cores. Later requests are refused, since
/// MSBuild offers no way to cancel a blocked request and joining one can prevent the task from returning. The compiler
/// finishes on its initial grant, returning unused cores as work completes. Every other line is a message, and a line
/// in MSBuild's canonical error format is logged as an error. Cancellation asks the generator to join its compilers;
/// a generator that does not exit within ten seconds has its process tree killed.
/// </remarks>
public sealed class PuckShaderBuild : Task, ICancelableTask {
    private const string Prefix = "##puck-cores ";

    private readonly object m_gate = new object();

    private Process m_process;
    private volatile bool m_cancelled;
    private int m_held;
    private bool m_requested;

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
    /// <summary>Every output, with its <c>Stage</c>, <c>Backend</c> (a <c>ShaderTarget</c>) and <c>SourcePath</c> metadata.
    /// Empty when the project's last stage source was deleted, so the build still removes the bytecode it wrote.</summary>
    public ITaskItem[] Outputs { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The shader cache directory, or empty for the compiler's per-user default.</summary>
    public string CacheDirectory { get; set; } = "";
    /// <summary>The <c>dxc</c> command.</summary>
    public string DxcCommand { get; set; } = "";

    /// <inheritdoc/>
    public override bool Execute() {
        var requestPath = ((Path.GetFullPath(path: RequestFile) + ".") + Guid.NewGuid().ToString(format: "N"));

        try {
            return ExecuteBuild(requestPath: requestPath);
        } finally {
            try {
                StopCompiler();
                if (m_process is not null) { m_process.WaitForExit(); }
            } finally {
                try { ReleaseHeld(); } finally {
                    lock (m_gate) {
                        if (m_process is not null) { m_process.Dispose(); m_process = null; }
                    }
                    if (File.Exists(path: requestPath)) { File.Delete(path: requestPath); }
                }
            }
        }
    }

    private bool ExecuteBuild(string requestPath) {
        var request = new StringBuilder();

        foreach (var output in (Outputs ?? Array.Empty<ITaskItem>())) {
            request.Append(value: output.GetMetadata(metadataName: "Stage")).Append(value: '\t').Append(value: output.GetMetadata(metadataName: "Backend")).Append(value: '\t')
                .Append(value: Path.GetFullPath(path: output.GetMetadata(metadataName: "SourcePath"))).Append(value: '\t').Append(value: output.GetMetadata(metadataName: "FullPath")).Append(value: '\n');
        }
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: requestPath));
        File.WriteAllText(requestPath, request.ToString());

        var start = new ProcessStartInfo(fileName: Host) {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = ProjectDirectory,
        };

        var arguments = new StringBuilder();

        foreach (var argument in new[] { Tool, Mode, "--project", ProjectDirectory, "--request", requestPath, "--lock", LockFile, "--cache", CacheDirectory, "--dxc", DxcCommand }) {
            arguments.Append(value: ((arguments.Length == 0) ? "" : " ")).Append(value: Quote(argument: argument));
        }
        start.Arguments = arguments.ToString();

        lock (m_gate) {
            if (m_cancelled) {
                return false;
            }
            m_process = Process.Start(startInfo: start);
        }
        m_process.ErrorDataReceived += (_, received) => { if (received.Data is not null) { Log.LogMessageFromText(lineOfText: received.Data, messageImportance: MessageImportance.High); } };
        m_process.BeginErrorReadLine();

        string line;

        while ((line = m_process.StandardOutput.ReadLine()) is not null) {
            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (Prefix + "request "))) {
                Grant(count: int.Parse(s: line.Substring(startIndex: (Prefix + "request ").Length)));
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (Prefix + "release "))) {
                Release(count: int.Parse(s: line.Substring(startIndex: (Prefix + "release ").Length)));
            } else {
                Log.LogMessageFromText(lineOfText: line, messageImportance: ((line.StartsWith(comparisonType: StringComparison.Ordinal, value: "Compiled ") || (line.IndexOf(comparisonType: StringComparison.Ordinal, value: " shader output(s): ") >= 0)) ? MessageImportance.High : MessageImportance.Normal));
            }
        }
        m_process.WaitForExit();

        return ((m_process.ExitCode == 0) && !Log.HasLoggedErrors && !m_cancelled);
    }
    private void Grant(int count) {
        var granted = 0;

        try {
            if (!m_requested && !m_cancelled && (count > 0)) {
                m_requested = true;
                if (BuildEngine is IBuildEngine9 engine) {
                    granted = engine.RequestCores(requestedCores: count);
                    lock (m_gate) { m_held += granted; }
                } else {
                    granted = 1;
                }
            }
        } catch (Exception exception) {
            Log.LogMessage(MessageImportance.Low, "Shader core request ended: {0}", exception.Message);
        }
        lock (m_gate) {
            try {
                m_process.StandardInput.WriteLine(value: ((granted > 0) ? ("granted " + granted) : "closed"));
                m_process.StandardInput.Flush();
            } catch (Exception exception) when (((exception is IOException) || (exception is ObjectDisposedException) || (exception is InvalidOperationException))) {
                // Execute releases the grant even if cancellation closed the transport before the answer arrived.
            }
        }
    }
    private void ReleaseHeld() {
        lock (m_gate) {
            if (m_held > 0) {
                ((IBuildEngine9)BuildEngine).ReleaseCores(coresToRelease: m_held);
                m_held = 0;
            }
        }
    }
    private void Release(int count) {
        lock (m_gate) {
            count = Math.Min(val1: count, val2: m_held);
            if (count <= 0) { return; }
            ((IBuildEngine9)BuildEngine).ReleaseCores(coresToRelease: count);
            m_held -= count;
        }
    }

    /// <summary>Cancels the generator, with a bounded process-tree kill fallback; <see cref="Execute"/> returns false.</summary>
    public void Cancel() {
        lock (m_gate) {
            m_cancelled = true;
        }
        StopCompiler();
    }

    // Asks a running generator to cancel and join its compilers, then kills its process tree if it has not exited within
    // ten seconds. The wait holds no lock: the reader thread must keep applying the releases the generator writes while
    // it unwinds.
    private void StopCompiler() {
        Process process;

        lock (m_gate) {
            process = m_process;
            if (process is null) { return; }
            try {
                if (process.HasExited) { return; }
                process.StandardInput.WriteLine(value: "cancel");
                process.StandardInput.Flush();
            } catch (Exception exception) when (((exception is IOException) || (exception is InvalidOperationException) || (exception is ObjectDisposedException))) { }
        }
        try {
            if (process.WaitForExit(milliseconds: 10000)) { return; }
            // Kill(bool) takes the compiler's own children with it; an engine on .NET Framework has only Kill().
            var tree = typeof(Process).GetMethod(name: "Kill", types: new[] { typeof(bool) });

            if (tree != null) { tree.Invoke(obj: process, parameters: new object[] { true }); } else { process.Kill(); }
        } catch (Exception exception) when (((exception is InvalidOperationException) || (exception is ObjectDisposedException) || (exception is System.ComponentModel.Win32Exception) || (exception is System.Reflection.TargetInvocationException))) { }
    }
    // One argument as the C runtime's command-line parser reads it back: quoted, with each run of backslashes before a
    // quote, or before the closing quote, doubled.
    private static string Quote(string argument) {
        var quoted = new StringBuilder(value: "\"");
        var backslashes = 0;

        foreach (var character in (argument ?? "")) {
            if (character == '\\') {
                backslashes++;
                continue;
            }
            quoted.Append(repeatCount: ((character == '"') ? ((backslashes * 2) + 1) : backslashes), value: '\\');
            backslashes = 0;
            quoted.Append(value: character);
        }

        return quoted.Append(repeatCount: (backslashes * 2), value: '\\').Append(value: '"').ToString();
    }
}
