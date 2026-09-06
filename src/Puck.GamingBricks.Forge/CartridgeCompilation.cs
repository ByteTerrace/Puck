namespace Puck.GamingBricks.Forge;

/// <summary>The deterministic output of a target compiler, with source identity and a state-symbol map for inspection.</summary>
/// <param name="Rom">The standalone ROM image. No host interpreter is required to execute it.</param>
/// <param name="SourceHash">The canonical source document's SHA-256.</param>
/// <param name="Target">The target platform.</param>
/// <param name="Variables">Bus addresses of the named one-byte state slots.</param>
public sealed record CartridgeCompilation(byte[] Rom, string SourceHash, string Target, IReadOnlyDictionary<string, uint> Variables);

/// <summary>Compiles validated authored data to standalone native cartridge instructions and assets.</summary>
public interface ICartridgeCompiler {
    /// <summary>Gets the supported target token.</summary>
    string Target { get; }
    /// <summary>Validates and compiles a cartridge without reading files, environment variables or host time.</summary>
    /// <param name="document">The complete cartridge source.</param>
    /// <returns>The ROM and its source/state metadata.</returns>
    CartridgeCompilation Compile(CartridgeDocument document);
}
