using System.Text.Json;

namespace Puck.Confinement;

/// <summary>The stub's own configuration, written beside the stub executable (<see cref="InstallLayout.StubConfigFileName"/>)
/// by whoever installs the stub — never from argv, so a user cannot redirect the stub. The install root is always the
/// stub's own directory (<c>AppContext.BaseDirectory</c>): a separate configurable root would be one more thing this file
/// could lie about. <see cref="Serialize"/> and <see cref="Load"/> are its one format.</summary>
/// <param name="AppExecutableFileName">The staged app's executable file name, resolved under
/// <c>versions/&lt;version&gt;/</c> (e.g. <c>Puck.World.exe</c>).</param>
/// <param name="MaxAttempts">Consecutive unhealthy attempts a version may accumulate before the health gate trips.</param>
public sealed record StubConfiguration(string AppExecutableFileName, int MaxAttempts = 3) {
    /// <summary>Returns the file's bytes for this configuration.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(jsonTypeInfo: InstallJsonContext.Default.StubConfiguration, value: this);
    /// <summary>Reads and parses an install root's stub configuration file.</summary>
    /// <param name="installRoot">The install root (the stub's own directory).</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="JsonException">The file did not parse as a <see cref="StubConfiguration"/>.</exception>
    public static StubConfiguration Load(string installRoot) {
        var path = Path.Combine(path1: installRoot, path2: InstallLayout.StubConfigFileName);

        return (JsonSerializer.Deserialize(jsonTypeInfo: InstallJsonContext.Default.StubConfiguration, utf8Json: File.ReadAllBytes(path: path))
            ?? throw new JsonException(message: $"'{path}' deserialized to null"));
    }
}
