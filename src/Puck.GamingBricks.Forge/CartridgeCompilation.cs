namespace Puck.GamingBricks.Forge;

/// <summary>The deterministic output of a target compiler, with source identity and a state-symbol map for inspection.</summary>
/// <param name="Rom">The standalone ROM image. No host interpreter is required to execute it.</param>
/// <param name="SourceHash">The canonical source document's SHA-256.</param>
/// <param name="Target">The target platform.</param>
/// <param name="Variables">Bus addresses of the named one-byte state slots.</param>
/// <param name="Arrays">Bus addresses of the first element of each named array.</param>
public sealed record CartridgeCompilation(byte[] Rom, string SourceHash, string Target, IReadOnlyDictionary<string, uint> Variables, IReadOnlyDictionary<string, uint> Arrays);

/// <summary>
/// A cartridge whose compiled image does not fit the machine it targets: the routine, the data, or the whole image is
/// larger than the hardware addresses.
/// </summary>
public sealed class CartridgeCapacityException : Exception {
    /// <summary>Creates the failure.</summary>
    /// <param name="message">What overran, by how much, and against which window.</param>
    public CartridgeCapacityException(string message) : base(message: message) { }

    /// <summary>Creates the failure over the builder's own report.</summary>
    /// <param name="message">What overran, by how much, and against which window.</param>
    /// <param name="innerException">The builder's report.</param>
    public CartridgeCapacityException(string message, Exception innerException) : base(message: message, innerException: innerException) { }
}

/// <summary>Compiles validated authored data to standalone native cartridge instructions and assets.</summary>
public interface ICartridgeCompiler {
    /// <summary>Gets the machine engine identifier this compiler targets.</summary>
    string EngineId { get; }
    /// <summary>Gets the supported target token.</summary>
    string Target { get; }
    /// <summary>Validates and compiles a cartridge without reading files, environment variables or host time.</summary>
    /// <param name="document">The complete cartridge source.</param>
    /// <returns>The ROM and its source/state metadata.</returns>
    CartridgeCompilation Compile(CartridgeDocument document);
}

/// <summary>
/// Entry point for a gaming brick extension package, supporting both static DI composition
/// and dynamic runtime loading without compile-time host coupling.
/// </summary>
public interface IGamingBrickExtension {
    /// <summary>Gets the user-friendly name of the extension.</summary>
    string Name { get; }
    /// <summary>Initializes and registers the extension's engines and compilers.</summary>
    /// <param name="registry">The registry to register components into.</param>
    void Initialize(IGamingBrickExtensionRegistry registry);
}

/// <summary>
/// Registry provided to an extension during initialization to register engines and compilers.
/// </summary>
public interface IGamingBrickExtensionRegistry {
    /// <summary>Registers a screen-machine engine and an optional companion cartridge forge compiler.</summary>
    /// <param name="engine">The screen-machine engine.</param>
    /// <param name="compiler">The optional cartridge compiler.</param>
    void RegisterEngine(Puck.Abstractions.Machines.IScreenMachineEngine engine, ICartridgeCompiler? compiler = null);
    /// <summary>Registers a cartridge compiler.</summary>
    /// <param name="compiler">The cartridge compiler.</param>
    void RegisterCompiler(ICartridgeCompiler compiler);
}
