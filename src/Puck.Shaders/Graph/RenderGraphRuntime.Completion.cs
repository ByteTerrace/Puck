using Puck.Hosting;

namespace Puck.Shaders;

// Completion: whether the root's image shows the frame the runtime was asked to compose. An instance the frame scheduled
// that produced nothing, or that rendered over an input older than the frame it reads, is stale for the frame, and so is
// every instance that reads it within the frame; a previous-frame read is a frame late by design, and an instance the
// frame does not schedule (its refresh, an unchanged view, a paused node) keeps the standing of its last render.
public sealed partial class RenderGraphRuntime {
    // Each instance's reason its latest render attempt does not show the frame it was asked for, or null when it does.
    private string?[] m_stale = [];
    // Whether that reason is a refusal, which only a change to what the instance was built from lifts.
    private bool[] m_staleRefused = [];
    // Repeated producer answers reuse their named diagnostic, even after a rendered frame clears the standing.
    private string?[] m_productionReasons = [];
    private string?[] m_productionMessages = [];

    /// <summary>Gets whether the latest produced frame's root image shows that frame:
    /// <see cref="FrameCompletion.Rendered"/> when the root rendered it over inputs current for it (or stands unchanged),
    /// <see cref="FrameCompletion.NotYetRenderable"/> while an instance it reads within the frame, or the root itself, has
    /// not rendered it yet, and <see cref="FrameCompletion.Refused"/> when one cannot until something it was built from
    /// changes; with the reason, naming the instance and its state, when it is not rendered.</summary>
    public FrameRender Render { get; private set; } = FrameRender.Waiting(reason: "the runtime has produced no frame");

