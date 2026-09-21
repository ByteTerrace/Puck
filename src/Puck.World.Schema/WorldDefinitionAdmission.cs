using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>A document-local validation result for one admission operation and its selected machine catalog.</summary>
/// <remarks>The operation owns the definition and its collection contents until installation completes. Neither
/// they nor the catalog may change while this result is carried between preparation steps. When host composition
/// changes the ambient vocabulary, refresh its checks with <see cref="WorldDefinitionValidator.TryCompleteAdmission"/>
/// before admitting the boot. This is not a persistent cache or enduring proof of cross-document adjacency claims.</remarks>
public sealed class WorldDefinitionAdmission {
    private readonly IMachineValidationCatalog? m_machines;

    internal WorldDefinitionAdmission(WorldRuleCompilation compilation, IMachineValidationCatalog? machines) {
        Compilation = compilation;
        m_machines = machines;
    }

    /// <summary>Gets the exact definition admitted.</summary>
    public WorldDefinition Definition => Compilation.Definition;
    /// <summary>Gets the programs and analysis produced by validation.</summary>
    public WorldRuleCompilation Compilation { get; }

    /// <summary>Checks the document and catalog identities before handing this operation's result to another
    /// preparation step. The caller still owns the unchanged-content and unchanged-vocabulary requirements.</summary>
    /// <param name="definition">The definition being installed.</param>
    /// <param name="machines">The catalog the receiving host uses.</param>
    /// <returns>Whether both are the exact objects validated by this admission.</returns>
    public bool AppliesTo(WorldDefinition definition, IMachineValidationCatalog? machines) =>
        (ReferenceEquals(objA: Definition, objB: definition) && ReferenceEquals(objA: m_machines, objB: machines));
}
