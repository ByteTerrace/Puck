namespace Puck.Abstractions.Documents;

/// <summary>Describes the JSON primitive types accepted by a custom System.Text.Json converter so a schema exporter
/// can restore the constraint that converter metadata otherwise hides. Use
/// <see cref="IJsonSchemaStringConverter"/> as well when the string arm has a closed token vocabulary.</summary>
public interface IJsonSchemaTypeConverter {
    /// <summary>Gets the accepted JSON Schema primitive type names, such as <c>number</c> and <c>string</c>.</summary>
    IReadOnlyList<string> SchemaTypes { get; }
}
