using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The current execution state of one admitted shading identity.</summary>
public sealed record SdfShadeStatus(string Identity, string Name, string State, string? Error);

public sealed partial class SdfWorldResidency {
    private readonly BackgroundBuild<SdfShadePipelines> m_shadeBuild = new();
    private readonly List<CanceledBuild<SdfShadePipelines>> m_shadeCanceled = [];
    private readonly List<SdfShadePipelines> m_shadeRetired = [];
    private SdfShadePipelines? m_shadeCurrent;
    private SdfShadeKernels? m_shadeRequest;
    private (SdfProgram Scene, SdfShadeProgram? Default, bool Enabled, SdfKernelSet Kernels)? m_shadeKey;
    private SdfShadeStatus[] m_shadeStatus = [];
    private string? m_shadeTree;
    private bool m_shadeEmpty;
    private bool m_interpretShade;

    /// <summary>Forces the admitted programs through tier 1 for a visual comparison. Changing this diagnostic forces
    /// a shading recording; ordinary specialization publication never invalidates a frame.</summary>
    public bool InterpretShade {
        get => m_interpretShade;
        set {
            if (m_interpretShade == value) { return; }
            m_interpretShade = value;
            m_tables?.InvalidateShadeSelection();
        }
    }

    /// <summary>Gets a snapshot of every active program's execution state, including compile or driver refusal.</summary>
    public IReadOnlyList<SdfShadeStatus> ShadeStatus => Volatile.Read(ref m_shadeStatus);

    // The only publication door. PrepareOnce runs before any pass records and at most once per produced frame.
    // Never waits, compiles, reflects, creates a pipeline, or invalidates scene/history revisions here.
    private void PrepareShade(SdfFrame frame, IGpuDeviceContext device) {
        for (var i = m_shadeCanceled.Count - 1; i >= 0; i--) {
            if (!m_shadeCanceled[i].IsCompleted) { continue; }
            m_shadeCanceled[i].Wait(static result => result.Dispose());
            m_shadeCanceled.RemoveAt(i);
        }
        for (var i = m_shadeRetired.Count - 1; i >= 0; i--) {
            if (m_shadeRetired[i].Pins != 0) { continue; }
            m_shadeRetired[i].Dispose();
            m_shadeRetired.RemoveAt(i);
        }
        var key = (frame.Program, frame.ShadeDefault, frame.ShadePrograms, m_kernels);
        if (m_shadeKey != key) {
            m_shadeKey = key;
            var programs = frame.ShadePrograms ? frame.ShadingPrograms.ToArray() : [];
            m_shadeEmpty = programs.Length == 0;
            var request = new SdfShadeKernels(programs, m_kernels);
            if (request.Identity != m_shadeRequest?.Identity) {
                if (m_shadeBuild.IsPending) { m_shadeCanceled.Add(m_shadeBuild.Detach()); }
                if (m_shadeCurrent is { } previous) { m_shadeRetired.Add(previous); m_shadeCurrent = null; }
                m_shadeRequest = request;
                SetShadeStatus(request, "compiling");
                if (!m_shadeEmpty) {
                    var tree = m_shadeTree ?? SdfKernelSet.DeployedTree;
                    var target = BytecodeExtension() == ".dxil" ? ShaderTarget.Dxil : ShaderTarget.Spirv;
                    var catalog = m_pipelines.Catalog;
                    m_shadeBuild.Start(token => SdfShadePipelines.BuildAsync(request, catalog.ShadeCompiler, target,
                        tree, catalog.Pipelines, device, token));
                }
            }
        }
        if (m_shadeBuild.TryTake(out var ready, out var error)) {
            if (error is DeviceLostException) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error); }
            if (ready is null) { SetShadeStatus(m_shadeRequest!, "interpreted", error?.Message); return; }
            if (ready.Request.Identity != m_shadeRequest?.Identity) { ready.Dispose(); return; }
            m_shadeCurrent = ready;
            SetShadeStatus(ready.Request, "specialized");
        }
    }

    private void SetShadeStatus(SdfShadeKernels request, string state, string? error = null) =>
        Volatile.Write(ref m_shadeStatus, request.Programs.Select(p => new SdfShadeStatus(p.Identity, p.Name, state, error)).ToArray());

    // Called only by SdfShadeSlots, which pins this selection before returning a native handle to a recorder.
    internal SdfShadePipelines? ShadeSelection => m_interpretShade ? null : m_shadeCurrent;
    internal IGpuComputePipeline ShadeFallback(SdfKernel kernel) => m_tables!.Pipeline(m_shadeEmpty
        ? kernel == SdfKernel.Views ? SdfKernel.ViewsPlain : SdfKernel.GlassPlain : kernel);

    // Device teardown or a residency's final idle drain is the only place allowed to ignore recording pins.
    private void ReleaseShadePipelines() {
        m_shadeBuild.CancelAndWait(static result => result.Dispose());
        foreach (var canceled in m_shadeCanceled) { canceled.Wait(static result => result.Dispose()); }
        m_shadeCanceled.Clear();
        m_shadeCurrent?.Dispose();
        m_shadeCurrent = null;
        foreach (var retired in m_shadeRetired) { retired.Dispose(); }
        m_shadeRetired.Clear();
        m_shadeRequest = null;
        m_shadeKey = null;
    }
}
