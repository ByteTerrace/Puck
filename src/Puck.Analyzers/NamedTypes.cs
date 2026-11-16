using Microsoft.CodeAnalysis;

namespace Puck.Analyzers;

/// <summary>Names a type by its namespace and its name, as the compiler's symbols spell them: the one test the rules
/// use to recognise an engine type without referencing the assembly that declares it.</summary>
internal static class NamedTypes {
    /// <summary>Returns whether a type's definition is the top-level type of that name in that namespace.</summary>
    /// <param name="type">The type, or <see langword="null"/>.</param>
    /// <param name="namespace">The namespace's display name.</param>
    /// <param name="name">The type's name, without arity.</param>
    /// <returns><see langword="true"/> when the type is that one, whatever its type arguments.</returns>
    public static bool Is(ITypeSymbol? type, string @namespace, string name) {
        if (type is null) {
            return false;
        }

        var definition = type.OriginalDefinition;

        return (
            (definition.Name == name) &&
            (definition.ContainingType is null) &&
            (definition.ContainingNamespace?.ToDisplayString() == @namespace)
        );
    }
    /// <summary>Returns whether a type is, or implements, the named type.</summary>
    /// <param name="type">The type, or <see langword="null"/>.</param>
    /// <param name="namespace">The namespace's display name.</param>
    /// <param name="name">The type's name, without arity.</param>
    /// <returns><see langword="true"/> when the type is the named one or lists it among its interfaces.</returns>
    public static bool Implements(ITypeSymbol? type, string @namespace, string name) {
        if (type is null) {
            return false;
        }
        if (Is(type: type, @namespace: @namespace, name: name)) {
            return true;
        }

        foreach (var contract in type.AllInterfaces) {
            if (Is(type: contract, @namespace: @namespace, name: name)) {
                return true;
            }
        }

        return false;
    }
}
