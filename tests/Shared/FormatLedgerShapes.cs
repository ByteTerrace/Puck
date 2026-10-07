using System.Text.Json;

namespace Puck.Testing;

/// <summary>The shape fingerprint <c>FormatVersions.json</c> records for a format: what the codec that owns it must write in its
/// header or handshake, read from the checked-in ledger, not from the generated constant the codec itself reads.</summary>
internal static class FormatLedgerShapes {
    /// <summary>Returns the recorded shape of <paramref name="id"/>.</summary>
    /// <param name="id">The format's ledger id, <c>Type.Member</c>.</param>
    /// <returns>The sixteen-digit fingerprint.</returns>
    internal static string Of(string id) {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            var path = Path.Combine(
                path1: directory.FullName,
                path2: "FormatVersions.json"
            );

            if (File.Exists(path: path)) {
                using var document = JsonDocument.Parse(json: File.ReadAllText(path: path));

                return document.RootElement.GetProperty(propertyName: "formats").GetProperty(propertyName: id).GetProperty(propertyName: "shape").GetString()!;
            }
        }

        throw new InvalidOperationException(message: "No checkout holds the test assembly.");
    }
}
