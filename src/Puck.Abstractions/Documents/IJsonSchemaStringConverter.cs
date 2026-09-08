namespace Puck.Abstractions.Documents;

/// <summary>
/// Describes the string shape accepted by a custom System.Text.Json converter so a schema exporter can restore the
/// constraint that converter metadata otherwise hides. A converter accepting multiple JSON primitive kinds uses
/// <see cref="IJsonSchemaTypeConverter"/> instead, and may implement both when its string arm also has closed tokens.
/// </summary>
public interface IJsonSchemaStringConverter {
    /// <summary>
    /// Every accepted token, or <see langword="null"/> when the converter accepts free-form string content and the
    /// schema should apply only a string type constraint.
    /// </summary>
    IReadOnlyList<string>? SchemaTokens { get; }
}
