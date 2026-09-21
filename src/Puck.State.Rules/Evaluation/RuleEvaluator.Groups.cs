using System.Globalization;

namespace Puck.State.Rules;

public sealed partial class RuleEvaluator {
    private readonly List<ulong> m_groupVersions = [];

    private CompiledRuleGroup[]? m_configuredUndoGroups;

    /// <summary>Evaluates every compiled group of a section, in document order, against the progress held beside the
    /// latch. A group's members are evaluated only through their group; a caller that also evaluates the whole rule
    /// array would run them twice.</summary>
    /// <param name="rules">The section's compiled rules, in the order the compiler produced them.</param>
    /// <param name="groups">The section's compiled groups.</param>
    /// <param name="state">The group progress, which hashes and checkpoints beside the latch.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any member's firing moved the arena.</returns>
    public bool EvaluateGroups(CompiledRule[] rules, CompiledRuleGroup[] groups, RuleGroupState state, RuleLatch latch, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rules);
        ArgumentNullException.ThrowIfNull(argument: state);

        var applied = false;

        if ((m_host is IArenaUndoHost undoHost) && !ReferenceEquals(objA: m_configuredUndoGroups, objB: groups)) {
            List<ArenaUndoPlan>? plans = null;

            foreach (var group in groups) {
                if (group.Undo is not null) {
                    (plans ??= []).Add(group.Undo);
                }
            }
            undoHost.ConfigureUndo(plans: ((plans is null) ? Array.Empty<ArenaUndoPlan>() : plans));
            m_configuredUndoGroups = groups;
        }

        foreach (var group in groups) {
            if ((m_host is IArenaUndoHost suppressingUndo) && suppressingUndo.UndoGroupSuppressed(group: group.Name, tick: m_host.Tick)) {
                continue;
            }
            var armed = Armed(group: group);

            // A trigger that reads false re-arms the group: a breached fixpoint group runs again only after its
            // trigger has gone false and holds once more.
            if (!armed) {
                if ((group.Undo is not null) && (m_host is IArenaUndoHost inactiveUndo) && inactiveUndo.UndoTurnPending(group: group.Name)) {
                    inactiveUndo.CancelUndoTurn(group: group.Name);
                }
                state.Set(
                    name: group.Name,
                    progress: default
                );

                continue;
            }

            BeginUndoIfNeeded(group: group);
            var attributed = ((group.Undo is not null) && (m_host is IArenaUndoHost));

            if (attributed) {
                ((IArenaUndoHost)m_host).BeginUndoPass(group: group.Name);
            }
            try {
                applied |= ((group.Shape == RuleGroupShape.Fixpoint)
                    ? RunFixpoint(group: group, latch: latch, rules: rules, state: state, stepTicks: stepTicks)
                    : RunStaged(group: group, latch: latch, rules: rules, state: state, stepTicks: stepTicks)
                );
            } finally {
                if (attributed) {
                    ((IArenaUndoHost)m_host).EndUndoPass(group: group.Name);
                }
            }
        }

