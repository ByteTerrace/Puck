using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;

/// <summary>Runs the selected shader recipes with a bounded number of compiler processes. Selection and publication
/// remain the existing tasks: this task writes only this invocation's token-owned temporary bytecode.</summary>
/// <remarks>Each child is the same MSBuild Exec used by the serial target. Failure or cancellation stops admission,
/// cancels every active child, and joins all workers before removing temporary files. Publication runs only after
/// every selected recipe succeeded; no compiler runs while the project's publication lock is held.</remarks>
public sealed class PuckCompileShaderBytecode : Task, ICancelableTask {
    private readonly object m_gate = new object();
    private readonly List<Exec> m_active = new List<Exec>();
    private bool m_stopping;
    private int m_next;

    /// <summary>Selected outputs, retaining their exact Recipe and SourcePath metadata.</summary>
    [Required]
    public ITaskItem[] BytecodeFiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The current invocation's unique temporary-output token.</summary>
    [Required]
    public string Token { get; set; } = "";
    /// <summary>The shader project's working directory, identical for every child.</summary>
    [Required]
    public string WorkingDirectory { get; set; } = "";
    /// <summary>The maximum compiler children in this project. The default is serial; callers that choose parallel
    /// compilers also bound MSBuild nodes so project-level parallelism cannot multiply this limit.</summary>
    public int Jobs { get; set; } = 1;

    /// <inheritdoc/>
    public override bool Execute() {
        if (Jobs < 1) {
            Log.LogError("PuckShaderCompileJobs must be a positive integer.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Token) || Token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            Log.LogError("Shader compilation requires a valid invocation token.");
            return false;
        }
        var workers = new Thread[Math.Min(Jobs, BytecodeFiles.Length)];
        try {
            for (var index = 0; index < workers.Length; index++) {
                workers[index] = new Thread(CompileNext) { IsBackground = true };
                workers[index].Start();
            }
        } catch (Exception exception) {
            Log.LogError("Could not start shader compiler workers: {0}", exception.Message);
            Cancel();
        } finally {
            // Even a failure to start a later worker must drain every child already admitted by earlier workers.
            foreach (var worker in workers) {
                if (worker != null && worker.IsAlive) { worker.Join(); }
            }
        }
        if (!m_stopping && !Log.HasLoggedErrors) { return true; }
        foreach (var bytecode in BytecodeFiles) {
            try { File.Delete(TemporaryPath(bytecode)); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) {
                Log.LogError("Could not remove shader temporary '{0}': {1}", TemporaryPath(bytecode), exception.Message);
            }
        }
        return false;
    }

    private string TemporaryPath(ITaskItem bytecode) => bytecode.GetMetadata("FullPath") + "." + Token + ".tmp";

    private void CompileNext() {
        while (true) {
            Exec child;
            lock (m_gate) {
                if (m_stopping || m_next == BytecodeFiles.Length) { return; }
                var bytecode = BytecodeFiles[m_next++];
                child = new Exec {
                    BuildEngine = BuildEngine,
                    WorkingDirectory = WorkingDirectory,
                    Command = bytecode.GetMetadata("Recipe") + " -Fo \"" + TemporaryPath(bytecode) + "\" \"" + bytecode.GetMetadata("SourcePath") + "\""
                };
                m_active.Add(child);
            }
            try {
                if (!child.Execute()) { Cancel(); }
            } catch (Exception exception) {
                Log.LogError("Shader compiler failed: {0}", exception.Message);
                Cancel();
            } finally {
                lock (m_gate) { m_active.Remove(child); }
            }
        }
    }

    /// <summary>Stops admitting work and cancels only the compiler children this task owns. Execute joins them
    /// before returning, so target cleanup cannot race a compiler that is still writing its temporary output.</summary>
    public void Cancel() {
        lock (m_gate) {
            m_stopping = true;
            foreach (var child in m_active) { child.Cancel(); }
        }
    }
}
