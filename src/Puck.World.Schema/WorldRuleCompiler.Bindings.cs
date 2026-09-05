namespace Puck.World;

public static partial class WorldRuleCompiler {
    // The bindings the rule or interaction being compiled may name — set by Compile/CompileInteraction for the
    // duration of one compile, read wherever a key or body-reference token is resolved.
    [ThreadStatic]
    private static RuleBinding[]? s_bindingScope;
    // The rule-scoped bound values the rule being compiled has declared SO FAR — a binding's own expression sees
    // only the bindings declared before it; the gate and effects see all of them.
    [ThreadStatic]
    private static List<CompiledRuleBinding>? s_ruleBindings;
    private static CompiledRuleBinding[] CompileBindings(WorldRule rule, WorldDefinition definition) {
        if (rule.Bindings is not { Count: > 0 } authored) {
            return [];
        }
        if (authored.Count > WorldRuleCapacity.MaxBindingsPerRule) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectKindInadmissible,
                ruleName: rule.Name,
                detail: $"declares {authored.Count} bindings, exceeding the {WorldRuleCapacity.MaxBindingsPerRule}-binding ceiling"
            );
        }
        var compiled = new List<CompiledRuleBinding>(capacity: authored.Count);
        s_ruleBindings = compiled;
        foreach (var binding in authored) {
            var name = (binding?.Name.Value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.NameMissing, ruleName: rule.Name, detail: "a binding declares a name");
            }
            if (binding!.Kind is not (CellKind.Int or CellKind.Fixed)) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: rule.Name, detail: $"binding '{name}' is kind={DescribeCellKind(kind: binding.Kind)} — a bound value is int or fixed");
            }
            foreach (var earlier in compiled) {
                if (string.Equals(a: earlier.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                    throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: rule.Name, detail: $"binding '{name}' is declared twice");
                }
            }
            compiled.Add(item: new CompiledRuleBinding(
                Name: name,
                Kind: binding.Kind,
                Expression: CompileExpression(expression: binding.Expression, kind: binding.Kind, ruleName: rule.Name, verb: $"binding '{name}'", definition: definition)
            ));
        }
        return [.. compiled];
    }
    // $table:<name>:<key> for a single-value table, $table:<name>:<column>:<key> for a column table — the table is
    // resolved and loaded at compile so a literal key is proven present and the value kind is the table's own; a
    // dynamic key ($cell:<row>:<key>, $each, or $bind:<name>) is read at evaluation.
    private static ResolvedOperand ResolveTableOperand(string name, string? key, string ruleName, WorldDefinition definition, string keyFieldLabel) {
        RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);
        var rest = name[WorldRuleFacts.TablePrefix.Length..];
        var firstColon = rest.IndexOf(value: ':');
        if (firstColon <= 0 || firstColon == rest.Length - 1) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.TablePrefix}<table>:<key>' or '{WorldRuleFacts.TablePrefix}<table>:<column>:<key>'");
        }
        string[] tokens = [rest[..firstColon], rest[(firstColon + 1)..]];
        var tables = (definition.Tables ?? []);
        var ordinal = -1;
        for (var index = 0; index < tables.Count; index++) {
            if (string.Equals(a: tables[index].Name, b: tokens[0], comparisonType: StringComparison.Ordinal)) {
                ordinal = index;
                break;
            }
        }
        if (ordinal < 0) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'{name}' names table '{tokens[0]}', which the document's tables do not declare");
        }
        if (!CompiledWorldTable.TryCompile(row: tables[ordinal], table: out var table, error: out var error)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'{name}': table '{tokens[0]}' cannot load — {error}");
        }
        var spelledKey = tokens[1];
        var column = 0;
        if (table!.ColumnNames.Count > 0) {
            var columnColon = spelledKey.IndexOf(value: ':');
            var columnName = (columnColon < 0) ? spelledKey : spelledKey[..columnColon];
            column = table.Column(name: columnName);
            if (column < 0 || columnColon < 0 || columnColon == spelledKey.Length - 1) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}': table '{tokens[0]}' has columns [{string.Join(separator: ", ", values: table.ColumnNames)}] and is read as '{WorldRuleFacts.TablePrefix}{tokens[0]}:<column>:<key>'");
            }
            spelledKey = spelledKey[(columnColon + 1)..];
        }
        CompiledCellRef? keyFrom = null;
        var keyBinding = -1;
        var literal = 0L;
        if (spelledKey.StartsWith(WorldRuleFacts.BindPrefix, StringComparison.Ordinal)) {
            var bound = ResolveBindingOperand(name: spelledKey, key: null, ruleName: ruleName, keyFieldLabel: keyFieldLabel);
            if (bound.ValueKind != CellKind.Int) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' key '{spelledKey}' is a fixed binding — a table key is an int binding");
            }
            keyBinding = ((BindingOperand)bound.Operand.Value!).Ordinal;
        } else if (TryResolveDynamicKey(key: spelledKey, ruleName: ruleName, definition: definition, verb: name, keyFieldLabel: "key", cell: out var dynamic)) {
            keyFrom = dynamic;
        } else if (!long.TryParse(s: spelledKey, style: System.Globalization.NumberStyles.AllowLeadingSign, provider: System.Globalization.CultureInfo.InvariantCulture, result: out literal)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' key '{spelledKey}' is not an integer, a '{WorldRuleFacts.CellKeyPrefix}<row>:<key>' indirection, a '{WorldRuleFacts.BindPrefix}<name>' binding, or a bound key token");
        } else if (!table.TryLookup(key: literal, column: column, raw: out _)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUndeclared, ruleName: ruleName, detail: $"'{name}' names key {literal}, which table '{tokens[0]}' does not carry");
        }
        return new ResolvedOperand(
            Operand: new CompiledWorldOperand(new TableOperand(tableOrdinal: ordinal, table: tokens[0], key: literal, keyFrom: keyFrom, keyBinding: keyBinding, column: column, entryCount: table.Count, valueKind: table.Kind)),
            ValueKind: table.Kind,
            Describe: name
        );
    }
    private static ResolvedOperand ResolveBindingOperand(string name, string? key, string ruleName, string keyFieldLabel) {
        RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);
        var bound = name[WorldRuleFacts.BindPrefix.Length..];
        var scope = (s_ruleBindings ?? []);
        for (var ordinal = 0; ordinal < scope.Count; ordinal++) {
            if (string.Equals(a: scope[ordinal].Name, b: bound, comparisonType: StringComparison.Ordinal)) {
                return new ResolvedOperand(
                    Operand: new CompiledWorldOperand(new BindingOperand(ordinal: ordinal, name: bound, valueKind: scope[ordinal].Kind)),
                    ValueKind: scope[ordinal].Kind,
                    Describe: name
                );
            }
        }
        throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateCellUnaddressable,
            ruleName: ruleName,
            detail: $"'{name}' names no binding declared before it — a rule's 'bindings' list is read in declared order by later bindings, the gate, and the effects"
        );
    }

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
