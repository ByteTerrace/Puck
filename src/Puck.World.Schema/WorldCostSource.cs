namespace Puck.World;

/// <summary>The document location of one rule or interaction cost contributor, optionally resolved to authored source.</summary>
/// <param name="Name">The contributor's compiled name.</param>
/// <param name="IsInteraction">Whether the name belongs to the interaction namespace.</param>
/// <param name="JsonPointer">The contributor's indexed location in the analyzed document.</param>
/// <param name="SourcePath">The defining source file, when available.</param>
/// <param name="Line">The one-based defining line, when available.</param>
/// <param name="Column">The one-based defining column, when available.</param>
/// <param name="ModuleInstancePath">The nested module instance path, when available.</param>
public sealed record WorldCostSource(string Name, bool IsInteraction, string JsonPointer,
    string? SourcePath = null, int? Line = null, int? Column = null, string? ModuleInstancePath = null) {
    internal static IReadOnlyList<WorldCostSource> From(WorldDefinition definition) {
        var rules = (definition.Rules ?? []);
        var interactions = (definition.Interactions?.Interactions ?? []);
        var sources = new WorldCostSource[(rules.Count + interactions.Count)];

        for (var index = 0; (index < rules.Count); index++) {
            sources[index] = new(rules[index].Name.Value, false, $"/rules/{index}");
        }
        for (var index = 0; (index < interactions.Count); index++) {
            sources[(rules.Count + index)] = new(interactions[index].Name.Value, true, $"/interactions/interactions/{index}");
        }
        return Array.AsReadOnly(array: sources);
    }
}
public sealed partial record WorldCostReport {
    /// <summary>Gets document locations keyed by contributor name and namespace. A source compiler can resolve
    /// those locations to defining files and module instances without changing any cost.</summary>
    public IReadOnlyList<WorldCostSource> ContributorSources { get; init; } = [];
}
