using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    private RenderGraphConvergence? m_convergence;
    private Puck.Hosting.FrameContext? m_convergenceContext;

    private readonly HashSet<int> m_convergenceInstances = [];

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
        var convergence = new RenderGraphConvergence(request: request);

        m_convergence = convergence;
        m_convergenceContext = null;
        m_convergenceInstances.Clear();
        var pending = new Stack<int>();

        pending.Push(item: captured);
        while (pending.TryPop(result: out var index)) {
            if (!m_convergenceInstances.Add(item: index)) {
                continue;
            }
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
            foreach (var read in m_set.Reads[index]) {
                pending.Push(item: read.Producer);
            }
        }
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
                foreach (var pass in graph.Pipeline.Plan.Passes) {
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
