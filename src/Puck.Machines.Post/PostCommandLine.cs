using System.CommandLine;
using System.CommandLine.Parsing;

namespace Puck.Machines.Post;

/// <summary>Parses a battery's options through System.CommandLine while leaving diagnostic tokens to their dispatcher.</summary>
public sealed class PostCommandLine {
    private readonly Dictionary<string, Option<string[]>> m_values = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Option<bool>> m_flags = new(comparer: StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> m_required;
    private readonly ParseResult m_result;
    private readonly string? m_valueError;

    /// <summary>Parses one invocation using the battery's value and switch options.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="values">The value-taking options.</param>
    /// <param name="flags">The switches that take no value.</param>
    /// <param name="requiredValues">The options whose supplied occurrences must have a nonblank value.</param>
    public PostCommandLine(string[] args, string[] values, string[]? flags = null, string[]? requiredValues = null) {
        m_required = new(collection: (requiredValues ?? []), comparer: StringComparer.OrdinalIgnoreCase);
        var command = new RootCommand { TreatUnmatchedTokensAsErrors = false };

        command.Options.Clear();
        foreach (var name in values) {
            var option = new Option<string[]>(name: name) {
                Arity = (m_required.Contains(item: name) ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore),
                AllowMultipleArgumentsPerToken = false,
            };

            m_values.Add(key: name, value: option);
            command.Options.Add(item: option);
        }
        foreach (var name in (flags ?? [])) {
            var option = new Option<bool>(name: name) { Arity = ArgumentArity.Zero };

            m_flags.Add(key: name, value: option);
            command.Options.Add(item: option);
        }
        // Each option binds its following raw token independently, including an option-looking value.
        // Binding before parsing preserves that diagnostic grammar and the first occurrence of repeated options.
        // The value prefix keeps empty strings and literal quotes intact through the parser tokenization.
        var normalized = new List<string>();

        for (var index = 0; (index < args.Length); ++index) {
            if (m_values.TryGetValue(key: args[index], value: out var option)) {
                var value = (((index + 1) < args.Length) ? args[(index + 1)] : null);

                if (m_required.Contains(item: option.Name) &&
                    ((value is null) || string.IsNullOrWhiteSpace(value: value) ||
                        value.StartsWith(comparisonType: StringComparison.Ordinal, value: "--"))) {
                    m_valueError ??= $"{option.Name} requires a value.";
                }
                normalized.Add(item: ((value is null) ? option.Name : $"{option.Name}=v{value}"));
            } else if (m_flags.TryGetValue(key: args[index], value: out var flag)) {
                normalized.Add(item: flag.Name);
            }
        }
        m_result = command.Parse(args: normalized, configuration: new ParserConfiguration {
            EnablePosixBundling = false,
            ResponseFileTokenReplacer = null,
        });
    }

    /// <summary>Returns the first supplied value for an option, or null when it has no value.</summary>
    /// <param name="name">The registered value option's name.</param>
    /// <returns>The first value verbatim, or null.</returns>
    public string? Value(string name) =>
        ((m_result.GetResult(option: m_values[name])?.Tokens.FirstOrDefault()?.Value is { } value) ? value[1..] : null);
    /// <summary>Returns every supplied value for a repeatable option, in occurrence order.</summary>
    /// <param name="name">The registered value option's name.</param>
    /// <returns>The values verbatim, without comma splitting.</returns>
    public string[] Values(string name) =>
        (m_result.GetResult(option: m_values[name])?.Tokens.Select(selector: static token => token.Value[1..]).ToArray() ?? []);
    /// <summary>Reports whether a registered switch was supplied.</summary>
    /// <param name="name">The switch's name.</param>
    /// <returns>Whether the invocation names the switch.</returns>
    public bool Flag(string name) => (m_result.GetResult(option: m_flags[name]) is { Implicit: false });
    /// <summary>Checks supplied options that require a nonblank value.</summary>
    /// <param name="error">The existing battery diagnostic for the first invalid value, or an empty string.</param>
    /// <returns>Whether the invocation supplies every required value.</returns>
    public bool TryValidateValues(out string error) {
        if (m_valueError is not null) {
            error = m_valueError;
            return false;
        }
        foreach (var parseError in m_result.Errors) {
            if ((parseError.SymbolResult is OptionResult option) && m_required.Contains(item: option.Option.Name)) {
                error = $"{option.Option.Name} requires a value.";
                return false;
            }
        }
        error = "";
        return true;
    }
}
