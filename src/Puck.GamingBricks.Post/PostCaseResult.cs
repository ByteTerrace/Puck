namespace Puck.GamingBricks.Post;

/// <summary>One case's row inside a stage's outcome: its name, how it compared to what was expected, a one-line detail,
/// and how long it took to measure.</summary>
/// <param name="Name">The case's stable name, unique within its stage (a ledger row's <c>path[model]</c>, a vector family).</param>
/// <param name="Verdict">How the case compared to what was expected of it.</param>
/// <param name="Detail">A one-line summary or reason.</param>
/// <param name="Duration">The wall-clock time spent measuring the case.</param>
public sealed record PostCaseResult(string Name, PostCaseVerdict Verdict, string Detail, TimeSpan Duration);
