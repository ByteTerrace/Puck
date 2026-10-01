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
        if (m_convergence is not { IsActive: true }) { return; }
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
        if (request.Converge == 0) {
            return;
        }

        var pending = new Stack<int>();

        pending.Push(item: captured);
        while (pending.TryPop(result: out var index)) {
            if (!m_convergenceInstances.Add(item: index)) {
                continue;
            }
            if (m_graphs[index] is { } graph) {
                var packages = new HashSet<string>(comparer: StringComparer.Ordinal);

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
        if (m_convergence is not { IsActive: true }) {
            return context;
        }
        m_convergenceContext ??= context with { FrameDeltaTicks = 0, DeltaTicks = 0 };
        return m_convergenceContext.Value;
    }
    private bool IsConverging(int index) =>
        ((m_convergence is { IsActive: true }) && m_convergenceInstances.Contains(item: index));

    private bool CanServeConvergence =>
        ((m_convergence is not { Request.Converge: > 0 } convergence) || (convergence.Samples >= (convergence.Request.Converge - 1)));
}
