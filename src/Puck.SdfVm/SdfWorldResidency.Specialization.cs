using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The execution state of one admitted shading program on a residency.</summary>
/// <param name="Identity">The program identity (<see cref="SdfShadeProgram.Identity"/>).</param>
/// <param name="Name">The author name, for reporting.</param>
/// <param name="State"><c>specialized</c> when the published kernels shade it, <c>compiling</c> while the kernels
/// that will run it compile, otherwise <c>interpreted</c>.</param>
/// <param name="Error">Why the latest specialization holding it failed, or <see langword="null"/>.</param>
public sealed record SdfShadeStatus(string Identity, string Name, string State, string? Error);

public sealed partial class SdfWorldResidency {
    private readonly BackgroundBuild<SdfShadePipelines> m_shadeBuild = new();
    private readonly List<SdfShadePipelines> m_shadeRetired = [];
    private SdfShadePipelines? m_shadeCurrent;
    private SdfShadeKernels? m_shadeRequest;
    private string? m_shadeFailure;
    private (SdfProgram Scene, SdfShadeProgram? Default, bool Enabled, IReadOnlyList<SdfShadeProgram> Declared, SdfKernelSet Kernels)? m_shadeKey;
    private SdfShadeProgram[] m_shadeActive = [];
    private HashSet<string> m_shadeActiveIdentities = new(comparer: StringComparer.Ordinal);
    private SdfShadeStatus[] m_shadeStatus = [];
    private volatile bool m_interpretShade;
    private volatile bool m_binShade = true;
    // The frame's selection and whether its views shade in bins, both taken once a frame by PrepareShade, with the levers
    // they were taken under.
    private SdfShadePipelines? m_shadeSelection;
    private bool m_shadesInBins;
    private (bool Interpret, bool Bins) m_shadeLevers = (false, true);

    /// <summary>Gets or sets whether the residency's active programs run through the interpreter even when published
    /// specialized kernels hold them, for comparing the tiers. The next produced frame takes it and re-renders the shading
    /// passes; compilation and publication continue either way.</summary>
    public bool InterpretShade {
        get => m_interpretShade;
        set => m_interpretShade = value;
    }
    /// <summary>Gets or sets whether a frame that runs authored programs shades its views in bins
    /// (<c>SdfWorldPackage.WithBins</c>: tiles with no program on the plain kernel, every other tile on the shading
    /// selection) rather than in one unsplit dispatch, for comparing the two. The next produced frame takes it, re-renders
    /// the shading passes and rebuilds the views' graphs; a graph records unsplit while it is not binned.</summary>
    public bool BinShade {
        get => m_binShade;
        set => m_binShade = value;
    }
    // Whether this frame's views shade in bins: the lever is on and the frame the passes record runs a program. A view
    // whose graph shades in bins records unsplit on any other frame.
    internal bool ShadesInBins => m_shadesInBins;

    /// <summary>Gets a snapshot of every active or requested program's execution state, safe to read from the console
    /// while rendering continues.</summary>
    public IReadOnlyList<SdfShadeStatus> ShadeStatus => Volatile.Read(location: ref m_shadeStatus);

