namespace Puck.World.Transpiler.Vocabulary;

/// <summary>What the printer requires of a document node before it may print that node as its construct, and what
/// it prints when the requirement fails.</summary>
/// <param name="AdmittedKeys">Keys the spelling can carry that no described member fills — a key the emitter adds
/// rather than the author writing it. Unless <paramref name="Open"/>, a node carrying any key outside the union of
/// these and the described members' own keys has no spelling and takes <paramref name="Fallback"/>.</param>
/// <param name="Condition">The part of the requirement a key list cannot state, or <see langword="null"/> when
/// the key lists are the whole of it.</param>
/// <param name="Fallback">What the printer prints instead when the requirement fails.</param>
/// <param name="Open">Whether the spelling carries any field of its document node, printing an undescribed one as
/// an ordinary property, rather than admitting a closed set of keys.</param>
/// <param name="Printed">Whether the printer ever produces this spelling. A construct the printer cannot
/// reproduce fails the projection law unless something else prints what it lowered to, which is what
/// <paramref name="Fallback"/> names.</param>
/// <param name="RequiredKeys">Keys a node must carry before the spelling applies, beyond the keys the construct's
/// own required members fill.</param>
/// <param name="FromRows">Whether the printer reads the construct back from the rows it generated — names in the
/// generated form (<c>Puck.State.GeneratedName</c>) — rather than from one document node, so
/// <paramref name="Condition"/> is the whole requirement and the key lists say nothing.</param>
public sealed record WorldConstructSugar(
    string Fallback,
    IReadOnlyList<string>? AdmittedKeys = null,
    IReadOnlyList<string>? RequiredKeys = null,
    string? Condition = null,
    bool Open = false,
    bool Printed = true,
    bool FromRows = false
) {
    /// <summary>The requirement for a compile-time construct the printer reads back from the rows it generated.</summary>
    /// <param name="condition">What those rows must be for the construct to print back.</param>
    /// <returns>The requirement; anything else is refused by naming the generated row.</returns>
    public static WorldConstructSugar ReadBack(string condition) => new(
        Condition: condition,
        Fallback: "a refusal naming the generated row, since printing it as an authored name writes a source the compiler refuses",
        FromRows: true,
        Open: true
    );

    /// <summary>The requirement for a construct of the compile-time layer, which reaches no document member of
    /// its own.</summary>
    public static WorldConstructSugar None { get; } = new(
        Fallback: "the statements its expansion produced",
        Open: true,
        Printed: false
    );

    /// <summary>Gets the keys the spelling can carry that no described member fills.</summary>
    public IReadOnlyList<string> AdmittedKeys { get; init; } = (AdmittedKeys ?? []);
    /// <summary>Gets the keys a node must carry beyond its required members' own.</summary>
    public IReadOnlyList<string> RequiredKeys { get; init; } = (RequiredKeys ?? []);
}
