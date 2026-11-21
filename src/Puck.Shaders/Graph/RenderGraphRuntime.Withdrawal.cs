using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// A withdrawn picture: a host says the viewer an instance renders for may no longer see what it showed
// (IRenderGraphInstances.Withdraw), and from the next frame nothing binds any image made from it.
//
// Invariant: once withdrawn, an output is bound by no reader. Every reader binds through the runtime's records of
// completed outputs (RecordedAt and Resolve: graph inputs, external and unbound reads, the display, captures, standing
// outputs), so forgetting those records is the one door. The node's own published image goes too
// (ShaderPipelineRenderNode.WithdrawPublication), so a paused node that presents its last image presents nothing, and a
// package's retained history of the instance goes with it (IRenderGraphPackageFactory.OnOutputsForgotten).
//
// What an instance read is in its own output, so the withdrawal follows every read edge to the instances that read a
// withdrawn one, at the same frame or the previous one, and withdraws theirs too: a world view that showed the picture
// on a screen, a pane, the display root. Each withdrawn instance renders again before anything shows it (it renders to
// stay shown, RenderGraphRuntime.Standing.cs), and a paused one renders nothing until it is resumed. It stops at
// external producers, which keep their own latest output, and at exports already copied out of the runtime.
public sealed partial class RenderGraphRuntime {
    // The withdrawals hosts asked for since the last frame, applied before it schedules.
    private readonly List<string> m_withdrawals = [];
    // Instances whose outputs were withdrawn and that have not rendered since: each renders again whenever it is shown.
    private readonly HashSet<string> m_withdrawing = new(comparer: StringComparer.Ordinal);
    // The instances the frame's withdrawal reaches, sized to the set.
    private bool[] m_withdrawn = [];

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    public void SetRoute(string instance, DeliveryLabel route) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        var index = IndexOf(instance: instance);

        if (!route.IsClassified) {
            throw new ArgumentException(
                message: $"Route for instance '{instance}' has an unclassified delivery label.",
                paramName: nameof(route)
            );
        }

        m_routes[index] = route;
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    public void Withdraw(string instance) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        _ = IndexOf(instance: instance);
        m_withdrawals.Add(item: instance);
    }

    // Applies the withdrawals asked for since the last frame: marks each named instance and every instance that reads a
    // marked one, then forgets each marked instance's outputs, drops its node's publication and its packages' history.
    private void ApplyWithdrawals() {
        if (m_withdrawing.Count != 0) {
            _ = m_withdrawing.RemoveWhere(match: name => (m_set.IndexOf(name: name) < 0));
        }
        if (m_withdrawals.Count == 0) {
            return;
        }

        var count = m_set.Instances.Count;

        if (m_withdrawn.Length != count) {
            m_withdrawn = new bool[count];
        } else {
            Array.Clear(array: m_withdrawn);
        }

        foreach (var name in m_withdrawals) {
            if (m_set.IndexOf(name: name) is >= 0 and var index) {
                m_withdrawn[index] = true;
            }
        }
        m_withdrawals.Clear();

        for (var changed = true; changed;) {
            changed = false;
            for (var index = 0; (index < count); index++) {
                if (m_withdrawn[index]) { continue; }
                foreach (var read in m_set.Reads[index]) {
                    if (m_withdrawn[read.Producer]) {
                        m_withdrawn[index] = true;
                        changed = true;
                        break;
                    }
                }
            }
        }

        var factories = m_packages.Factories;

        for (var index = 0; (index < count); index++) {
            if (!m_withdrawn[index]) { continue; }

            var name = m_set.Instances[index].Name;

            m_nodes[index]?.WithdrawPublication();
            m_outputs.Forget(index: index);
            m_producerLabels[index] = default;
            for (var factory = 0; (factory < factories.Count); factory++) {
                factories[factory].OnOutputsForgotten(instance: name);
            }
            // A source converts at its own cadence and an external producer answers from its own latest output, so
            // neither is asked to render again; their readers bind what they produce next.
            if ((m_sources[index] is null) && (m_producers[index] is null)) {
                _ = m_withdrawing.Add(item: name);
            }
        }
    }
    // Whether an instance's outputs were withdrawn and it has not rendered since.
    private bool IsWithdrawing(int index) => ((m_withdrawing.Count != 0) && m_withdrawing.Contains(item: m_set.Instances[index].Name));
    // An instance that rendered, or that stands paused presenting nothing, no longer owes a render for its withdrawal.
    private void SettleWithdrawal(int index) {
        if (m_withdrawing.Count != 0) {
            _ = m_withdrawing.Remove(item: m_set.Instances[index].Name);
        }
    }
}
