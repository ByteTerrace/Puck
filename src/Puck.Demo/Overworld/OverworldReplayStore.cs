using Puck.Commands;

namespace Puck.Demo.Overworld;

/// <summary>
/// Persists <see cref="OverworldRecording"/>s as demo-local data: a flat <c>Replays/</c> folder under the same
/// <c>%LOCALAPPDATA%\Puck\Demo</c> root <see cref="BindingProfileDocumentStore"/> (settings) and
/// <c>Forge.RomForge.PrepareDefaultSavePath</c> (cartridge saves) already use — filename is <c>&lt;name&gt;.puckreplay</c>,
/// no separate index file. <b>CAS was considered and rejected</b>: a replay is an ad hoc proof/demo artifact a script
/// names on the fly, not render-pipeline content addressed by hash — a plain named file is simpler and matches the
/// two existing local-data conventions this file sits beside, rather than forking a third (content-addressed) shape
/// for one small binary blob. The binary format itself (<see cref="OverworldRecording.Write"/>/<c>.Read</c>) is
/// unrelated to this storage choice — it would round-trip through a CAS blob identically if that judgment ever flips.
/// </summary>
internal static class OverworldReplayStore {
    private const string Extension = ".puckreplay";

    /// <summary>The <c>Replays/</c> directory, created on first use.</summary>
    public static string Directory() {
        var directory = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "Puck", path3: "Demo", path4: "Replays");

        _ = System.IO.Directory.CreateDirectory(path: directory);

        return directory;
    }

    /// <summary>Validates a replay name: non-empty, and free of path-navigation characters (a console verb argument
    /// is untrusted input — this keeps every path under <see cref="Directory"/>).</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is safe to use as a filename stem.</returns>
    public static bool IsValidName(string name) {
        return (!string.IsNullOrWhiteSpace(value: name) &&
            (name.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) < 0) &&
            !name.Contains(value: '.') &&
            !name.Contains(value: '/') &&
            !name.Contains(value: '\\'));
    }

    /// <summary>The on-disk path a valid <paramref name="name"/> resolves to.</summary>
    public static string PathFor(string name) {
        return Path.Combine(path1: Directory(), path2: (name + Extension));
    }

    /// <summary>Persists a recording as <c>&lt;name&gt;.puckreplay</c>, overwriting any existing file of the same name.</summary>
    /// <param name="name">The replay's name (validated by <see cref="IsValidName"/> — the caller checks first).</param>
    /// <param name="recording">The recording to write.</param>
    /// <param name="registry">The registry whose interned ids the recording's entries reference.</param>
    /// <returns>The path written to.</returns>
    public static string Save(string name, OverworldRecording recording, CommandRegistry registry) {
        var path = PathFor(name: name);

        using var stream = File.Create(path: path);

        recording.Write(stream: stream, registry: registry);

        return path;
    }

    /// <summary>Loads a previously-saved recording, or <see langword="null"/> when no file matches <paramref name="name"/>.</summary>
    /// <param name="name">The replay's name.</param>
    /// <param name="registry">The registry the recording's entries are remapped into.</param>
    public static OverworldRecording? Load(string name, CommandRegistry registry) {
        var path = PathFor(name: name);

        if (!File.Exists(path: path)) {
            return null;
        }

        using var stream = File.OpenRead(path: path);

        return OverworldRecording.Read(stream: stream, registry: registry);
    }

    /// <summary>Lists every persisted replay's name (extension stripped), sorted ordinally.</summary>
    public static IReadOnlyList<string> List() {
        var directory = Directory();
        var names = System.IO.Directory.GetFiles(path: directory, searchPattern: ("*" + Extension))
            .Select(selector: Path.GetFileNameWithoutExtension)
            .Where(predicate: static name => !string.IsNullOrEmpty(value: name))
            .Select(selector: static name => name!)
            .ToList();

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }
}
