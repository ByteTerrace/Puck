using Puck.Assets;
using Puck.Launcher.Release;

namespace Puck.Cli.Official;

// The one object-store write path every Official/ scanner shares: writes bytes create-only under
// <root>/objects/sha256/<hh>/<hex64> (Puck.Assets.ContentAddressedStore's own layout, read back by
// Puck.Launcher.Release.DirectoryReleaseSource/ContentAddressedLayout — the same tree puck publish's dry-run
// writes), and refuses when an object already on disk at that path does not itself hash to the path it sits
// under (a corrupted or hand-edited tree) rather than silently trusting it. Returns the manifest's own
// path/hash/size triple for the write.
internal sealed class OfficialObjectWriter(string root) {
    private readonly ContentAddressedStore m_store = new(root: root);
    private readonly string m_root = Path.GetFullPath(path: root);

    public (string Path, string Hash, long Size) Put(byte[] bytes) {
        var hash = ContentAddressedStore.ComputeHash(content: bytes);
        var objectPath = ContentAddressedLayout.ObjectPath(root: m_root, hash: hash);

        if (File.Exists(path: objectPath)) {
            var existingHash = ContentAddressedStore.ComputeHash(content: File.ReadAllBytes(path: objectPath));

            if (!string.Equals(a: existingHash, b: hash, comparisonType: StringComparison.Ordinal)) {
                throw new InvalidDataException(message: $"object at '{CliPaths.ToDisplay(relativeTo: m_root, fullPath: objectPath)}' does not hash to its own path — the tree is corrupted or tampered; refusing to overwrite it.");
            }
        } else {
            _ = m_store.Put(content: bytes);
        }

        var relativePath = Path.GetRelativePath(path: objectPath, relativeTo: m_root).Replace(oldChar: '\\', newChar: '/');

        return (relativePath, $"sha256/{hash}", bytes.LongLength);
    }
}
