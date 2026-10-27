using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    private readonly List<(Owner Owner, WorldInputOwnerCheckpoint Input, int Generation, string? KitName)> m_kitUndo = [];
    private WorldInputHoldCheckpoint? m_kitHoldUndo;
    private bool m_kitStep;
    private int m_kitPopulationRevision;
    private int m_kitStateMark;
    private readonly List<(WorldBody Body, WorldBody.StepScratch Scratch)> m_kitForeignUndo = [];
    private readonly Stack<WorldBody.StepScratch> m_kitForeignScratch = [];

    /// <summary>Opens a body step's lazy input undo scope; unchanged ticks retain no checkpoint arrays.</summary>
    internal void BeginKitStep() {
        m_kitStep = true;
        m_kitPopulationRevision = m_host.Population.Revision;
        m_kitUndo.Clear();
        m_kitHoldUndo = null;
        m_kitStateMark = m_host.Arena.BeginScope();
    }

    private void SaveKitInput(Owner owner) {
        if (!m_kitStep) { return; }
        foreach (var saved in m_kitUndo) { if (ReferenceEquals(saved.Owner, owner)) { return; } }
        m_kitHoldUndo ??= m_host.InputHold.Capture();
        m_kitUndo.Add((owner, owner.Input.Capture(), owner.Generation, owner.KitName));
    }

    /// <summary>Commits input barriers with an admitted step, or restores them with a refused sweep.</summary>
    internal void CompleteKitStep(bool accepted) {
        m_kitStep = false;
        if (accepted) { m_host.Arena.Commit(m_kitStateMark); }
        else { m_host.Arena.Rewind(m_kitStateMark); }
        if (!accepted && m_kitUndo.Count != 0) {
            foreach (var saved in m_kitUndo) {
                saved.Owner.Input.Restore(saved.Input);
                saved.Owner.Generation = saved.Generation;
                saved.Owner.KitName = saved.KitName;
            }
            m_host.Population.RestoreInputEffectSelections(m_host.Definition, m_kitPopulationRevision);
            foreach (var saved in m_kitUndo) {
                if (OwnsBodyMapping(saved.Owner)) { m_host.Population.RestoreInputMapping(saved.Owner.Body, saved.Owner.Input); }
            }
            m_host.InputHold.Restore(m_kitHoldUndo!);
        }
        foreach (var saved in m_kitForeignUndo) {
            if (!accepted) { saved.Body.RestoreMotion(saved.Scratch); }
            m_kitForeignScratch.Push(saved.Scratch);
        }
        m_kitForeignUndo.Clear();
        m_kitUndo.Clear();
        m_kitHoldUndo = null;
    }

    /// <summary>Applies an already validated firing's foreign state member inside the same body-step undo scope.</summary>
    internal void ApplyKitForeignState(int sourceIndex, int targetIndex, CompiledBodyInstruction instruction) {
        if (m_host.Body(targetIndex) is not { } target) { return; }
        if (m_kitStep && sourceIndex != targetIndex) {
            var captured = false;
            foreach (var saved in m_kitForeignUndo) { captured |= ReferenceEquals(saved.Body, target); }
            if (!captured) {
                var scratch = m_kitForeignScratch.TryPop(out var reused) ? reused : new WorldBody.StepScratch();
                target.CaptureMotion(scratch);
                m_kitForeignUndo.Add((target, scratch));
            }
        }
        _ = target.ApplyTargetedEffect(sourceIndex, instruction, validatedFiring: true);
    }

    /// <summary>Installs a successful kit firing's selector changes before another input consumer runs.</summary>
    internal void CommitKitEffects(int body, int logicalChannel = -1) {
        if (m_ordered.Count == 0 || m_host.Population.InputCatalog.Families.Length == 0) { return; }
        // Synchronize the owner before retuning a kit: its old bank and edge state are the barrier's source.
        foreach (var owner in m_ordered) {
            var activation = owner.Body == body && logicalChannel >= 0 && owner.Input.TryHeld((ushort)logicalChannel, out var held)
                ? held.Origin.Event.Activation : 0;
            if (!TrySynchronize(owner, activation, out var reason)) { throw new InvalidOperationException(reason); }
        }
        m_host.Population.SyncBodyCells(m_host.Definition);
    }

    /// <summary>Checks an automatic counter reset before it changes a selector.</summary>
    internal bool TryValidateReset(int body, int slot, long raw, out string reason) {
        var effect = new CompiledBodyInstruction(BodyMotionOp.SetState, FixedQ4816.FromRawBits(raw), default, 0, slot);
        return TryValidateEffects(body, new ReadOnlySpan<CompiledBodyInstruction>(in effect), default, out reason);
    }

    /// <summary>Checks a kit firing's complete proposed register writes before any motion or delivery happens.</summary>
    internal bool TryValidateEffects(int body, ReadOnlySpan<CompiledBodyInstruction> effects, BodyEffectTargets targets, out string reason) {
        reason = string.Empty;
        if (m_ordered.Count == 0 || m_host.Population.InputCatalog.Families.Length == 0) { return true; }
        var writesState = false;
        foreach (var effect in effects) {
            writesState |= effect.Operation is BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer;
        }
        if (!writesState) { return true; }
        var lane = m_host.Population.ActionStateLane;
        var mark = m_host.Arena.BeginScope();
        try {
            foreach (var effect in effects) {
                if (effect.Operation is not (BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer)) { continue; }
                var target = effect.Target == ActionTarget.Self ? body : targets.Resolve(effect.Target);
                if (target < 0 || m_host.Body(target) is null) { continue; }
                var slot = effect.StateSlot;
                if (effect.Target != ActionTarget.Self) {
                    slot = -1;
                    for (var candidate = 0; candidate < lane.Definitions.Count; candidate++) {
                        if (lane.Definitions[candidate].Name == effect.StateName) { slot = candidate; break; }
                    }
                }
                if ((uint)slot >= (uint)lane.Definitions.Count) { continue; }
                var definition = lane.Definitions[slot];
                var requested = effect.Operation switch {
                    BodyMotionOp.AddState => (FixedQ4816.FromRawBits(lane.Read(slot, target)) + effect.Value).Value,
                    BodyMotionOp.StartTimer => checked((long)effect.DurationTicks),
                    _ => effect.Value.Value,
                };
                var effective = definition.Envelope?.Clamp(requested, WorldActionStateLane.InitialRaw(in definition)) ?? requested;
                lane.Write(slot, target, definition.Kind == ActionStateKind.Timer ? Math.Max(0, effective) : effective);
            }
            if (m_host.Arena.Journal.OverCeiling) { reason = "input.mode: kit firing exceeds the state journal allowance"; return false; }
            return TryValidate(out reason);
        } finally {
            m_host.Arena.Rewind(mark);
        }
    }
}
