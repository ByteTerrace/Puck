using System.Globalization;

namespace Puck.Cli;

// The argument parser the verbs share: bool flags, valued flags (-Name value) and bare positionals, in
// any order. Names are canonicalized (leading dashes trimmed, inner dashes dropped, lowercased), so
// -NoBlocks == --no-blocks == --noblocks and -Out-Dir == -OutDir. Every occurrence of a valued flag is
// retained: Get answers with the last one, GetAll with all of them in command-line order.
internal sealed class ArgScanner {
    private readonly Dictionary<string, bool> m_spec = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> m_values = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_present = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_positionals = [];

    public string? Error { get; private set; }
    public IReadOnlyList<string> Positionals => m_positionals;

    public ArgScanner Flag(string name) {
        m_spec[Canonical(name: name)] = false;

        return this;
    }
    public ArgScanner Value(string name) {
        m_spec[Canonical(name: name)] = true;

        return this;
    }
    public bool Parse(string[] args) {
        for (var index = 0; (index < args.Length); index++) {
            var argument = args[index];

            if (!argument.StartsWith(value: '-')) {
                m_positionals.Add(item: argument);

                continue;
            }

            var name = Canonical(name: argument);

            if (!m_spec.TryGetValue(key: name, value: out var takesValue)) {
                Error = $"unknown argument '{argument}'.";

                return false;
            }

            if (!takesValue) {
                m_present.Add(item: name);

                continue;
            }

            if ((index + 1) >= args.Length) {
                Error = $"argument '{argument}' requires a value.";

                return false;
            }

            if (!m_values.TryGetValue(key: name, value: out var occurrences)) {
                m_values[name] = occurrences = [];
            }

            occurrences.Add(item: args[++index]);
            m_present.Add(item: name);
        }

        return true;
    }
    public bool Has(string name) =>
        m_present.Contains(item: Canonical(name: name));
    public string? Get(string name) =>
        (m_values.TryGetValue(key: Canonical(name: name), value: out var occurrences) ? occurrences[^1] : null);
    // Every occurrence of a repeatable valued flag (-g, --not), in command-line order.
    public IReadOnlyList<string> GetAll(string name) =>
        (m_values.TryGetValue(key: Canonical(name: name), value: out var occurrences) ? occurrences : []);
    public bool TryGetInt(string name, out int value) =>
        int.TryParse(s: Get(name: name), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out value);

    private static string Canonical(string name) =>
        name.TrimStart(trimChar: '-').Replace(newValue: string.Empty, oldValue: "-").ToLowerInvariant();
}
