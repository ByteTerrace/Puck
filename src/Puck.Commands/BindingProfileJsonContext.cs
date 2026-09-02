using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// The System.Text.Json source-generation context for <c>puck.bindings.v1</c> — the sanctioned entry point for
/// (de)serializing a <see cref="BindingProfileDocument"/> from this package, with no runtime reflection anywhere on
/// the path, so a consumer publishing Native AOT or trimmed round-trips a player's controller mapping without a
/// single trim warning of this project's making.
/// </summary>
/// <remarks>
/// <para>
/// <b>One wire shape, not two.</b> Every type in this graph that needs a bespoke spelling declares it at its OWN
/// declaration rather than in this context's converter list: <see cref="CommandValue"/> carries
/// <see cref="CommandValueJsonConverter"/>, <see cref="ChannelRef"/> carries <see cref="ChannelRefJsonConverter"/>,
/// <c>DocumentIdentifier</c> carries its own, and every enum carries
/// <see cref="Puck.Abstractions.Documents.StrictEnumConverter{TEnum}"/> (written by exact declared member name,
/// numeric token refused on read). That is what makes this context and <c>Puck.World.WorldJsonContext</c> —
/// which reaches the same <see cref="BindingProfileDocument"/> as a section of the world document — produce
/// byte-identical text for the same document rather than two spellings a reader must guess between. Neither
/// context defines the shape; the types do.
/// </para>
/// <para>
/// <b>Strictness matches the world document's.</b> <c>UnmappedMemberHandling.Disallow</c> refuses a member the
/// model does not have, and <c>RespectRequiredConstructorParameters</c> refuses a member the model requires and the
/// document does not carry — so "no C# default" means required here exactly as it does there, and a retired
/// authoring key fails by name instead of vanishing. <c>WriteIndented</c> matches too, deliberately: a binding
/// document written from this package and one written as a section of a world document are then the SAME BYTES,
/// which is a claim a test can make (and does) rather than a resemblance a reader has to eyeball.
/// </para>
/// <para>
/// <b>What this does not cover.</b> The AOT/trim analyzers pass over this file and every type it names, but a
/// consumer publishing Native AOT still sees warnings out of <c>ByteTerrace.Puck.Assets</c>, which sets
/// <c>IsAotCompatible=false</c> for its reflection-based <c>DocumentCanonicalizer</c>/<c>DocumentJsonOptions</c>
/// path. Nothing on THIS graph reaches that path — <c>DocumentIdentifier</c>'s converter, the only Puck.Assets type
/// here, is reflection-free — but the warnings are assembly-scoped, so they are a fact about the dependency rather
/// than about this serializer.
/// </para>
/// </remarks>
[JsonSerializable(typeof(BindingProfileDocument))]
// The nested rows, named so a caller can (de)serialize one row through a typed accessor — an editor saving a single
// chord, a wheel, or the bar preferences — rather than reaching for a reflection-based overload and losing the
// property.
[JsonSerializable(typeof(BindingActivatorDefinition))]
[JsonSerializable(typeof(BindingBarPreferences))]
[JsonSerializable(typeof(BindingChordDefinition))]
[JsonSerializable(typeof(BindingCommandDefinition))]
[JsonSerializable(typeof(BindingContextDefinition))]
[JsonSerializable(typeof(BindingModifierDefinition))]
[JsonSerializable(typeof(BindingPageDefinition))]
[JsonSerializable(typeof(BindingPageEntryDefinition))]
[JsonSerializable(typeof(BindingWheelDefinition))]
[JsonSerializable(typeof(BindingWheelExcursionDefinition))]
[JsonSerializable(typeof(BindingWheelStyleDefinition))]
// The two converted leaves, named so a caller holding one alone reaches the same canonical shape the document
// embeds. Every enum below is likewise reachable on its own; the graph would generate them regardless, and naming
// them makes the strict by-name spelling addressable rather than only implied.
[JsonSerializable(typeof(ChannelRef))]
[JsonSerializable(typeof(CommandValue))]
[JsonSerializable(typeof(BindingActivatorMode))]
[JsonSerializable(typeof(BindingEntryMode))]
[JsonSerializable(typeof(BindingWheelPlacement))]
[JsonSerializable(typeof(BindingWheelRingSelectionMode))]
[JsonSerializable(typeof(BindingWheelSpatialSelectionMode))]
[JsonSerializable(typeof(CommandPhase))]
[JsonSerializable(typeof(CommandValueKind))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
)]
public sealed partial class BindingProfileJsonContext : JsonSerializerContext {
}
