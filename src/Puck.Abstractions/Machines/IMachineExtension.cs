namespace Puck.Abstractions.Machines;

/// <summary>Registers a machine provider through either static composition or dynamic extension discovery.</summary>
public interface IMachineExtension {
    /// <summary>Gets the extension's display name.</summary>
    string Name { get; }

    /// <summary>Registers the extension's engines and content preparation capabilities.</summary>
    /// <param name="registry">The host's registration target.</param>
    void Initialize(IMachineExtensionRegistry registry);
}

/// <summary>The neutral registration surface supplied to a machine extension.</summary>
public interface IMachineExtensionRegistry {
    /// <summary>Registers an engine with an optional provider that prepares its authored content.</summary>
    /// <param name="engine">The machine factory.</param>
    /// <param name="contentProvider">The engine's content provider, or null for native images only.</param>
    void RegisterEngine(IMachineEngine engine, IMachineContentProvider? contentProvider = null);

    /// <summary>Registers a content provider under its engine identifier.</summary>
    /// <param name="contentProvider">The provider to register.</param>
    void RegisterContentProvider(IMachineContentProvider contentProvider);
}
