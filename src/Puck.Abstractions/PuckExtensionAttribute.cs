using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions;

/// <summary>
/// Declares the <see cref="IPuckExtension"/> entry types an installed extension assembly provides. Discovery reads only
/// this attribute: an assembly without it is refused rather than scanned for types.
/// </summary>
/// <param name="extensionType">The concrete type that implements <see cref="IPuckExtension"/> and has a public
/// parameterless constructor.</param>
[AttributeUsage(validOn: AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PuckExtensionAttribute([DynamicallyAccessedMembers(memberTypes: DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type extensionType) : Attribute {
    /// <summary>Gets the concrete type that implements <see cref="IPuckExtension"/> and has a public parameterless
    /// constructor.</summary>
    [DynamicallyAccessedMembers(memberTypes: DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ExtensionType { get; } = extensionType;
}
