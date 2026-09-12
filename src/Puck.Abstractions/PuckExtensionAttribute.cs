namespace Puck.Abstractions;

/// <summary>
/// Marks an assembly as providing one or more dynamically discoverable Puck extensions.
/// The host loader queries this metadata attribute for fast activation without expensive assembly-wide type reflection.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PuckExtensionAttribute(Type extensionType) : Attribute {
    /// <summary>
    /// Gets the concrete type of the extension that implements an extension interface (such as
    /// <c>IMachineExtension</c> or <c>IWorldExtension</c>).
    /// </summary>
    public Type ExtensionType { get; } = extensionType;
}