        return applied;
    }

    private bool Armed(CompiledRuleGroup group) {
        if (group.Trigger.Length == 0) {
            return true;
        }
        if (!TryEvaluateLocals(
            declared: group.Locals,
            owner: group.Name,
            tick: m_host.Tick
        )) {
            return false;
        }

        var open = GateOpen(
            faulted: out var faulted,
            gate: group.Trigger,
            ruleName: group.Name
        );

        return (open && !faulted);
    }
    // One pass per tick over every member, in authored order. The pass closes the group when no row of its write
    // set moved: RowVersion moves only on a commit that left the row's bytes different, so a member that wrote and
    // then rewound is not progress.
    private bool RunFixpoint(CompiledRuleGroup group, CompiledRule[] rules, RuleGroupState state, RuleLatch latch, ulong stepTicks) {
        var progress = state.Progress(name: group.Name);

        if (progress.Breached) {
            return false;
        }
        m_groupVersions.Clear();
        foreach (var row in group.WriteRows) {
            m_groupVersions.Add(item: (m_host.TryRowVersion(
                rowOrdinal: row,
                version: out var version
            )
                ? version
                : 0UL
            ));
        }

        var applied = false;

        foreach (var member in group.Members) {
            _ = EvaluateRule(
                applied: out var moved,
                latch: latch,
                rule: rules[member],
                stepTicks: stepTicks
            );
            applied |= moved;
        }

        var changed = false;

        for (var index = 0; (index < group.WriteRows.Length); index++) {
            var version = (m_host.TryRowVersion(
                rowOrdinal: group.WriteRows[index],
                version: out var current
            )
                ? current
                : 0UL
            );

            changed |= (version != m_groupVersions[index]);
        }

        if (!changed) {
            CommitUndoIfPending(group: group);
            state.Set(
                name: group.Name,
                progress: default
            );

            return applied;
        }

        var passes = (progress.Step + 1);

        if (passes >= group.Passes) {
            CommitUndoIfPending(group: group);
            ReportRefusal(
                detail: $"ran its whole ceiling of {group.Passes.ToString(provider: CultureInfo.InvariantCulture)} pass(es) without a pass leaving its write set unchanged",
                effect: $"group '{group.Name}'",
                refusal: RuleEffectRefusal.GroupPassCeiling,
                ruleName: group.Name,
                tick: m_host.Tick
            );
            // A breach latches only where a trigger can lift it. A group with no trigger is always armed, so
            // latching would end it for the rest of the run; instead the breach is reported and the group starts a
            // fresh set of passes next tick, which is what a cascade that did not settle in one tick needs.
            state.Set(
                name: group.Name,
                progress: new RuleGroupProgress(
                    Breached: (group.Trigger.Length > 0),
                    Running: false,
                    Step: 0
                )
            );

            return applied;
        }

        state.Set(
            name: group.Name,
            progress: new RuleGroupProgress(
                Breached: false,
                Running: true,
                Step: passes
            )
        );

        return applied;
    }
    // One step per tick. The cursor advances when the step's own firing commits, stalls when it refuses or its gate
    // is closed, and skips past a refusal when the step declares it.
    private bool RunStaged(CompiledRuleGroup group, CompiledRule[] rules, RuleGroupState state, RuleLatch latch, ulong stepTicks) {
        var progress = state.Progress(name: group.Name);

        if (!progress.Running) {
            progress = new RuleGroupProgress(
                Breached: false,
                Running: true,
                Step: 0
            );
        }

        var step = progress.Step;

        if (((uint)step) >= ((uint)group.Members.Length)) {
            state.Set(
                name: group.Name,
                progress: default
            );

            return false;
        }

        var outcome = EvaluateRule(
            applied: out var applied,
            latch: latch,
            rule: rules[group.Members[step]],
            stepTicks: stepTicks
        );
        var advance = ((outcome == RuleOutcome.Fired) || ((outcome == RuleOutcome.Refused) && (group.Policies[step] == RuleGroupStepPolicy.Skip)));

        if (!advance) {
            state.Set(
                name: group.Name,
                progress: progress
            );

            return applied;
        }

        var next = (step + 1);

        state.Set(
            name: group.Name,
            progress: ((next > group.TerminalStep)
                ? default
                : new RuleGroupProgress(
                    Breached: false,
                    Running: true,
                    Step: next
                ))
        );

        if (next > group.TerminalStep) {
            CommitUndoIfPending(group: group);
        }

        return applied;
    }
    private void BeginUndoIfNeeded(CompiledRuleGroup group) {
        if ((group.Undo is not null) && (m_host is IArenaUndoHost undo) && !undo.UndoTurnPending(group: group.Name)) {
            undo.BeginUndoTurn(group: group.Name);
        }
    }
    private void CommitUndoIfPending(CompiledRuleGroup group) {
        if ((group.Undo is not null) && (m_host is IArenaUndoHost undo) && undo.UndoTurnPending(group: group.Name)) {
            undo.CommitUndoTurn(group: group.Name);
        }
    }
    // A group's trigger carries its own bindings, which its binding keys address. They are evaluated fresh every
    // tick: a trigger runs once per tick, so memoizing it would cost more than it saves.
    private bool TryEvaluateLocals(CompiledRuleLocal[] declared, string owner, ulong tick) {
        for (var ordinal = 0; (ordinal < declared.Length); ordinal++) {
            var bound = declared[ordinal];

            if (!RuleExpressions.TryEvaluate(
                fault: out var fault,
                kind: bound.CarrierKind,
                program: bound.Expression,
                reader: m_host,
                value: out var value
            )) {
                ReportRefusal(
                    detail: RuleEvaluation.DescribeFault(fault: fault),
                    effect: $"binding '{bound.Name}'",
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: owner,
                    tick: tick
                );

                return false;
            }

            m_host.Locals[ordinal] = value;
        }

        return true;
    }
}