    // Sizes the per-instance standing to a set of instances, every one current until it renders.
    private void ResetStale(int count) {
        m_stale = new string?[count];
        m_staleRefused = new bool[count];
        m_productionReasons = new string?[count];
        m_productionMessages = new string?[count];
    }
    private void MarkCurrent(int index) {
        m_stale[index] = null;
        m_staleRefused[index] = false;
    }
    private void MarkStale(int index, string reason, bool refused = false) {
        m_stale[index] = reason;
        m_staleRefused[index] = refused;
    }
    // Whether an instance presents its last image on purpose: a paused node renders only when stepped, so that image is
    // its output for every frame until then, both for its own standing and for an instance that reads it.
    private bool Stands(int index) => (m_nodes[index] is { Paused: true });
    // A scheduled graph instance whose node produced nothing this frame. A paused node presents its last image on purpose.
    // This is the one place a graph instance's refusal becomes Refused: a package's refusal of the instance
    // (IRenderGraphPackageFactory.RefusalOf, such as an SDF residency's refused tables), the node's refused build or a
    // missing graph; otherwise the instance is waiting. A source write that waits also passes through here, since binding
    // its region advances the build. A producer's or an upload's own three-way answer is taken as it is (MarkProduction).
    private void MarkUnproduced(int index, ShaderPipelineRenderNode node, string? waiting = null) {
        var name = m_set.Instances[index].Name;

        if (Stands(index: index)) {
            MarkCurrent(index: index);
        } else if ((PackageRefusalOf(index: index) ?? m_sources[index]?.Upload.Fault) is { } refusal) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {refusal}",
                refused: true
            );
        } else if (
            (node.LastSwapError is { } error) &&
            !node.IsBuildingCandidate &&
            !node.HasPendingCandidate
        ) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {error.Message}",
                refused: true
            );
        } else if (m_graphs[index] is null) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {(m_sources[index]?.Fault ?? "no graph is installed")}",
                refused: true
            );
        } else if (!node.IsReady) {
            MarkStale(
                index: index,
                reason: (waiting ?? $"the instance '{name}' has no installed graph yet: its pipelines are building")
            );
        } else {
            MarkStale(
                index: index,
                reason: (waiting ?? $"the instance '{name}' kept an earlier frame's image while its graph rebuilds")
            );
        }
    }
    // The first refusal a package of the instance's graph states for it, or null when none refuses.
    private string? PackageRefusalOf(int index) {
        if (m_graphs[index] is not { } graph) {
            return null;
        }

        var name = m_set.Instances[index].Name;
        var passes = graph.Pipeline.Plan.Passes;

        for (var position = 0; (position < passes.Count); position++) {
            if (
                (passes[position].Package is { } step) &&
                m_packages.TryGetFactory(
                    factory: out var factory,
                    package: step.Package
                ) &&
                (factory.RefusalOf(instance: name) is { } refusal)
            ) {
                return refusal;
            }
        }

        return null;
    }
    // A producer's or an upload's own answer that it produced nothing: refused or waiting, as it says, naming the
    // instance and its reason.
    private void MarkProduction(int index, FrameRender production) {
        if ((m_productionMessages[index] is null) || (m_productionReasons[index] != production.Reason)) {
            m_productionReasons[index] = production.Reason;
            m_productionMessages[index] = $"the instance '{m_set.Instances[index].Name}' produced no output: {production.Reason}";
        }

        MarkStale(
            index: index,
            reason: m_productionMessages[index]!,
            refused: (production.Completion == FrameCompletion.Refused)
        );
    }
    // A graph instance that rendered: stale when an input it reads within the frame is, when it bound a stand-in, or when
    // the output it bound is older than the frame the schedule has it read.
    private void MarkRendered(int index, RenderGraphSchedule schedule) {
        if (!MarkReadStale(index: index, schedule: schedule)) {
            MarkCurrent(index: index);
        }
    }
    // Only reads the schedule shows can affect completion. External producers and screen-sampling packages read
    // edges without graph input bindings; an unshown bound image has no bearing on the displayed frame.
    private bool MarkReadStale(int index, RenderGraphSchedule schedule) {
        var name = m_set.Instances[index].Name;
        string? waiting = null;

        // An indexed loop: an interface enumerator would allocate on every frame.
        var reads = schedule.Reads;

        for (var position = 0; (position < reads.Count); position++) {
            var read = reads[position];

            if ((read.Consumer != name) || read.PreviousFrame) {
                continue;
            }
            var producer = m_set.IndexOf(name: read.Producer);

            if ((m_producers[index] is null) &&
                !BindsProducer(index: index, producer: producer) &&
                !SamplesReads(graph: m_graphs[index])) {
                continue;
            }
            if (m_stale[producer] is { } reason) {
                if (m_staleRefused[producer]) {
                    MarkStale(index: index, reason: reason, refused: true);

                    return true;
                }
                waiting ??= reason;

                continue;
            }
            if (m_standInReads[index] == read.Producer) {
                waiting ??= $"the instance '{name}' rendered over a stand-in for '{read.Producer}', which has no output for the frame";

                continue;
            }
            if (
                (m_producers[producer] is null) &&
                !Stands(index: producer) &&
                (read.Frame >= 0) &&
                (RecordedAt(
                    frame: read.Frame,
                    producer: producer
                ).Frame < read.Frame)
            ) {
                waiting ??= $"the instance '{name}' read an earlier frame's image of '{read.Producer}'";
            }
        }

        if (waiting is null) {
            return false;
        }

        MarkStale(index: index, reason: waiting);

        return true;
    }
    // Decides the frame's completion from the root's standing.
    private void Complete() {
        if (m_stale[m_root] is { } reason) {
            Render = (m_staleRefused[m_root]
                ? FrameRender.Refused(reason: reason)
                : FrameRender.Waiting(reason: reason));
        } else if (
            (m_producers[m_root] is null) &&
            (m_current[m_root].Frame < 0)
        ) {
            Render = FrameRender.Waiting(reason: $"the root '{m_set.Instances[m_root].Name}' has produced no output");
        } else {
            Render = FrameRender.Rendered;
        }
    }
}
