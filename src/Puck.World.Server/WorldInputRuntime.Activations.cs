using Puck.Commands;
using System.Runtime.InteropServices;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    private readonly List<(Principal Owner, ulong Activation)> m_evaluationActivations = [];
    private readonly List<ulong> m_activationScratch = [];
    private bool m_observingEvaluation;

    internal void BeginActivationScope() {
        m_evaluationActivations.Clear();
        m_observingEvaluation = true;
    }

    internal void EndActivationScope() {
        m_observingEvaluation = false;
        m_evaluationActivations.Clear();
    }

    internal void ObserveActivation(Principal principal, ulong activation) {
        if (!m_observingEvaluation || activation == 0) { return; }
        var cause = (principal, activation);
        if (!m_evaluationActivations.Contains(cause)) { m_evaluationActivations.Add(cause); }
    }

    internal void ObserveHeld(Principal principal, ushort channel) {
        if (!m_observingEvaluation || !TryGet(principal, out var owner)) { return; }
        if (owner!.TryHeld(channel, out var held)) { ObserveActivation(principal, held.Origin.Event.Activation); }
        // The session's pressed-since image can be nonzero after a press and release in one interval.
        // It still has a transition cause even though no held origin remains at the rule read.
        for (var index = owner.Count - 1; index >= 0; index--) {
            var record = owner.Record(index);
            if (record.Admission.Tick < Interval.Tick) { break; }
            if (record.Event.Channel == channel && record.Event.Kind == WorldInputEventKind.Press) {
                ObserveActivation(principal, record.Event.Activation);
            }
        }
    }

    internal void ObserveBodyChannel(int body, ushort channel) {
        if (!m_observingEvaluation) { return; }
        foreach (var owner in m_ordered) {
            if (owner.Body == body) { ObserveHeld(owner.Principal, channel); }
        }
    }

    private ReadOnlySpan<ulong> ActivationCauses(Principal principal, ulong explicitActivation = 0) {
        m_activationScratch.Clear();
        if (explicitActivation != 0) { m_activationScratch.Add(explicitActivation); }
        foreach (var cause in m_evaluationActivations) {
            if (cause.Owner == principal && cause.Activation != explicitActivation) { m_activationScratch.Add(cause.Activation); }
        }
        return CollectionsMarshal.AsSpan(m_activationScratch);
    }
}
