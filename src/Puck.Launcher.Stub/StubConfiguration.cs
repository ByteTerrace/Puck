using System.Text.Json;

namespace Puck.Launcher.Stub;

/// <summary>The stub's own configuration, written beside the stub executable at install/publish time — never from
/// argv, so a user cannot redirect the stub. The install root is always the stub's own directory
/// (<c>AppContext.BaseDirectory</c>): a separate configurable root would be one more thing this file could lie about.</summary>
/// <param name="AppExecutableFileName">The staged app's executable file name, resolved under
/// <c>versions/&lt;version&gt;/</c> (e.g. <c>Puck.World.exe</c>).</param>
/// <param name="MaxAttempts">Consecutive unhealthy attempts a version may accumulate before the health gate trips.</param>
public sealed record StubConfiguration(string AppExecutableFileName, int MaxAttempts = 3);
/// <summary>Loads <see cref="StubConfiguration"/> from <see cref="StubInstall.StubConfigFileName"/>.</summary>
public static class StubConfigurationFile {
    /// <summary>Reads and parses the stub configuration file.</summary>
    /// <param name="installRoot">The install root (the stub's own directory).</param>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="JsonException">The file did not parse as <see cref="StubConfiguration"/>.</exception>
    public static StubConfiguration Load(string installRoot) {
        var path = Path.Combine(path1: installRoot, path2: StubInstall.StubConfigFileName);
        var json = File.ReadAllText(path: path);

        return (JsonSerializer.Deserialize<StubConfiguration>(json: json)
            ?? throw new JsonException(message: $"'{path}' deserialized to null"));
    }
}
