using System.Security.Cryptography;

namespace Puck.Cli.Channel;

/// <summary>Where a release channel is hosted and whose key signs it: a deployment's provider contributes one
/// (<see cref="Select"/>), and <see cref="DirectoryReleaseChannelHost"/> mirrors a channel into a local directory with a key
/// file. <c>puck channel push</c> signs a release-source tree with the host's root key and writes it through the host, so
/// no provider type or setting reaches the verb.</summary>
public abstract class ReleaseChannelHost {
    /// <summary>Gets a short description of where the host writes, for the verb's report.</summary>
    public abstract string Describe { get; }
    /// <summary>Loads the channel's root signing key from the host's key custody, creating it there on first use, so every
    /// release the host publishes verifies against the anchor its consumers pinned.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The key, owned by the caller.</returns>
    public abstract Task<ECDsa> LoadSigningKeyAsync(CancellationToken cancellationToken);
    /// <summary>Writes a signed release-source tree: first every content-addressed object the host does not already hold,
    /// as immutable content, then each mutable document, in the order given, so a reader that sees a new manifest finds
    /// every object it names.</summary>
    /// <param name="tree">The tree's root.</param>
    /// <param name="objects">The objects' slashed paths under <paramref name="tree"/> (<c>objects/sha256/…</c>).</param>
    /// <param name="documents">The mutable documents' slashed paths under <paramref name="tree"/>, manifests last.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>A task that completes when every file is written.</returns>
    public abstract Task UploadAsync(string tree, IReadOnlyList<string> objects, IReadOnlyList<string> documents, CancellationToken cancellationToken);

    /// <summary>Selects an installed host by name, or the single installed host when no name is supplied.</summary>
    /// <param name="name">The host's name, or <see langword="null"/>.</param>
    /// <returns>The host.</returns>
    public static ReleaseChannelHost Select(string? name = null) => CliExtensions.Installed.Select<ReleaseChannelHost>(key: name, purpose: "release channel host");
    /// <summary>Opens the host a verb's options name: a local directory's channel tree when one is given, otherwise an
    /// installed host by name, or the single installed host.</summary>
    /// <param name="name">The installed host's name, or <see langword="null"/>.</param>
    /// <param name="directory">The local tree's root, or <see langword="null"/> for an installed host.</param>
    /// <param name="keyFile">With <paramref name="directory"/>: the root key file that signs it, or <see langword="null"/>
    /// when nothing written is signed.</param>
    /// <returns>The host.</returns>
    /// <exception cref="InvalidOperationException">No directory is given and no installed host answers to
    /// <paramref name="name"/>.</exception>
    public static ReleaseChannelHost Open(string? name, string? directory, string? keyFile = null) =>
        ((directory is not null) ? new DirectoryReleaseChannelHost(directory: directory, keyFile: keyFile) : Select(name: name));
}
/// <summary>A channel tree in a local directory, signed by a key file when it carries a release: a developer's own channel,
/// served by <c>puck official serve</c> or read by a <see cref="Puck.Launcher.Release.DirectoryReleaseSource"/>.</summary>
/// <param name="directory">The tree's root.</param>
/// <param name="keyFile">The PKCS#8 root key file, created on first use, or <see langword="null"/> for a tree that holds
/// nothing signed (a store of cache sets).</param>
public sealed class DirectoryReleaseChannelHost(string directory, string? keyFile) : ReleaseChannelHost {
    private readonly string m_directory = Path.GetFullPath(path: directory);

    /// <inheritdoc/>
    public override string Describe => m_directory;
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The host was opened without a key file.</exception>
    public override Task<ECDsa> LoadSigningKeyAsync(CancellationToken cancellationToken) => Task.FromResult(result: Puck.Launcher.Release.ReleaseSigner.LoadOrCreate(
        path: (keyFile ?? throw new InvalidOperationException(message: $"{m_directory} was opened without a root key file, so it signs nothing."))));
    /// <inheritdoc/>
    public override Task UploadAsync(string tree, IReadOnlyList<string> objects, IReadOnlyList<string> documents, CancellationToken cancellationToken) {
        var source = Path.GetFullPath(path: tree);

        if (string.Equals(a: source, b: m_directory, comparisonType: Puck.Abstractions.PuckPaths.Comparison)) { return Task.CompletedTask; }
        foreach (var path in objects) {
            var target = Path.Combine(path1: m_directory, path2: path);

            if (File.Exists(path: target)) { continue; }
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(destFileName: target, overwrite: false, sourceFileName: Path.Combine(path1: source, path2: path));
        }
        foreach (var path in documents) {
            cancellationToken.ThrowIfCancellationRequested();
            Puck.Confinement.AtomicFile.WriteAllBytes(bytes: File.ReadAllBytes(path: Path.Combine(path1: source, path2: path)), path: Path.Combine(path1: m_directory, path2: path));
        }
        return Task.CompletedTask;
    }
}
