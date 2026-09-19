using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One rule-edge latch entry on the wire.</summary>
/// <param name="Rule">The rule's name.</param>
/// <param name="Key">The bound cell key's NAME, or the empty string when the binding is a participant index or
/// the rule was evaluated once. The name rather than its ordinal, because a catalog that interned its keys in
/// another order gives the same name another ordinal.</param>
/// <param name="Left">The bound participant index, meaningful only when <paramref name="Key"/> is empty.</param>
/// <param name="Right">The bound pair's right participant index, or -1.</param>
/// <param name="Held">Whether the gate held at the last evaluation.</param>
public readonly record struct WorldRuleLatchEntry(string Rule, string Key, int Left, int Right, bool Held);
/// <summary>One rule group's progress on the wire.</summary>
/// <param name="Group">The group's name.</param>
/// <param name="Step">The pass count for a fixpoint group, or the step cursor for a staged one.</param>
/// <param name="Running">Whether the group is open.</param>
/// <param name="Breached">Whether the group stopped on its pass ceiling.</param>
public readonly record struct WorldRuleGroupEntry(string Group, int Step, bool Running, bool Breached);
/// <summary>This server's own checkpointed fields — journal, base/definition documents, buffered pending ops,
/// step clock, rule-edge latches, rule-group progress, and per-binding decisions. Every other subsystem's own
/// section lives beside this one on <see cref="WorldAuthorityCheckpoint"/>.</summary>
public sealed record WorldServerCheckpoint(
    byte[] DefinitionJson,
    byte[] BaseDefinitionJson,
    string BaseOrigin,
    IReadOnlyList<(ulong Tick, ulong EngineTick, WorldMutation Mutation)> Journal,
    ulong LastCompletedTick,
    ulong LastCompletedEngineTicks,
    ulong LastStepTicks,
    IReadOnlyList<IntentSubmission> Intents,
    IReadOnlyList<WorldPendingOpCheckpoint> Pending,
    IReadOnlyList<WorldRuleLatchEntry> RuleGateHeld,
    IReadOnlyList<WorldRuleLatchEntry> InteractionGateHeld,
    IReadOnlyList<WorldRuleGroupEntry> RuleGroups,
    WorldDocumentSubmissionReceipt? LastDocumentReceipt,
    int SolidRevision,
    ulong? MusicClockElapsedTicks,
    string? MusicDirectorCurrentSegmentId,
    Puck.Audio.Simulation.MusicTransition? MusicDirectorArmed,
    ulong MusicDirectorTransitionCount,
    ulong? MusicDirectorLastTransitionTick,
    string? MusicDirectorLastTransitionFromSegmentId,
    string? MusicDirectorLastTransitionToSegmentId,
    string? MusicDirectorLastEmbellishmentPatchId,
    ulong? MusicDirectorLastEmbellishmentTick,
    IReadOnlyList<WorldDecisionCheckpoint> Decisions
);
