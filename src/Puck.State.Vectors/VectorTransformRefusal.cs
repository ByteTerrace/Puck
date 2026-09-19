namespace Puck.State;

/// <summary>Why a vector transform refused: the catalogued code a counted refusal reports, and the text naming the
/// row, cell, or parameter that refused.</summary>
/// <param name="Code">The catalogued code — a member of a <see cref="RefusalAttribute"/>-tagged enum, so a shape
/// the compiler decides is a <see cref="RuleRefusal"/> and one a firing decides is a
/// <see cref="RuleEffectRefusal"/>.</param>
/// <param name="Reason">The text naming what refused; empty on success.</param>
public readonly record struct VectorTransformRefusal(Enum Code, string Reason) {
    /// <summary>Gets a value indicating whether this carrier records a refusal rather than a success.</summary>
    public bool IsRefused => !string.IsNullOrEmpty(value: Reason);

    /// <inheritdoc/>
    public override string ToString() => (IsRefused
        ? $"{Code}: {Reason}"
        : "none"
    );
}
