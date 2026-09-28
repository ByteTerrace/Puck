using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    private FrameCaptureRequest? m_convergence;
    private int m_convergenceFrames;
    private Puck.Hosting.FrameContext? m_convergenceContext;

    private readonly HashSet<int> m_convergenceInstances = [];

    private void BeginConvergence(int captured, FrameCaptureRequest request) {
        m_convergence = request;
        m_convergenceFrames = 0;
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
                        factory.BeginConvergence(instance: m_set.Instances[index].Name, request: request);
                    }
                }
            }
            foreach (var read in m_set.Reads[index]) {
                pending.Push(item: read.Producer);
            }
        }
    }
    private Puck.Hosting.FrameContext ConvergenceContext(in Puck.Hosting.FrameContext context) {
        if (m_convergence is not { Converge: > 0, Completion.IsCompleted: false }) {
            return context;
        }
        m_convergenceContext ??= context with { FrameDeltaTicks = 0, DeltaTicks = 0 };
        return m_convergenceContext.Value;
    }
    private bool IsConverging(int index) =>
        ((m_convergence is { Converge: > 0, Completion.IsCompleted: false }) && m_convergenceInstances.Contains(item: index));

    private bool CanServeConvergence =>
        ((m_convergence is not { Converge: > 0 } request) || (m_convergenceFrames >= (request.Converge - 1)));
}
