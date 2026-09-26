using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// Specifies a presentation quality tier: the authored quality vocabulary a <c>views.graphs</c> row names its tier
/// from, the tiers a frame graph declares, and the variant of a shader package a row loads. A tier selects how a pass
/// computes, never what it reads: every variant of a pass is compiled from one source against one interface, with
/// <see cref="QualityTiers.Define"/> set to the tier's <see cref="QualityTiers.DefineValue"/>. Every document spells a
/// tier by its <see cref="QualityTiers.Name"/>. Presentation only; no tier reaches simulation state.
/// </summary>
[JsonConverter(typeof(QualityTierJsonConverter))]
public enum QualityTier : byte {
    /// <summary>The cheapest tier.</summary>
    Low,
    /// <summary>The middle tier.</summary>
    Medium,
    /// <summary>The fullest tier.</summary>
    High,
}
/// <summary>The one statement of how a <see cref="QualityTier"/> is spelled: its name, which a document, a
/// <c>.puck</c> source and a shader package's variant all write, and the preprocessor definition a variant is compiled
/// with.</summary>
public static class QualityTiers {
    /// <summary>The preprocessor symbol a tier's variant is compiled with. A source that reads it tests whether it is
    /// defined: the variant built for no tier leaves it undefined.</summary>
    public const string Define = "PUCK_QUALITY_TIER";

    /// <summary>Gets every tier, cheapest first.</summary>
    public static IReadOnlyList<QualityTier> All { get; } = [QualityTier.Low, QualityTier.Medium, QualityTier.High];
    /// <summary>Gets every tier's name, in the order of <see cref="All"/>: <c>low</c>, <c>medium</c>, <c>high</c>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(selector: Name)];

    /// <summary>Returns the value <see cref="Define"/> takes in a tier's variant: 0 for <see cref="QualityTier.Low"/>,
    /// 1 for <see cref="QualityTier.Medium"/>, 2 for <see cref="QualityTier.High"/>.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The value.</returns>
    public static int DefineValue(QualityTier tier) => ((int)tier);
    /// <summary>Returns a tier's name: <c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a declared tier.</exception>
    public static string Name(QualityTier tier) => tier switch {
        QualityTier.Low => "low",
        QualityTier.Medium => "medium",
        QualityTier.High => "high",
        _ => throw new ArgumentOutOfRangeException(
            actualValue: tier,
            message: "The tier is not a declared quality tier.",
            paramName: nameof(tier)
        ),
    };
    /// <summary>Returns the tier a name spells, exactly as <see cref="Name"/> writes it.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tier, or <see langword="null"/> when <paramref name="name"/> spells none.</returns>
    public static QualityTier? Parse(string? name) => name switch {
        "low" => QualityTier.Low,
        "medium" => QualityTier.Medium,
        "high" => QualityTier.High,
        _ => null,
    };
}
/// <summary>Reads and writes a <see cref="QualityTier"/> as its name (<see cref="QualityTiers.Name"/>), refusing any
/// other word, or the right word in another case, by name.</summary>
public sealed class QualityTierJsonConverter : JsonConverter<QualityTier>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => QualityTiers.Names;

    /// <inheritdoc/>
    public override QualityTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null);

        return (QualityTiers.Parse(name: token) ?? throw new JsonException(message: $"tier '{token}' must be 'low', 'medium', or 'high'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, QualityTier value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteStringValue(value: QualityTiers.Name(tier: value));
    }
}
