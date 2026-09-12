using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions.Machines;

/// <summary>The machine vocabulary selected by a host for document admission. Offline tools may carry no catalog;
/// they must report the resulting deferred provider checks instead of claiming semantic validation.</summary>
public interface IMachineValidationCatalog {
    /// <summary>Resolves the same provider descriptor used by runtime construction and authoring tools.</summary>
    /// <param name="engineId">The registered engine identifier.</param>
    /// <param name="descriptor">The selected provider description, or null when unavailable.</param>
    /// <returns>Whether the descriptor is available in this host's catalog.</returns>
    bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? descriptor);
    /// <summary>Determines whether this host can construct the named engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    bool IsRegistered(string engineId);
    /// <summary>Determines whether an installed provider recognizes the path as authored source requiring preparation.</summary>
    /// <param name="contentPath">The logical content path.</param>
    bool RequiresPreparation(string contentPath);
    /// <summary>Determines whether the selected engine's provider can prepare the named source format.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    /// <param name="contentPath">The logical content path.</param>
    bool CanPrepare(string engineId, string contentPath);
}
