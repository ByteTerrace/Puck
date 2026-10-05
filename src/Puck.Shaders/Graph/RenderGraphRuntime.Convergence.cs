using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    private RenderGraphConvergence? m_convergence;
    private Puck.Hosting.FrameContext? m_convergenceContext;

    private readonly HashSet<int> m_convergenceInstances = [];
    private readonly HashSet<int> m_convergenceDemand = [];
    private readonly HashSet<string> m_convergenceStarted = new(StringComparer.Ordinal);
    private readonly Stack<int> m_convergencePending = new();
    private IReadOnlyList<RenderGraphFootprint> m_captureFootprints = [];

    private bool CanConverge(int index) => ((m_producers[index] is null) && (m_sources[index] is null));
    // An encoder already owns the Nth image. Keep its existing dependency closure frozen across index changes,
    // without restarting the samples or transferring that request to the new display root.
    private void RemapForwardedConvergence(Puck.Hosting.RenderGraphInstanceSet set) {
        if (m_convergence is not { Request.Completion.IsCompleted: false }) { return; }
        var retained = m_convergenceInstances.Select(selector: index => set.IndexOf(name: m_set.Instances[index].Name))
            .Where(predicate: index => (index >= 0)).ToArray();

        m_convergenceInstances.Clear();
        foreach (var index in retained) { m_convergenceInstances.Add(item: index); }
    }
    private void BeginConvergence(int captured, FrameCaptureRequest request) {
        m_convergence = new RenderGraphConvergence(request: request);
        m_convergenceContext = null;
        m_convergenceInstances.Clear();
        m_convergenceStarted.Clear();
        RefreshConvergenceDemand(captured);
    }
    // Capacity and declared reads do not imply visible image demand. Keep standing, deferred and previous-frame
    // contributors, but require their actual positive footprint; buffer dependencies never need a pixel footprint.
    // A package joins this request only once, so changing demand cannot restart its samples or finite source epoch.
    private void RefreshConvergenceDemand(int captured) {
        if (m_convergence is not { Request.Completion.IsCompleted: false } convergence ||
            m_nodes[captured]?.PendingCapturePath == convergence.Request.Path) { return; }
        m_convergenceDemand.Clear();
        m_convergencePending.Clear();
        m_convergencePending.Push(captured);
        while (m_convergencePending.TryPop(out var index)) {
            if (!m_convergenceDemand.Add(index)) { continue; }
            foreach (var read in m_set.Reads[index]) {
                if (read.Kind == ShaderPipelineResourceKind.Buffer || ShowsCaptureRead(index, read.Producer)) {
                    m_convergencePending.Push(read.Producer);
                }
            }
        }
        if (m_convergenceInstances.SetEquals(m_convergenceDemand)) { return; }
        m_convergenceInstances.Clear();
        m_convergenceInstances.UnionWith(m_convergenceDemand);
        foreach (var index in m_convergenceInstances) {
            if (!m_convergenceStarted.Add(m_set.Instances[index].Name)) { continue; }
            var packages = new HashSet<string>(comparer: StringComparer.Ordinal);
            if (m_set.Instances[index].ExternalPackage is { } declared &&
                m_packages.TryGetFactory(declared, out var declaredFactory)) {
                packages.Add(declared);
                declaredFactory.BeginConvergence(m_set.Instances[index].Name, convergence);
            }
            if (m_graphs[index] is { } graph) {

                foreach (var pass in graph.Pipeline.Plan.Passes) {
                    if ((pass.Package is { } step) && packages.Add(item: step.Package) &&
                        m_packages.TryGetFactory(package: step.Package, factory: out var factory)) {
                        factory.BeginConvergence(instance: m_set.Instances[index].Name, convergence: convergence);
                    }
                }
            }
        }
    }
    private bool ShowsCaptureRead(int consumer, int producer) {
        var consumerName = m_set.Instances[consumer].Name;
        var producerName = m_set.Instances[producer].Name;
        for (var index = 0; index < m_captureFootprints.Count; index++) {
            var footprint = m_captureFootprints[index];
            if (footprint.Consumer == consumerName && footprint.Producer == producerName &&
                footprint.Width > 0 && footprint.Height > 0) { return true; }
        }
        return false;
    }
    private Puck.Hosting.FrameContext ConvergenceContext(in Puck.Hosting.FrameContext context) {
        if (m_convergence is not { Request.Completion.IsCompleted: false }) {
            return context;
        }
        m_convergenceContext ??= context with { FrameDeltaTicks = 0, DeltaTicks = 0 };
        return m_convergenceContext.Value;
    }
    private bool IsConverging(int index) =>
        ((m_convergence is { IsActive: true }) && m_convergenceInstances.Contains(item: index));

    private bool CanServeConvergence =>
        CaptureReadiness.IsRendered &&
        ((m_convergence is not { Request.Converge: > 0 } convergence) || (convergence.Samples >= (convergence.Request.Converge - 1)));

    private Puck.Hosting.FrameRender CaptureReadiness {
        get {
            if (m_convergence is not { Request.Completion.IsCompleted: false }) { return Puck.Hosting.FrameRender.Rendered; }
            var result = Puck.Hosting.FrameRender.Rendered;
            foreach (var index in m_convergenceInstances) {
                if (m_graphs[index] is not { } graph) { continue; }
                var name = m_set.Instances[index].Name;
                var passes = graph.Pipeline.Plan.Passes;
                for (var position = 0; position < passes.Count; position++) {
                    var pass = passes[position];
                    if (pass.Package is not { } step || !m_packages.TryGetFactory(step.Package, out var factory)) { continue; }
                    var readiness = factory.CaptureReadinessOf(name);
                    if (readiness.Completion == Puck.Hosting.FrameCompletion.Refused) { return readiness; }
                    if (!readiness.IsRendered) { result = readiness; }
                }
            }
            return result;
        }
    }
}