    // The only publication door, run once a produced frame on the frame thread after the frame is packed and before any
    // pass records it, with the frame the passes record. It never waits, compiles, reflects, creates a pipeline or moves
    // a scene or history revision: a published kernel set shades every frame whose active programs it holds with
    // pixels the interpreter's within the still tolerance, so swapping it is a pointer change.
    private void PrepareShade(SdfFrame frame, IGpuDeviceContext device) {
        // A replaced set is freed only once no recording or unretired submission pins it (SdfShadeSlots).
        for (var i = m_shadeRetired.Count - 1; i >= 0; i--) {
            if (m_shadeRetired[i].Pins != 0) { continue; }
            m_shadeRetired[i].Dispose();
            m_shadeRetired.RemoveAt(index: i);
        }

        var changed = false;
        var key = (frame.Program, frame.ShadeDefault, frame.ShadePrograms, frame.ShadeDeclared, m_kernels);

        if (m_shadeKey != key) {
            m_shadeKey = key;
            changed = true;
            m_shadeActive = (frame.ShadePrograms ? SdfShadeHlsl.Canonical(programs: frame.ShadingPrograms) : []);
            m_shadeActiveIdentities = new HashSet<string>(collection: m_shadeActive.Select(selector: static p => p.Identity), comparer: StringComparer.Ordinal);
            var interpreter = SdfShadeKernels.InterpreterOf(kernels: m_kernels);

            // A set that no longer holds every active program, or was compiled against kernels or sources a reload
            // replaced, stops shading at once: the interpreter shows an edit on its next frame.
            if ((m_shadeCurrent is { } current) && ((current.Request.Interpreter != interpreter) || !current.Request.Covers(active: m_shadeActiveIdentities))) {
                RetireShade();
            }
            if (m_shadeActive.Length != 0) {
                // The documents' declared programs join the active ones, so the set stays stable while the scene's
                // composition changes and matches what world prepare compiled for the same documents. The request
                // compiles the sources m_kernels carries, the ones its interpreter was compiled from, never a tree.
                var request = new SdfShadeKernels(kernels: m_kernels, programs: m_shadeActive.Concat(second: frame.ShadeDeclared));

                if (request.Identity == m_shadeCurrent?.Request.Identity) {
                    CancelShadeBuild();
                } else if (request.Identity != m_shadeRequest?.Identity) {
                    CancelShadeBuild();
                    m_shadeRequest = request;
                    m_shadeFailure = null;
                    var target = ((BytecodeExtension() == ".dxil") ? ShaderTarget.Dxil : ShaderTarget.Spirv);
                    var catalog = m_pipelines.Catalog;

                    m_shadeBuild.Start(build: token => SdfShadePipelines.BuildAsync(
                        cache: catalog.Pipelines,
                        cancellationToken: token,
                        compiler: catalog.ShadeCompiler,
                        device: device,
                        request: request,
                        target: target
                    ));
                }
            }
        }
        if (m_shadeBuild.TryTake(error: out var error, result: out var ready)) {
            changed = true;
            if (error is DeviceLostException) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: error); }
            if (ready is null) {
                m_shadeFailure = (error?.Message ?? "the specialization produced no kernels");
                Console.Error.WriteLine(value: $"[world.shaders: specialization {m_shadeRequest?.Identity[..12]} refused: {m_shadeFailure}]");
            } else if ((ready.Request.Identity != m_shadeRequest?.Identity) || !ready.Request.Covers(active: m_shadeActiveIdentities)) {
                // An edit overtook the compile: the arrival is discarded, never published.
                ready.Dispose();
            } else {
                RetireShade();
                m_shadeCurrent = ready;
                m_shadeRequest = null;
                Console.Error.WriteLine(value: $"[world.shaders: specialized {ready.Request.Programs.Count} program(s) set={ready.Request.Identity[..12]}]");
            }
        }
        if (changed) { PublishShadeStatus(); }
        // The levers move the selection only here, so a frame's passes all record one, and only a lever, never a
        // publication, invalidates the shading passes.
        var levers = (Interpret: m_interpretShade, Bins: m_binShade);

        if (levers != m_shadeLevers) {
            m_shadeLevers = levers;
            m_tables?.InvalidateShadeSelection();
        }
        m_shadeSelection = ((levers.Interpret || (m_shadeActive.Length == 0)) ? null : m_shadeCurrent);
        m_shadesInBins = (levers.Bins && (m_shadeActive.Length != 0));
    }

    // An overtaken or released compile is canceled without waiting and retired to the catalog, which disposes a result
    // it completed anyway on the pool as it finishes and outlives this residency; a device loss drains the catalog's
    // retired builds before the device is recreated (ReleaseShadePipelines), the host's teardown before it is destroyed.
    private void CancelShadeBuild() {
        m_pipelines.Catalog.RetiredShadeBuilds.Retire(build: m_shadeBuild.Detach(), discard: static result => result.Dispose());
        m_shadeRequest = null;
    }
    private void RetireShade() {
        if (m_shadeCurrent is { } previous) {
            m_shadeRetired.Add(item: previous);
            m_shadeCurrent = null;
        }
    }
    private void PublishShadeStatus() {
        var programs = m_shadeActive.Concat(second: (m_shadeRequest?.Programs ?? [])).Concat(second: (m_shadeCurrent?.Request.Programs ?? []))
            .DistinctBy(keySelector: static p => p.Identity).OrderBy(keySelector: static p => p.Identity, comparer: StringComparer.Ordinal);

        Volatile.Write(location: ref m_shadeStatus, value: [.. programs.Select(selector: p => {
            if (m_shadeCurrent?.Request.Holds(identity: p.Identity) == true) { return new SdfShadeStatus(Error: null, Identity: p.Identity, Name: p.Name, State: "specialized"); }
            if ((m_shadeRequest?.Holds(identity: p.Identity) == true) && m_shadeBuild.IsPending) { return new SdfShadeStatus(Error: null, Identity: p.Identity, Name: p.Name, State: "compiling"); }
            return new SdfShadeStatus(Error: ((m_shadeRequest?.Holds(identity: p.Identity) == true) ? m_shadeFailure : null), Identity: p.Identity, Name: p.Name, State: "interpreted");
        })]);
    }

    // Read only by SdfShadeSlots, which pins the set before a native handle reaches a recorder. Null selects the fixed
    // kernels (ShadeFallback): the plain ones for a frame with no active program, the interpreter otherwise. PrepareShade
    // takes it once a frame, so every pass of a frame records one selection whatever the levers do meanwhile.
    internal SdfShadePipelines? ShadeSelection => m_shadeSelection;
    internal IGpuComputePipeline ShadeFallback(SdfKernel kernel) => m_tables!.Pipeline(kernel: ((m_shadeActive.Length != 0) ? kernel : PlainOf(kernel: kernel)));
    // The fixed kernel that shades no program in a specialized kernel's place, whatever the frame runs.
    internal IGpuComputePipeline ShadePlain(SdfKernel kernel) => m_tables!.Pipeline(kernel: PlainOf(kernel: kernel));
    private static SdfKernel PlainOf(SdfKernel kernel) => kernel switch {
        SdfKernel.Views => SdfKernel.ViewsPlain,
        SdfKernel.Glass => SdfKernel.GlassPlain,
        SdfKernel.ViewsShade => SdfKernel.ViewsShadePlain,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kernel), actualValue: kernel, message: "Not a shading kernel."),
    };

    // Device teardown and a residency's final idle drain are the only places that free sets regardless of their pins. The
    // final release waits for no compile or driver: a build still running is retired, and the sets disposed here are
    // built, so their leases hold no build to wait out. A device loss drains every retired build of the composition, so
    // no lease of the lost device outlives its recovery.
    private void ReleaseShadePipelines(bool deviceLost) {
        CancelShadeBuild();
        if (deviceLost) { m_pipelines.Catalog.RetiredShadeBuilds.Drain(); }
        m_shadeCurrent?.Dispose();
        m_shadeCurrent = null;
        foreach (var retired in m_shadeRetired) { retired.Dispose(); }
        m_shadeRetired.Clear();
        m_shadeRequest = null;
        m_shadeKey = null;
        m_shadeActive = [];
        m_shadeActiveIdentities = new HashSet<string>(comparer: StringComparer.Ordinal);
        m_shadeSelection = null;
        m_shadesInBins = false;
    }
}
