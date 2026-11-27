using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>
/// A math text paint's resolved text (<see cref="Puck.World.Authoring.PalettePaintTextDocument.Math"/>) read as its lines
/// of math markup: a line that starts with <c>@</c> is a directive and any other line is markup as written. The
/// directives bring in the engine's own formulas (<see cref="SdfFormulaTypesetting"/>), so a panel shows exactly the
/// arithmetic the field's readers run:
/// <list type="bullet">
/// <item><c>@formula &lt;reference&gt; [equation|values|steps|terms|meaning]</c> writes a formula's equation (the
/// default), its equation with every term a <c>@value</c> names written as that value, its step by step, its terms one a
/// line (each with its <c>@meaning</c> and value), or the selected term's line alone. An empty reference writes
/// nothing.</item>
/// <item><c>@value &lt;term&gt; &lt;number&gt; [unit]</c> gives a term its live value.</item>
/// <item><c>@select &lt;index|term&gt;</c> highlights a term: by its place in the first formula's terms from zero, or by
/// name; a negative index selects none.</item>
/// <item><c>@meaning &lt;term&gt; &lt;text&gt;</c> says what a term means.</item>
/// <item><c>@color highlight|live|quiet &lt;#hex&gt;</c> sets the highlighted term's, the live values' and the meanings'
/// colours.</item>
/// </list>
/// </summary>
/// <remarks>Presentation only. A directive this reader does not know, or one whose arguments it cannot read, writes
/// nothing.</remarks>
public static class WorldMathText {
    /// <summary>The highlighted term's colour while no <c>@color highlight</c> names one.</summary>
    public const string DefaultHighlight = "#FFB347";
    /// <summary>The live values' colour while no <c>@color live</c> names one.</summary>
    public const string DefaultLive = "#7FE0D0";
    /// <summary>The meanings' colour while no <c>@color quiet</c> names one.</summary>
    public const string DefaultQuiet = "#A9B4C2";

    /// <summary>Reads a math text's resolved text into its lines of markup and the term it highlights.</summary>
    /// <param name="text">The resolved text, its placeholders already read.</param>
    /// <returns>The expanded text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static WorldMathTextExpansion Expand(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        var lines = text.Split(separator: '\n');
        var values = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var meanings = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var formulas = new List<string>();
        var highlight = DefaultHighlight;
        var live = DefaultLive;
        var quiet = DefaultQuiet;
        string? select = null;

        foreach (var raw in lines) {
            var line = raw.TrimEnd(trimChar: '\r');

            if (!line.StartsWith(value: '@')) { continue; }

            var (verb, rest) = Split(text: line[1..]);

            switch (verb) {
                case "formula":
                    if ((Split(text: rest).Head is { Length: > 0 } reference) && SdfFormulaTypesetting.TryFind(formula: out _, reference: reference)) { formulas.Add(item: reference); }
                    break;
                case "value": {
                    var (name, value) = Split(text: rest);

                    if (name.Length > 0) { values[name] = FormatValue(text: value); }
                    break;
                }
                case "meaning": {
                    var (name, meaning) = Split(text: rest);

                    if (name.Length > 0) { meanings[name] = meaning; }
                    break;
                }
                case "select":
                    select = rest.Trim();
                    break;
                case "color": {
                    var (which, colour) = Split(text: rest);

                    if (HexColor.TryParseRgba(color: out _, value: colour.Trim())) {
                        switch (which) {
                            case "highlight": highlight = colour.Trim(); break;
                            case "live": live = colour.Trim(); break;
                            case "quiet": quiet = colour.Trim(); break;
                        }
                    }
                    break;
                }
            }
        }

        var first = ((formulas.Count > 0) && SdfFormulaTypesetting.TryFind(formula: out var found, reference: formulas[0])) ? found : null;
        var selected = Selected(first: first, select: select);
        var output = new List<string>();

        foreach (var raw in lines) {
            var line = raw.TrimEnd(trimChar: '\r');

            if (!line.StartsWith(value: '@')) {
                output.Add(item: line);
                continue;
            }

            var (verb, rest) = Split(text: line[1..]);

            if ((verb != "formula") || !SdfFormulaTypesetting.TryFind(formula: out var formula, reference: Split(text: rest).Head)) {
                continue;
            }

            switch (Split(text: Split(text: rest).Tail).Head) {
                case "values":
                    output.AddRange(collection: formula.Equation.Select(selector: equation => WithValues(live: live, markup: equation, values: values)));
                    break;
                case "steps":
                    output.AddRange(collection: formula.Steps.Select(selector: static step => (string.Concat(values: Enumerable.Repeat(element: "\\quad ", count: step.Depth)) + step.Markup)));
                    break;
                case "terms":
                    output.AddRange(collection: formula.Terms.Select(selector: term => TermLine(live: live, meanings: meanings, quiet: quiet, term: term, values: values)));
                    break;
                case "meaning":
                    if (formula.Terms.FirstOrDefault(predicate: term => (term.Name == selected)) is { } chosen) {
                        output.Add(item: TermLine(live: live, meanings: meanings, quiet: quiet, term: chosen, values: values));
                    }
                    break;
                default:
                    output.AddRange(collection: formula.Equation);
                    break;
            }
        }

