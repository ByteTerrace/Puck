namespace Puck.World.Transpiler.Vocabulary;

/// <summary>A construct the table deliberately carries no row for, and why.</summary>
/// <param name="Keyword">The construct's keyword, or the shape's name when it is not keyword-led.</param>
/// <param name="Reason">Why the table's own shape cannot carry it.</param>
public sealed record WorldConstructExclusion(string Keyword, string Reason);
