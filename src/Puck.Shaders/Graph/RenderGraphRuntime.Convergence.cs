using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    private FrameCaptureRequest? m_convergence;
    private int m_convergenceFrames;
    private bool m_convergenceWarming;
    private bool m_restartConvergence;
    private Puck.Hosting.FrameContext? m_convergenceContext;

    private readonly HashSet<int> m_convergenceInstances = [];

    private bool CanConverge(int index) => ((m_producers[index] is null) && (m_sources[index] is null));
    // An encoder already owns the Nth image. Keep its existing dependency closure frozen across index changes,
    // without restarting the samples or transferring that request to the new display root.
    private void RemapForwardedConvergence(Puck.Hosting.RenderGraphInstanceSet set) {
        if (m_convergence is not { Converge: > 0, Completion.IsCompleted: false }) { return; }
        var retained = m_convergenceInstances.Select(selector: index => set.IndexOf(name: m_set.Instances[index].Name))
            .Where(predicate: index => (index >= 0)).ToArray();

        m_convergenceInstances.Clear();
        foreach (var index in retained) { m_convergenceInstances.Add(item: index); }
    }
    private void BeginConvergence(int captured, FrameCaptureRequest request) {
        m_convergence = request;
        m_convergenceFrames = 0;
        m_convergenceWarming = false;
        m_restartConvergence = false;
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
            foreach (var read in m_set.Reads[index]) {
                pending.Push(item: read.Producer);
            }
        }
        ResetConvergenceSamples();
    }
    // Dependency/build warmup can run real package passes. Their samples must not become a capture's first history.
    // Restart at the next frame boundary, after a completed ready target proves the whole contributing closure ready.
    private void PrepareConvergenceSamples() {
        if (m_convergence is not { Converge: > 0, Completion.IsCompleted: false }) { return; }
        if (m_restartConvergence && (m_convergence is { Completion.IsCompleted: false })) {
            ResetConvergenceSamples();
            m_convergenceWarming = false;
            m_restartConvergence = false;
        }
        foreach (var index in m_convergenceInstances) {
            if (m_nodes[index] is { IsReady: false }) { DeferConvergence(index: index); }
        }
    }
    private void DeferConvergence(int index) {
        if (IsConverging(index: index)) {
            m_convergenceWarming = true;
            m_convergenceFrames = 0;
        }
    }
    private void ResetConvergenceSamples() {
        if (m_convergence is not { } request) { return; }
        foreach (var index in m_convergenceInstances) {
            if (m_graphs[index] is { } graph) {
                var packages = new HashSet<string>(comparer: StringComparer.Ordinal);

                foreach (var pass in graph.Pipeline.Plan.Passes) {
                    if ((pass.Package is { } step) && packages.Add(item: step.Package) &&
                        m_packages.TryGetFactory(package: step.Package, factory: out var factory)) {
                        factory.BeginConvergence(instance: m_set.Instances[index].Name, request: request);
                    }
                }
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
        ((m_convergence is not { Converge: > 0 } request) || (!m_convergenceWarming && (m_convergenceFrames >= (request.Converge - 1))));
}
