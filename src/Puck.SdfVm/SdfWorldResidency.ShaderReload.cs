using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>An immutable snapshot of a residency's most recent shader reload request.</summary>
/// <param name="RequestId">Monotone request number; zero before the first request.</param>
/// <param name="Generation">Number of successfully installed, changed shader sets.</param>
/// <param name="State">Idle, pending, applied, unchanged, or failed.</param>
/// <param name="ChangedPipelines">Active pipelines replaced by this request.</param>
/// <param name="Directory">Resolved bytecode directory, or null before a request.</param>
/// <param name="Error">Failure reason, or null.</param>
public sealed record SdfShaderReloadStatus(long RequestId, long Generation, string State, int ChangedPipelines, string? Directory, string? Error);
public sealed partial class SdfWorldResidency {
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
    // The compiler the pending request's carried sources compile with, written with the request under the gate.
    private ShaderCompiler? m_pendingShaderCompiler;

    // The reload whose pipelines are building, and the request it answers.
    private readonly BackgroundBuild<SdfWorldPipelineReload> m_reloadBuild = new();

    private SdfShaderReloadStatus? m_reloadRequest;

    /// <summary>Gets the latest request outcome, safe to read from the console while rendering continues.</summary>
    public SdfShaderReloadStatus ShaderReloadStatus => Volatile.Read(location: ref m_shaderReloadStatus);

    /// <summary>Queues a kernel reload. The next produced frame starts reading the kernels the tree carries, compiling
    /// each carried source (<see cref="SdfKernelSet.Overlaid"/>), and creating the changed pipelines off the frame
    /// thread; a later frame installs them. A kernel the tree does not carry keeps its bytecode. Does not rebuild or
    /// reset the world.</summary>
    /// <param name="compiler">The compiler a carried kernel source compiles with.</param>
    /// <param name="tree">The kernel tree whose passes directory (<see cref="SdfKernelSet.PassesDirectory"/>) carries the
    /// kernels, as sources or bytecode: a source checkout's <c>src/Puck.SdfVm/Assets/Shaders/Sdf</c>, or null for the
    /// deployed tree (<see cref="SdfKernelSet.DeployedTree"/>). Relative paths resolve against the process working
    /// directory.</param>
    /// <returns>False if another request is still pending; otherwise true. Read <see cref="ShaderReloadStatus"/> for completion.</returns>
    /// <exception cref="ArgumentException">The directory path is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> is <see langword="null"/>.</exception>
    public bool RequestShaderReload(ShaderCompiler compiler, string? tree = null) {
        ArgumentNullException.ThrowIfNull(argument: compiler);

        var resolved = Path.GetFullPath(path: SdfKernelSet.PassesDirectory(tree: (tree ?? SdfKernelSet.DeployedTree)));

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
                location: ref m_pendingShaderCompiler,
                value: compiler
            );
            Volatile.Write(
                location: ref m_pendingShaderReload,
                value: request
            );
            return true;
        }
    }

    // Starts a queued reload, or installs the one whose pipelines finished building. Loading the bytecode and creating
    // the changed pipelines run on the thread pool; only the install, a device drain and the swap, runs here, so the
    // request stays pending while the driver compiles.
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

        m_reloadRequest = request;
        StartShaderReload(
            compiler: Volatile.Read(location: ref m_pendingShaderCompiler)!,
            device: m_deviceContext!,
            directory: request.Directory!,
            // Use the format already loaded into this residency, never an OS guess or a mutable host preference.
            extension: (m_kernels[SdfKernel.Beam].Span.StartsWith(value: "DXBC"u8)
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
                var changed = m_tables!.InstallReload(reload: reload!);

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

            // A bad directory or bytecode, or kernels that do not read this host's interface (another instruction set's
            // stamp, a binding the host does not place), fail the request, as do several pipelines failing together;
            // anything else, a device loss above all, continues to the host's recovery.
            if (FailsTheRequest(exception: exception)) {
                return;
            }

            throw;
        }

        PublishShaderReload(result: result);

        static bool FailsTheRequest(Exception exception) =>
            ((exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) ||
            ((exception is AggregateException aggregate) && aggregate.InnerExceptions.All(predicate: FailsTheRequest)));
    }
    private void PublishShaderReload(SdfShaderReloadStatus result) {
        lock (m_shaderReloadGate) {
            Volatile.Write(
                location: ref m_shaderReloadStatus,
                value: result
            );
        }
        Console.Error.WriteLine(value: $"[shaders.reload: request={result.RequestId} {result.State} generation={result.Generation} pipelines={result.ChangedPipelines}{((result.Error is { } error)
            ? $" error={error}"
            : "")}]");
    }
    // Kept apart so the build's closure is allocated only when a reload starts, never on a polled frame. The changed
    // kernels' replacements are leased from the pass-pipeline cache and waited for here, off the frame thread; another
    // residency leasing the replaced entries keeps them, since a reload swaps only this residency's leases.
    private void StartShaderReload(SdfWorldPipelines pipelines, IGpuDeviceContext device, ShaderCompiler compiler, string directory, string extension) {
        var cache = m_pipelines.Catalog.Pipelines;
        var current = m_kernels;

        m_reloadBuild.Start(build: token => {
            SdfWorldPipelineReload reload;

            // A reload reflects the kernels it would install, so DXIL needs the compiler's reflection: the dxc on the
            // search path, as the kernels' own build uses.
            using (var reflector = new ShaderBytecodeReflector(toolchain: new ShaderToolchain())) {
                reload = pipelines.PrepareReload(
                    cache: cache,
                    device: device,
                    kernels: current.Overlaid(
                        bytecodeExtension: extension,
                        cancellationToken: token,
                        compiler: compiler,
                        directory: directory
                    ),
                    reflector: reflector
                );
            }

            try {
                reload.Wait(cancellationToken: token);

                return reload;
            } catch {
                reload.Dispose();

                throw;
            }
        });
    }
}
