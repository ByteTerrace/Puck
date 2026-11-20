using System.Reflection;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>One registered name-bearing member of the document model.</summary>
/// <param name="Owner">The type declaring the member; a derived record inheriting it matches too.</param>
/// <param name="Member">The member's C# name.</param>
/// <param name="Kind">The namespace the name resolves against.</param>
/// <param name="Role">How the member carries the name.</param>
/// <param name="Facet">The export list a host reference through this member is admitted by when the name belongs
/// to an imported module (<see cref="WorldModuleExports"/>); meaningless for a <see cref="WorldNameRole.Declares"/>
/// member.</param>
public sealed record WorldNameField(Type Owner, string Member, WorldNameKind Kind, WorldNameRole Role, WorldExportFacet Facet = WorldExportFacet.Read);
/// <summary>The name positions read by import qualification and the recipient write door.</summary>
public static partial class WorldNameRegistry {
    /// <summary>Gets the registration of every binding a creation document holds: the document rides its own
    /// serializer, so every value in it a state cell may stand in for (a shape's spatial value, a palette color, an
    /// identifier) binds a row by its <c>state.&lt;row&gt;</c> token, wherever it sits.</summary>
    public static WorldNameField CreationBinding { get; } = new(
        Facet: WorldExportFacet.Binding,
        Kind: WorldNameKind.State,
        Member: "state binding",
        Owner: typeof(Puck.World.Authoring.CreationDocument),
        Role: WorldNameRole.Binding
    );

    /// <summary>Reads the spelling declared on a model property when generating its runtime shape.</summary>
    /// <param name="model">The serializer member.</param>
    /// <returns>The member with its name field or text purpose.</returns>
    public static WorldModelMember Describe(WorldModelMember model) {
        ArgumentNullException.ThrowIfNull(argument: model);
        var property = model.DeclaringType.GetProperty(model.Member, BindingFlags.Instance | BindingFlags.Public);
        var name = property?.GetCustomAttribute<DocumentNameAttribute>();
        var text = property?.GetCustomAttribute<DocumentTextAttribute>();
        var field = name is null
            ? Implicit(model.DeclaringType, model.Member, model.Type)
            : new WorldNameField(model.DeclaringType, model.Member, name.Kind, name.Role, name.Facet);
        if (text is not null && (field is not null || string.IsNullOrWhiteSpace(text.Purpose))) {
            throw new InvalidOperationException($"{model.DeclaringType.Name}.{model.Member} must declare one spelling and give literal or local text a purpose.");
        }
        return model with { NameField = field, TextPurpose = text?.Purpose };
    }

    /// <summary>Whether a member capable of spelling a name still needs a classification.</summary>
    /// <param name="model">The described document member.</param>
    /// <returns>True when the member is unclassified.</returns>
    public static bool IsUncovered(WorldModelMember model) {
        ArgumentNullException.ThrowIfNull(argument: model);
        if (model.NameField is not null || model.TextPurpose is not null) { return false; }
        var type = ValueType(model.Type);
        return type == typeof(string) || type == typeof(CellName) || type == typeof(StateChannelRef) || type == typeof(ExpressionProgram);
    }

    private static Type ValueType(Type type) {
        while (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) {
            // A dictionary's values have the member's spelling. Its keys address entries of the map.
            var contracts = type.GetInterfaces().Prepend(type);
            var dictionary = contracts.FirstOrDefault(contract => contract.IsGenericType &&
                (contract.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || contract.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
            var element = dictionary?.GetGenericArguments()[1] ?? contracts.FirstOrDefault(contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            if (element is null || element == type) { break; }
            type = element;
        }
        return Nullable.GetUnderlyingType(type) ?? type;
    }
}
