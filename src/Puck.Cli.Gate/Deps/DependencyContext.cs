using System.Text;
using System.Text.RegularExpressions;
using Puck.Confinement;

namespace Puck.Cli.Deps;

/// <summary>The ecosystems <c>puck deps update</c> covers, by the names <c>--only</c> takes.</summary>
public static class DependencyEcosystems {
    /// <summary>GitHub Actions pinned by commit in workflows and composite actions.</summary>
    public const string Actions = "actions";
    /// <summary>NuGet packages: every project's lock file, and the pins no lock file holds.</summary>
    public const string NuGet = "nuget";
    /// <summary>npm manifests and their lock files.</summary>
    public const string Npm = "npm";
    /// <summary>The .NET SDK <c>global.json</c> pins, and the MSBuild SDK packages beside it.</summary>
    public const string Sdk = "sdk";
    /// <summary>The .NET tools in <c>.config/dotnet-tools.json</c>.</summary>
    public const string Tools = "tools";
    /// <summary>Every ecosystem, in the order a run takes them.</summary>
    public static IReadOnlyList<string> All { get; } = [NuGet, Npm, Sdk, Tools, Actions];
}

/// <summary>Everything one ecosystem's update needs from the run that owns it.</summary>
/// <param name="RepositoryRoot">The checkout.</param>
/// <param name="Major">Whether a declared range's major bound moves too.</param>
/// <param name="Report">Where moves and holds go.</param>
/// <param name="Http">The client for the package feeds and release indexes.</param>
/// <param name="Feed">The NuGet feed.</param>
/// <param name="Files">The keeper of the files a run changes.</param>
/// <param name="CancellationToken">Cancels the run.</param>
public sealed record DependencyContext(string RepositoryRoot, bool Major, DependencyReport Report, HttpClient Http, NuGetFeed Feed, DependencyFiles Files, CancellationToken CancellationToken);

/// <summary>Remembers the first bytes of every file a run may change, so a <c>--check</c> run puts them all back and a
/// failed run leaves the tree as it found it. A file is tracked before anything writes it; <see cref="Write"/> tracks as
/// it writes.</summary>
public sealed class DependencyFiles {
    private readonly Dictionary<string, byte[]?> m_originals = new(comparer: StringComparer.Ordinal);

    /// <summary>Remembers a file's current bytes, or that it does not exist, unless it is already remembered.</summary>
    /// <param name="path">The file's full path.</param>
    public void Track(string path) {
        if (!m_originals.ContainsKey(key: path)) { m_originals[path] = (File.Exists(path: path) ? File.ReadAllBytes(path: path) : null); }
    }
    /// <summary>Replaces a file's text, keeping its original bytes for <see cref="Restore()"/>.</summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="text">The new text, written as UTF-8 without a byte-order mark.</param>
    public void Write(string path, string text) {
        Track(path: path);
        AtomicFile.WriteAllBytes(bytes: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(s: text), path: path);
    }
    /// <summary>Puts every tracked file back as it was first seen.</summary>
    public void Restore() {
        foreach (var path in m_originals.Keys) { Restore(path: path); }
    }
    /// <summary>Puts one tracked file back as it was first seen.</summary>
    /// <param name="path">The tracked file's full path.</param>
    public void Restore(string path) {
        if (m_originals[path] is { } bytes) { AtomicFile.WriteAllBytes(bytes: bytes, path: path); }
        else if (File.Exists(path: path)) { File.Delete(path: path); }
    }

    /// <summary>Replaces <c>"name": "oldSpec"</c> with <c>"name": "newSpec"</c> in a JSON file's text, wherever that
    /// pair occurs, leaving every other byte as written.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">The property name.</param>
    /// <param name="oldSpec">The value to replace.</param>
    /// <param name="newSpec">The replacement.</param>
    /// <returns>The text after the replacement.</returns>
    public static string ReplaceJsonPair(string text, string name, string oldSpec, string newSpec) => Regex.Replace(
        evaluator: match => $"{match.Groups["head"].Value}{newSpec}{match.Groups["tail"].Value}",
        input: text,
        options: RegexOptions.CultureInvariant,
        pattern: $"(?<head>\"{Regex.Escape(str: name)}\"\\s*:\\s*\")(?:{Regex.Escape(str: oldSpec)})(?<tail>\")"
    );
}
