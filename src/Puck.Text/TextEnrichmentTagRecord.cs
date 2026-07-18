namespace Puck.Text;

// The parsed shape of one tag payload: its kind (or None for reset/unknown), any parameters, and the flags the
// left-to-right stack scan acts on (end pops, reset clears, unrecognized is dropped).
internal readonly record struct TextEnrichmentTagRecord(
    TextEffectKind Kind,
    IReadOnlyList<TextEnrichmentTagParameter> Parameters,
    bool IsEnd,
    bool IsReset,
    bool IsRecognized
);
