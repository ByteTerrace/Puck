using Puck.Assets;

namespace Puck.Cli.Official;

// The one object-store write path every Official/ scanner shares: writes bytes create-only through
// Puck.Assets.ContentAddressedStore, whose ObjectPath/ObjectRelativePath are the layout Puck.Launcher's release
// sources read back (the same tree puck publish's dry-run writes), and refuses when an object already on disk at that
// path does not itself hash to the path it sits under (a corrupted or hand-edited tree) rather than silently trusting
// it. Returns the manifest's own path/hash/size triple for the write.
internal sealed class OfficialObjectWriter(string root) {
    private readonly ContentAddressedStore m_store = new(root: root);

    public (string Path, string Hash, long Size) Put(byte[] bytes) {
        var pin = ContentPin.Compute(content: bytes);
        var objectPath = ContentAddressedStore.ObjectPath(
            pin: pin,
            root: m_store.Root
        );

        if (File.Exists(path: objectPath)) {
            // Streamed, so re-verifying a large engine file costs no copy of it.
            if (ContentPin.OfFile(path: objectPath) != pin) {
                throw new InvalidDataException(message: $"object at '{CliPaths.ToDisplay(
                    fullPath: objectPath,
                    relativeTo: m_store.Root
                )}' does not hash to its own path — the tree is corrupted or tampered; refusing to overwrite it.");
            }
        } else {
            _ = m_store.Put(content: bytes);
        }

        return (ContentAddressedStore.ObjectRelativePath(pin: pin), pin.ToString(), bytes.LongLength);
    }
}
