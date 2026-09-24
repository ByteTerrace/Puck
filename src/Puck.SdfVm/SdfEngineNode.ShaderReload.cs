using System.Runtime.ExceptionServices;
using Puck.Hosting;

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
    private SdfShaderReloadStatus m_shaderReloadStatus = new(
        ChangedPipelines: 0,
        Directory: null,
        Error: null,
        Generation: 0,
        RequestId: 0,
        State: "idle"
    );

    private SdfShaderReloadStatus? m_pendingShaderReload;

    // The reload whose pipelines are building, and the request it answers.
    private readonly BackgroundBuild<SdfWorldPipelineReload> m_reloadBuild = new();

    private SdfShaderReloadStatus? m_reloadRequest;

    /// <summary>Gets the latest request outcome, safe to read from the console while rendering continues.</summary>
    public SdfShaderReloadStatus ShaderReloadStatus => Volatile.Read(location: ref m_shaderReloadStatus);

    /// <summary>Queues a compiled-kernel reload. The next produced frame starts loading the bytecode and creating the
    /// changed pipelines off the frame thread; a later frame installs them. Does not rebuild or reset the world.</summary>
    /// <param name="directory">Bytecode directory; null selects the deployed SDF assets. Relative paths resolve against the process working directory.</param>
    /// <returns>False if another request is still pending; otherwise true. Read <see cref="ShaderReloadStatus"/> for completion.</returns>
    /// <exception cref="ArgumentException">The directory path is invalid.</exception>
    public bool RequestShaderReload(string? directory = null) {
        var resolved = Path.GetFullPath(path: (directory ?? SdfWorldKernels.DefaultDirectory));

        lock (m_shaderReloadGate) {
            var previous = ShaderReloadStatus;

            if (previous.State == "pending") {
                return false;
            }
            var request = new SdfShaderReloadStatus(
                RequestId: (previous.RequestId + 1),
                Generation: previous.Generation,
                State: "pending",
                ChangedPipelines: 0,
                Directory: resolved,
                Error: null
            );

            Volatile.Write(
                location: ref m_shaderReloadStatus,
                value: request
            );
            Volatile.Write(
                location: ref m_pendingShaderReload,
                value: request
            );
            return true;
        }
    }

    // Starts a queued reload, or installs the one whose pipelines finished building. Loading the bytecode and creating
    // the changed pipelines run on the thread pool; only the install — a frame-ring drain, the swap, and the ISA
    // handshake — runs here, so the request stays pending while the driver compiles.
    private void ApplyPendingShaderReload() {
        if (m_reloadBuild.TryTake(
            error: out var error,
            result: out var reload
        )) {
            InstallShaderReload(
                error: error,
                reload: reload
            );
        }

        if (m_reloadBuild.IsPending || (Volatile.Read(location: ref m_pendingShaderReload) is null)) {
            return;
        }

        var request = Interlocked.Exchange(
            location1: ref m_pendingShaderReload,
            value: null
        )!;

        // A reload replaces pipelines in place, draining only this node's frame ring, so no other engine may record
        // with the set: another node or view on the device leasing the same set fails the request.
        if (!m_pipelines.TryMakePrivate()) {
            PublishShaderReload(result: request with {
                State = "failed",
                Error = "the pipeline set is shared with another engine on this device",
            });

            return;
        }

        m_reloadRequest = request;
        StartShaderReload(
            directory: request.Directory!,
            // Use the format already loaded into this node, never an OS guess or a mutable host preference.
            extension: (m_kernels.Beam.Span.StartsWith(value: "DXBC"u8)
                ? ".dxil"
                : ".spv"
            ),
            pipelines: m_pipelines.Current!
        );
    }
    // Waits out a reload in flight and reports its request failed; a device loss or disposal discards the pipelines.
    private void CancelShaderReload(string reason) {
        m_reloadBuild.CancelAndWait(discard: static reload => reload.Dispose());

        if (m_reloadRequest is { } request) {
            m_reloadRequest = null;
            PublishShaderReload(result: request with { State = "failed", Error = $"canceled: {reason}" });
        }
    }
    private void InstallShaderReload(SdfWorldPipelineReload? reload, Exception? error) {
        var request = m_reloadRequest!;
        SdfShaderReloadStatus result;

        m_reloadRequest = null;

        try {
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            using (reload) {
                var changed = m_engine!.InstallReload(reload: reload!);

                m_kernels = reload!.Kernels; // Device-loss recovery must reconstruct the last successful set too.
                result = request with {
                    Generation = (request.Generation + ((changed > 0)
                        ? 1
                        : 0)),
                    State = ((changed > 0)
                        ? "applied"
                        : "unchanged"),
                    ChangedPipelines = changed,
                };
            }
        } catch (Exception exception) {
            PublishShaderReload(result: request with { State = "failed", Error = exception.Message });

            // A bad directory, bytecode or handshake fails the request; anything else, a device loss above all,
            // continues to the host's recovery.
            if (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) {
                return;
            }

            throw;
        }

        PublishShaderReload(result: result);
    }
    private void PublishShaderReload(SdfShaderReloadStatus result) {
        lock (m_shaderReloadGate) {
            Volatile.Write(
                location: ref m_shaderReloadStatus,
                value: result
            );
        }
        Console.Error.WriteLine(value: $"[world.shaders.reload: request={result.RequestId} {result.State} generation={result.Generation} pipelines={result.ChangedPipelines}{((result.Error is { } error)
            ? $" error={error}"
            : "")}]");
    }
    // Kept apart so the build's closure is allocated only when a reload starts, never on a polled frame.
    private void StartShaderReload(SdfWorldPipelines pipelines, string directory, string extension) =>
        m_reloadBuild.Start(build: token => pipelines.PrepareReload(
            cancellationToken: token,
            kernels: SdfWorldKernels.Load(
                bytecodeExtension: extension,
                directory: directory
            )
        ));
}