        return new WorldMathTextExpansion(
            Formulas: formulas,
            Highlight: selected,
            HighlightTint: HexColor.ParseRgba(fallback: default, value: highlight).Rgba,
            Lines: output,
            Values: values
        );
    }

    // A term's line: its symbol, its name, what it means and its live value.
    private static string TermLine(SdfTypesetTerm term, IReadOnlyDictionary<string, string> meanings, IReadOnlyDictionary<string, string> values, string live, string quiet) {
        var builder = new StringBuilder();

        builder.Append(value: $"\\term{{{term.Name}}}{{{term.Symbol}}}\\quad \\color{{{quiet}}}{{{MathMarkup.Escape(text: term.Name)}");
        if (meanings.TryGetValue(key: term.Name, value: out var meaning) && (meaning.Length > 0)) {
            builder.Append(value: $": {MathMarkup.Escape(text: meaning)}");
        }
        builder.Append(value: '}');
        if (values.TryGetValue(key: term.Name, value: out var value)) {
            builder.Append(value: $" = \\color{{{live}}}{{{MathMarkup.Escape(text: value)}}}");
        }

        return builder.ToString();
    }
    // An equation with each term a value names drawn as that value, in the live colour.
    private static string WithValues(string markup, IReadOnlyDictionary<string, string> values, string live) {
        foreach (var (name, value) in values) {
            markup = ReplaceTerm(body: $"\\color{{{live}}}{{{MathMarkup.Escape(text: value)}}}", markup: markup, name: name);
        }

        return markup;
    }
    // Replaces the body of every \term{name}{…} in markup, matching the body's braces.
    private static string ReplaceTerm(string markup, string name, string body) {
        var opening = $"\\term{{{name}}}{{";
        var builder = new StringBuilder(capacity: markup.Length);
        var at = 0;

        while (true) {
            var found = markup.IndexOf(value: opening, startIndex: at, comparisonType: StringComparison.Ordinal);

            if (found < 0) {
                builder.Append(value: markup, startIndex: at, count: (markup.Length - at));

                return builder.ToString();
            }

            var close = (found + opening.Length);

            for (var depth = 1; ((close < markup.Length) && (depth > 0)); close++) {
                if (markup[close] == '\\') { close++; continue; }
                if (markup[close] == '{') { depth++; } else if (markup[close] == '}') { depth--; }
            }

            builder.Append(value: markup, startIndex: at, count: (found - at)).Append(value: opening).Append(value: body).Append(value: '}');
            at = close;
        }
    }
    private static string? Selected(SdfTypesetFormula? first, string? select) {
        if (string.IsNullOrWhiteSpace(value: select)) { return null; }
        if (double.TryParse(s: select, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var index)) {
            var place = (int)Math.Round(a: index);

            return (((first is not null) && (place >= 0) && (place < first.Terms.Count)) ? first.Terms[place].Name : null);
        }

        return select;
    }
    // A number written with at most three decimals and its unit, if the text reads as one; any other text as it is.
    private static string FormatValue(string text) {
        var (number, unit) = Split(text: text);

        if (!double.TryParse(s: number, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var value)) {
            return text.Trim();
        }

        var written = value.ToString(format: "0.###", provider: CultureInfo.InvariantCulture).Replace(oldChar: '-', newChar: '−');

        return ((unit.Length > 0) ? $"{written} {unit}" : written);
    }
    private static (string Head, string Tail) Split(string text) {
        var trimmed = text.TrimStart();
        var space = trimmed.IndexOf(value: ' ');

        return ((space < 0) ? (trimmed.Trim(), string.Empty) : (trimmed[..space], trimmed[(space + 1)..].Trim()));
    }
}

/// <summary>A math text read into its lines (<see cref="WorldMathText.Expand"/>).</summary>
/// <param name="Lines">The lines of math markup.</param>
/// <param name="Highlight">The highlighted term's name, or <see langword="null"/>.</param>
/// <param name="HighlightTint">The highlighted term's display colour.</param>
/// <param name="Formulas">The references of the formulas the text brings in, in order.</param>
/// <param name="Values">The live values the text gives its terms, written as they draw.</param>
public sealed record WorldMathTextExpansion(
    IReadOnlyList<string> Lines,
    string? Highlight,
    Vector4 HighlightTint,
    IReadOnlyList<string> Formulas,
    IReadOnlyDictionary<string, string> Values
);
