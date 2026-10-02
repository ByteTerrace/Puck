namespace Puck;

/// <summary>Marks a producer whose transitive source dependencies determine a generated content key.</summary>
/// <param name="name">The derivation listed by <c>puck derivations</c>.</param>
[AttributeUsage(validOn: AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
internal sealed class DerivationAttribute(string name) : Attribute {
    /// <summary>Gets the derivation's name.</summary>
    public string Name { get; } = name;
}
