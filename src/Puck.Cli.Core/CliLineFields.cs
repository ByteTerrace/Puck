namespace Puck.Cli;

/// <summary>Reads the <c>name=value</c> fields of a World's narration line (<c>[sdf.mesh-bake] start residency=… baking=3 …</c>),
/// each a whitespace-separated token.</summary>
public static class CliLineFields {
    /// <summary>Returns a field's value: the first token that starts with the name and <c>=</c>, without a closing
    /// bracket that ends the line.</summary>
    /// <param name="line">The narration line.</param>
    /// <param name="name">The field's name.</param>
    /// <returns>The value, or <see langword="null"/> when the line has no such field.</returns>
    public static string? Field(string line, string name) {
        var prefix = (name + "=");

        foreach (var token in line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)) {
            if (token.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) { return token[prefix.Length..].TrimEnd(trimChar: ']'); }
        }
        return null;
    }
    /// <summary>Returns a field's whole-number value.</summary>
    /// <param name="line">The narration line.</param>
    /// <param name="name">The field's name.</param>
    /// <returns>The value, or zero when the line has no such field or it is not a whole number.</returns>
    public static int Count(string line, string name) =>
        (int.TryParse(s: Field(line: line, name: name), provider: System.Globalization.CultureInfo.InvariantCulture, result: out var value) ? value : 0);
}
