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

    // The batch whose pipelines are building, the tree load the request's kernels arrive from, the request they answer
    // and the pipelines its installed batches changed.
    private readonly BackgroundBuild<SdfWorldPipelineReload> m_reloadBuild = new();
    private SdfKernelTreeLoad? m_reloadLoad;
    private SdfShaderReloadStatus? m_reloadRequest;
    private int m_reloadChanged;

    /// <summary>Gets the latest request outcome, safe to read from the console while rendering continues.</summary>
    public SdfShaderReloadStatus ShaderReloadStatus => Volatile.Read(location: ref m_shaderReloadStatus);

    /// <summary>Queues a kernel reload. The next produced frame starts reading the kernels the tree carries, compiling
    /// each carried source to this residency's backend alone (<see cref="SdfKernelTreeLoad"/>), and each later frame
    /// installs the kernels that have compiled since the last batch, their changed pipelines created off the frame
    /// thread, so a kernel that compiles quickly renders while a heavy one still compiles. A kernel the tree does not
    /// carry keeps its bytecode. The request is pending until its last batch is installed; a failure keeps the batches
    /// already installed. Does not rebuild or reset the world.</summary>
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

    // Advances the request in flight, or starts a queued one. The tree's kernels arrive from the load as they compile;
    // each batch becomes a reload of its own (its pipelines leased and built off the frame thread, installed here once
    // built), so a kernel that compiles quickly renders while a heavy one still compiles. The request stays pending
    // until the load has completed and its last batch is installed.
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

        if (m_reloadBuild.IsPending) {
            return;
        }

        if (m_reloadLoad is { } load) {
            AdvanceShaderReload(load: load);

            return;
        }

        if (Volatile.Read(location: ref m_pendingShaderReload) is null) {
            return;
        }

        var request = Interlocked.Exchange(
            location1: ref m_pendingShaderReload,
            value: null
        )!;

        m_reloadRequest = request;
        m_reloadChanged = 0;

        try {
            m_reloadLoad = SdfKernelTreeLoad.Start(
                bytecodeExtension: BytecodeExtension(),
                compiler: Volatile.Read(location: ref m_pendingShaderCompiler)!,
                directory: request.Directory!
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            FinishShaderReload(failure: exception.Message);
        }
    }
    // Use the format already loaded into this residency, never an OS guess or a mutable host preference.
    private string BytecodeExtension() =>
        (ShaderBytecode.IsDxbcContainer(bytecode: m_kernels[SdfKernel.Beam].Span)
            ? ".dxil"
            : ".spv"
        );
    // Starts building the batch the load delivered since the last one, or finishes the request once the load has
    // completed with nothing left to install.
    private void AdvanceShaderReload(SdfKernelTreeLoad load) {
        var completed = load.IsCompleted;
        var ready = load.TakeReady();

        if (ready.Count != 0) {
            var kernels = m_kernels;

            foreach (var (kernel, bytecode) in ready) {
                kernels = kernels.With(
                    bytecode: bytecode,
                    kernel: kernel
                );
            }

            StartShaderReload(
                device: m_deviceContext!,
                kernels: kernels,
                pipelines: m_pipelines.Current!
            );

            return;
        }

        if (completed) {
            FinishShaderReload(failure: load.FailureOf());
        }
    }
    // Waits out a reload in flight and reports its request failed; a device loss or disposal discards the pipelines.
    private void CancelShaderReload(string reason) {
        m_reloadBuild.CancelAndWait(discard: static reload => reload.Dispose());
        m_reloadLoad?.Dispose();
        m_reloadLoad = null;

        if (m_reloadRequest is { } request) {
            m_reloadRequest = null;
            PublishShaderReload(result: request with { State = "failed", Error = $"canceled: {reason}" });
        }
    }
    private void InstallShaderReload(SdfWorldPipelineReload? reload, Exception? error) {
        try {
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            using (reload) {
                var changed = m_tables!.InstallReload(reload: reload!);
                var previous = m_kernels;

                m_kernels = reload!.Kernels; // Device-loss recovery must reconstruct the last successful set too.
                m_reloadChanged += changed;

                if (changed > 0) {
                    var kernels = string.Join(separator: ",", values: SdfKernelSet.Kernels.Where(predicate: kernel => !previous[kernel].Span.SequenceEqual(other: m_kernels[kernel].Span)).Select(selector: SdfKernelSet.StemOf));

                    Console.Error.WriteLine(value: $"[shaders.reload: request={m_reloadRequest!.RequestId} installed pipelines={changed} kernels={kernels}{((m_reloadLoad is { IsCompleted: false }) ? " more=compiling" : "")}]");
                }
            }
        } catch (Exception exception) {
            // A bad directory or bytecode, or kernels that do not read this host's interface (another instruction set's
            // stamp, a binding the host does not place), fail the request, as do several pipelines failing together;
            // anything else, a device loss above all, continues to the host's recovery. Batches already installed stay.
            m_reloadLoad?.Dispose();
            m_reloadLoad = null;
            FinishShaderReload(failure: exception.Message);

            if (FailsTheRequest(exception: exception)) {
                return;
            }

            throw;
        }

        static bool FailsTheRequest(Exception exception) =>
            ((exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) ||
            ((exception is AggregateException aggregate) && aggregate.InnerExceptions.All(predicate: FailsTheRequest)));
    }
    private void FinishShaderReload(string? failure) {
        var request = m_reloadRequest!;

        m_reloadRequest = null;
        m_reloadLoad?.Dispose();
        m_reloadLoad = null;
        PublishShaderReload(result: ((failure is not null)
            ? request with { State = "failed", Error = failure, ChangedPipelines = m_reloadChanged, Generation = (request.Generation + ((m_reloadChanged > 0) ? 1 : 0)) }
            : request with {
                Generation = (request.Generation + ((m_reloadChanged > 0)
                    ? 1
                    : 0)),
                State = ((m_reloadChanged > 0)
                    ? "applied"
                    : "unchanged"),
                ChangedPipelines = m_reloadChanged,
            }));
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
    // Kept apart so the build's closure is allocated only when a batch starts, never on a polled frame. The changed
    // kernels' replacements are leased from the pass-pipeline cache and waited for here, off the frame thread; another
    // residency leasing the replaced entries keeps them, since a reload swaps only this residency's leases.
    private void StartShaderReload(SdfWorldPipelines pipelines, IGpuDeviceContext device, SdfKernelSet kernels) {
        var cache = m_pipelines.Catalog.Pipelines;

        m_reloadBuild.Start(build: async token => {
            SdfWorldPipelineReload reload;

            // A reload reflects the kernels it would install, so DXIL needs the compiler's reflection: the dxc on the
            // search path, as the kernels' own build uses.
            using (var reflector = new ShaderBytecodeReflector(toolchain: new ShaderToolchain())) {
                reload = pipelines.PrepareReload(
                    cache: cache,
                    device: device,
                    kernels: kernels,
                    reflector: reflector
                );
            }

            try {
                await reload.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                return reload;
            } catch {
                reload.Dispose();

                throw;
            }
        });
    }
}
