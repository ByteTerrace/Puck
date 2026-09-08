namespace Puck.SdfVm;

/// <summary>An immutable snapshot of a render node's most recent shader reload request.</summary>
/// <param name="RequestId">Monotone request number; zero before the first request.</param>
/// <param name="Generation">Number of successfully installed, changed shader sets.</param>
/// <param name="State">Idle, pending, applied, unchanged, or failed.</param>
/// <param name="ChangedPipelines">Active pipelines replaced by this request.</param>
/// <param name="Directory">Resolved bytecode directory, or null before a request.</param>
/// <param name="Error">Failure reason, or null.</param>
public sealed record SdfShaderReloadStatus(long RequestId, long Generation, string State, int ChangedPipelines, string? Directory, string? Error);

public sealed partial class SdfEngineNode {
    private readonly Lock m_shaderReloadGate = new();
    private SdfShaderReloadStatus m_shaderReloadStatus = new(RequestId: 0, Generation: 0, State: "idle", ChangedPipelines: 0, Directory: null, Error: null);
    private SdfShaderReloadStatus? m_pendingShaderReload;

    /// <summary>Gets the latest request outcome, safe to read from the console while rendering continues.</summary>
    public SdfShaderReloadStatus ShaderReloadStatus => Volatile.Read(location: ref m_shaderReloadStatus);

    /// <summary>Queues a compiled-kernel reload for the next produced frame. Does not rebuild or reset the world.</summary>
    /// <param name="directory">Bytecode directory; null selects the deployed SDF assets. Relative paths resolve against the process working directory.</param>
    /// <returns>False if another request is still pending; otherwise true. Read <see cref="ShaderReloadStatus"/> for completion.</returns>
    /// <exception cref="ArgumentException">The directory path is invalid.</exception>
    public bool RequestShaderReload(string? directory = null) {
        var resolved = Path.GetFullPath(path: directory ?? SdfWorldKernels.DefaultDirectory);
        lock (m_shaderReloadGate) {
            var previous = ShaderReloadStatus;
            if (previous.State == "pending") {
                return false;
            }
            var request = new SdfShaderReloadStatus(RequestId: previous.RequestId + 1, Generation: previous.Generation, State: "pending", ChangedPipelines: 0, Directory: resolved, Error: null);
            Volatile.Write(location: ref m_shaderReloadStatus, value: request);
            Volatile.Write(location: ref m_pendingShaderReload, value: request);
            return true;
        }
    }

    private void ApplyPendingShaderReload() {
        var request = Interlocked.Exchange(location1: ref m_pendingShaderReload, value: null);
        if (request is null) {
            return;
        }

        SdfShaderReloadStatus result;
        try {
            // Use the format already loaded into this node, never an OS guess or a mutable host preference.
            var extension = m_kernels.Beam.Span.StartsWith(value: "DXBC"u8) ? ".dxil" : ".spv";
            var kernels = SdfWorldKernels.Load(bytecodeExtension: extension, directory: request.Directory!);
            var changed = m_engine!.ReloadKernels(kernels: kernels);
            m_kernels = kernels; // Device-loss recovery must reconstruct the last successful set too.
            result = request with { Generation = request.Generation + (changed > 0 ? 1 : 0), State = changed > 0 ? "applied" : "unchanged", ChangedPipelines = changed };
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) {
            result = request with { State = "failed", Error = exception.Message };
        }

        lock (m_shaderReloadGate) {
            Volatile.Write(location: ref m_shaderReloadStatus, value: result);
        }
        Console.Error.WriteLine(value: $"[world.shaders.reload: request={result.RequestId} {result.State} generation={result.Generation} pipelines={result.ChangedPipelines}{(result.Error is { } error ? $" error={error}" : "")}]");
    }
}
