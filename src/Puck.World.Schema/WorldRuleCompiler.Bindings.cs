namespace Puck.World;

public static partial class WorldRuleCompiler {
    // The bindings the rule or interaction being compiled may name — set by Compile/CompileInteraction for the
    // duration of one compile, read wherever a key or body-reference token is resolved.
    [ThreadStatic]
    private static RuleBinding[]? s_bindingScope;

    private static RuleBinding BindingOfKeyToken(string? key) {
        foreach (var (binding, keyToken, _) in WorldRuleFacts.Bindings) {
            if (string.Equals(
                a: key,
                b: keyToken,
                comparisonType: StringComparison.Ordinal
            )) {
                return binding;
            }
        }

        return RuleBinding.None;
    }
    private static RuleBinding BindingOfBodyToken(string token) {
        foreach (var (binding, keyToken, _) in WorldRuleFacts.Bindings) {
            if (string.Equals(
                a: token,
                b: WorldRuleFacts.BodyTokenOf(keyToken: keyToken),
                comparisonType: StringComparison.Ordinal
            )) {
                return binding;
            }
        }

        return RuleBinding.None;
    }

    // The body-reference grammar as a refusal spells it; the bound tokens come from the same table the parser reads.
    private static readonly string s_bodyRefVocabulary =
        (("a 'body:<n>', 'argmax:<row>'/'argmin:<row>', 'cell:<row>:<key>', or a bound " +
        string.Join(
            separator: '/',
            values: WorldRuleFacts.Bindings.Select(selector: static entry => $"'{WorldRuleFacts.BodyTokenOf(keyToken: entry.KeyToken)}'")
        )) +
        " reference");
    private static readonly string s_bindingScopes = string.Join(
        separator: ", ",
        values: WorldRuleFacts.Bindings.Select(selector: static (entry, index) => $"'{entry.KeyToken}' {((index == 0)
            ? "binds inside "
            : "inside ")}{entry.Scope}")
    );

    private static void RequireBindingInScope(RuleBinding binding, string spelled, string ruleName, string where) {
        if (Array.IndexOf(
            array: (s_bindingScope ?? []),
            value: binding
        ) < 0) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"{where} names '{spelled}', which is not bound here — {s_bindingScopes}"
            );
        }
    }
    // How many ':'-separated tokens the body reference starting at 'start' spends: 'cell:<row>:<key>' spends three,
    // a binding token ('each'/'left'/'right') one, every other kind two.
    private static int BodyRefTokenWidth(string[] tokens, int start) {
        if (start >= tokens.Length) {
            return 2;
        }

        if (string.Equals(
            a: tokens[start],
            b: "cell",
            comparisonType: StringComparison.Ordinal
        )) {
            return 3;
        }

        return ((BindingOfBodyToken(token: tokens[start]) != RuleBinding.None)
            ? 1
            : 2
        );
    }
}
