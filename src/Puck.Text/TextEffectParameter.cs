namespace Puck.Text;

/// <summary>
/// One numeric effect parameter that may be late-bound to a content-time channel. It carries a
/// <see cref="BaseValue"/> and, optionally, a <see cref="VariableHash"/> naming a <see cref="TextEnrichmentVariable"/>
/// to fold in via <see cref="BindingMode"/> at resolve time. With no variable it is just the base constant; the whole
/// binding system is determinism-clean because <see cref="Evaluate"/> is pure arithmetic over caller-supplied values.
/// </summary>
/// <param name="BaseValue">The literal base value (used directly when no variable is bound or a bound variable is absent).</param>
/// <param name="VariableHash">The FNV-1a name hash of the bound content-time channel, or <c>0</c> for an unbound constant.</param>
/// <param name="BindingMode">How the bound variable folds onto <see cref="BaseValue"/>.</param>
public readonly record struct TextEffectParameter(
    float BaseValue,
    uint VariableHash = 0,
    TextEffectParameterBindingMode BindingMode = TextEffectParameterBindingMode.Multiplicative
) {
    /// <summary>Resolves the parameter against the current content-time channels.</summary>
    /// <param name="variables">The named channels in effect, or <see langword="null"/> for none.</param>
    /// <returns>The base value when unbound or the named channel is absent; otherwise the base folded with the channel
    /// per <see cref="BindingMode"/>. Non-finite inputs fall back to the finite base (or zero).</returns>
    public float Evaluate(IReadOnlyList<TextEnrichmentVariable>? variables) {
        var baseValue = (float.IsFinite(f: BaseValue) ? BaseValue : 0.0f);

        if ((VariableHash == 0) || (variables is null)) {
            return baseValue;
        }

        foreach (var variable in variables) {
            if (variable.NameHash != VariableHash) {
                continue;
            }

            if (!float.IsFinite(f: variable.Value)) {
                return baseValue;
            }

            var evaluatedValue = BindingMode switch {
                TextEffectParameterBindingMode.Replacement => variable.Value,
                TextEffectParameterBindingMode.Additive => (baseValue + variable.Value),
                _ => (baseValue * variable.Value)
            };

            return (float.IsFinite(f: evaluatedValue) ? evaluatedValue : baseValue);
        }

        return baseValue;
    }
}
