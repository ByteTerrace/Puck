using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Puck.State;

/// <summary>Declares one vector embedding space with its model, revision, and component dimensions.</summary>
/// <remarks>The model, revision, and dimensions are the space's <see cref="EmbeddingIdentity"/>, which
/// <see cref="Identity"/> carries; a document spells them flat beside the name, and <see cref="ExtendJson"/> is the
/// serializer modifier that spells them.</remarks>
/// <param name="Name">The stable space name, unique within the section.</param>
/// <param name="Identity">The model, revision, and component dimensions of the space's vectors.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSpace(
    CellName Name,
    [property: JsonIgnore] EmbeddingIdentity Identity
) {
    /// <summary>The maximum character length of a space name.</summary>
    public const int MaxNameLength = 128;

    /// <summary>Initializes a space from its document spelling.</summary>
    /// <param name="name">The stable space name, unique within the section.</param>
    /// <param name="model">The model name, non-empty and at most <see cref="EmbeddingIdentity.MaxModelLength"/> characters.</param>
    /// <param name="revision">The model revision, non-empty and at most <see cref="EmbeddingIdentity.MaxRevisionLength"/> characters.</param>
    /// <param name="dimensions">The component dimension count, in
    /// [<see cref="StateCapacity.MinVectorDimensions"/>, <see cref="StateCapacity.MaxVectorDimensions"/>].</param>
    [JsonConstructor]
    public StateSpace(CellName name, string model, string revision, int dimensions) : this(
        Identity: new EmbeddingIdentity(
            Dimensions: dimensions,
            Model: model,
            Revision: revision
        ),
        Name: name
    ) { }

    /// <summary>Spells a space's <see cref="Identity"/> flat beside its name: the modifier replaces the serializer's
    /// ignored <c>identity</c> slot with one member per identity field, each read from <see cref="Identity"/> and bound
    /// by name to the document constructor's parameter. A resolver that serializes a space installs it; one that does
    /// not has no member to bind <c>model</c>, <c>revision</c> or <c>dimensions</c> to and refuses the type.</summary>
    /// <param name="typeInfo">The type info being resolved; any type other than <see cref="StateSpace"/> is left as
    /// resolved.</param>
    public static void ExtendJson(JsonTypeInfo typeInfo) {
        ArgumentNullException.ThrowIfNull(argument: typeInfo);

        if (typeInfo.Type != typeof(StateSpace)) {
            return;
        }

        var identityName = JsonName(
            member: nameof(Identity),
            typeInfo: typeInfo
        );

        for (var index = (typeInfo.Properties.Count - 1); (index >= 0); index--) {
            if (string.Equals(
                a: typeInfo.Properties[index].Name,
                b: identityName,
                comparisonType: StringComparison.Ordinal
            )) {
                typeInfo.Properties.RemoveAt(index: index);
            }
        }

        AddIdentityMember<string>(
            member: nameof(EmbeddingIdentity.Model),
            read: static space => ((StateSpace)space).Identity.Model,
            typeInfo: typeInfo
        );
        AddIdentityMember<string>(
            member: nameof(EmbeddingIdentity.Revision),
            read: static space => ((StateSpace)space).Identity.Revision,
            typeInfo: typeInfo
        );
        AddIdentityMember<int>(
            member: nameof(EmbeddingIdentity.Dimensions),
            read: static space => ((StateSpace)space).Identity.Dimensions,
            typeInfo: typeInfo
        );
    }

    // Built through the source generator's own metadata path, which needs no runtime code generation. The member's
    // attribute provider is the identity field it reads, so a walk over the resolved metadata (the world model's
    // shape, the schema's descriptions) names that field rather than a member StateSpace lacks.
    private static void AddIdentityMember<T>(JsonTypeInfo typeInfo, string member, Func<object, T?> read) {
        var provider = typeof(EmbeddingIdentity).GetProperty(name: member)!;
        var property = JsonMetadataServices.CreatePropertyInfo(
            options: typeInfo.Options,
            propertyInfo: new JsonPropertyInfoValues<T> {
                AttributeProviderFactory = () => provider,
                DeclaringType = typeof(StateSpace),
                Getter = read,
                IsProperty = true,
                IsPublic = true,
                IsVirtual = false,
                PropertyName = member,
            }
        );

        // Every identity field is non-null, as the constructor parameter it binds to declares.
        property.IsGetNullable = false;
        property.IsSetNullable = false;
        typeInfo.Properties.Add(item: property);
    }
    private static string JsonName(JsonTypeInfo typeInfo, string member) =>
        (typeInfo.Options.PropertyNamingPolicy?.ConvertName(name: member) ?? member);
}
