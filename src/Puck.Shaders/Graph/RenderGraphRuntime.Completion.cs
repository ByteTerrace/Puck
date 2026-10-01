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

    /// <summary>Gets whether the latest produced frame's root image shows that frame:
    /// <see cref="FrameCompletion.Rendered"/> when the root rendered it over inputs current for it (or stands unchanged),
    /// <see cref="FrameCompletion.NotYetRenderable"/> while an instance it reads within the frame, or the root itself, has
    /// not rendered it yet, and <see cref="FrameCompletion.Refused"/> when one cannot until something it was built from
    /// changes.</summary>
    public FrameCompletion Completion { get; private set; } = FrameCompletion.NotYetRenderable;
    /// <summary>Gets why the latest produced frame is not <see cref="FrameCompletion.Rendered"/>, naming the instance
    /// and its state, or <see langword="null"/> when it is.</summary>
    public string? CompletionReason { get; private set; } = "the runtime has produced no frame";

    // Sizes the per-instance standing to a set of instances, every one current until it renders.
    private void ResetStale(int count) {
        m_stale = new string?[count];
        m_staleRefused = new bool[count];
    }
    private void MarkCurrent(int index) {
        m_stale[index] = null;
        m_staleRefused[index] = false;
    }
    private void MarkStale(int index, string reason, bool refused = false) {
        m_stale[index] = reason;
        m_staleRefused[index] = refused;
    }
    // A scheduled graph instance whose node produced nothing this frame. A paused node presents its last image on purpose.
    // This is the one place a refusal becomes Refused: a package's refusal of the instance (IRenderGraphPackageFactory.
    // RefusalOf, such as an SDF residency's refused tables) or the node's own refused build; anything else is a wait.
    private void MarkUnproduced(int index, ShaderPipelineRenderNode node) {
        var name = m_set.Instances[index].Name;

        if (node.Paused) {
            MarkCurrent(index: index);
        } else if (PackageRefusalOf(index: index) is { } refusal) {
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
        } else if (!node.IsReady) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' has no installed graph yet: its pipelines are building"
            );
        } else {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' kept an earlier frame's image while its graph rebuilds"
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
    // A graph instance that rendered: stale when an input it reads within the frame is, when it bound a stand-in, or when
    // the output it bound is older than the frame the schedule has it read.
    private void MarkRendered(int index, RenderGraphSchedule schedule) {
        var name = m_set.Instances[index].Name;

        foreach (var binding in m_inputs[index]) {
            if (binding.PreviousFrame) {
                continue;
            }
            if (m_stale[binding.Producer] is { } reason) {
                MarkStale(
                    index: index,
                    reason: reason,
                    refused: m_staleRefused[binding.Producer]
                );

                return;
            }
            if (m_producers[binding.Producer] is not null) {
                continue;
            }

            var frame = FrameOf(
                consumer: name,
                producer: binding.ProducerName,
                schedule: schedule
            );

            if (
                (frame >= 0) &&
                (OutputAt(
                    frame: frame,
                    producer: binding.Producer
                ).Frame < frame)
            ) {
                MarkStale(
                    index: index,
                    reason: $"the instance '{name}' read an earlier frame's image of '{binding.ProducerName}'"
                );

                return;
            }
        }

        if (m_standInReads[index] is { } producer) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' rendered over a stand-in for '{producer}', which has no output for the frame"
            );

            return;
        }

        MarkCurrent(index: index);
    }
    // Decides the frame's completion from the root's standing.
    private void Complete() {
        if (m_stale[m_root] is { } reason) {
            Completion = (m_staleRefused[m_root]
                ? FrameCompletion.Refused
                : FrameCompletion.NotYetRenderable);
            CompletionReason = reason;
        } else if (
            (m_producers[m_root] is null) &&
            (m_current[m_root].Frame < 0)
        ) {
            Completion = FrameCompletion.NotYetRenderable;
            CompletionReason = $"the root '{m_set.Instances[m_root].Name}' has produced no output";
        } else {
            Completion = FrameCompletion.Rendered;
            CompletionReason = null;
        }
    }
}
