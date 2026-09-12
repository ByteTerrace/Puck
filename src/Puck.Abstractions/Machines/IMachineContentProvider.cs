namespace Puck.Abstractions.Machines;

/// <summary>A provider-owned transformation from authored content into an executable machine image.</summary>
/// <remarks>The caller owns reading and pinning the input bytes. Preparation does not read files, boot a machine,
/// or mutate an existing instance. The provider owns content recognition, validation, compilation, and symbols.</remarks>
public interface IMachineContentProvider {
    /// <summary>Gets the identifier of the engine that executes the prepared image.</summary>
    string EngineId { get; }

    /// <summary>Determines whether this provider recognizes the named authored content format.</summary>
    /// <param name="contentPath">The content's logical path, used for format selection only.</param>
    /// <returns>True when the provider prepares this format; false for an ordinary native image.</returns>
    bool Recognizes(string contentPath);

    /// <summary>Validates and prepares one recognized content image.</summary>
    /// <param name="content">The complete immutable input bytes.</param>
    /// <returns>The executable image and provider-owned source and symbol metadata.</returns>
    /// <exception cref="MachineContentException">The content cannot be prepared.</exception>
    PreparedMachineContent Prepare(ReadOnlyMemory<byte> content);
}

/// <summary>An addressable symbol exported by a content provider.</summary>
/// <param name="Space">The provider-defined address space.</param>
/// <param name="Address">The unsigned address in that space.</param>
public readonly record struct MachineContentSymbol(string Space, ulong Address);

/// <summary>An executable image prepared without knowledge of a particular machine's document format.</summary>
/// <param name="Image">The complete native executable image; callers must not modify it while mounted.</param>
/// <param name="SourceHash">The provider's canonical source identity.</param>
/// <param name="Symbols">Named addresses exported by the provider.</param>
public sealed record PreparedMachineContent(byte[] Image, string SourceHash, IReadOnlyDictionary<string, MachineContentSymbol> Symbols);

/// <summary>A provider's content validation or preparation refusal.</summary>
public sealed class MachineContentException : Exception {
    /// <summary>Creates a refusal with the provider's diagnostic.</summary>
    /// <param name="message">The actionable content diagnostic.</param>
    /// <param name="innerException">The underlying provider failure, when present.</param>
    public MachineContentException(string message, Exception? innerException = null) : base(message: message, innerException: innerException) { }
}
