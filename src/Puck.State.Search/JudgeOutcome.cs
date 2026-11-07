namespace Puck.State;

/// <summary>What a judge answered for one candidate's position: whether it wrote the arena, and whether any rule or
/// score it ran faulted. A faulted candidate is not a scored one: the search step that met it decides nothing and
/// restarts.</summary>
/// <param name="Applied">Whether the judge wrote the arena.</param>
/// <param name="Faulted">Whether a judge rule faulted.</param>
public readonly record struct JudgeOutcome(bool Applied, bool Faulted);
