using Puck.Demo.Forge;

namespace Puck.Demo.World;

/// <summary>
/// Every world has a theme song: a deliberate save's content hash, folded through a pentatonic note table into a
/// small <c>puck.audio.v1</c> document — same bytes, same tune, forever (change the world and its song changes with
/// it). Two 16-row patterns on the music voice, tempo and rests drawn from the same hash bytes, playable through
/// the existing jukebox/tracker paths (<c>--forge-tune-from tunes/&lt;name&gt;-theme.audio.json</c>, or tracker-load
/// it). Pure derivation — no clock, no randomness; the hash IS the composer.
/// </summary>
public static class WorldThemeComposer {
    // The C-major pentatonic across the brick's sweet octaves — no wrong notes, whatever the hash says.
    private static readonly string[] Pentatonic = [
        "C4", "D4", "E4", "G4", "A4",
        "C5", "D5", "E5", "G5", "A5",
    ];

    /// <summary>Composes the theme for a content hash.</summary>
    /// <param name="hashHex">The world's content hash (<c>sha256/&lt;hex64&gt;</c> or bare hex).</param>
    /// <param name="name">The tune's display name (the world's save handle).</param>
    /// <returns>A normalized-shape audio document (two patterns, order [0, 1]).</returns>
    public static AudioDocument Compose(string hashHex, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hashHex);

        var hex = (hashHex.StartsWith(value: "sha256/", comparisonType: StringComparison.Ordinal) ? hashHex["sha256/".Length..] : hashHex);
        var bytes = Convert.FromHexString(s: hex);
        var patterns = new List<IReadOnlyList<AudioRowDocument>>(capacity: 2);

        for (var pattern = 0; (pattern < 2); pattern++) {
            var rows = new List<AudioRowDocument>(capacity: 16);

            for (var row = 0; (row < 16); row++) {
                var value = bytes[(((pattern * 16) + row) % bytes.Length)];

                // Three of every eight steps rest (the hash decides WHICH three) — space is what makes it a tune
                // rather than a scale exercise; the last row of a pattern always sounds, so the loop lands.
                rows.Add(item: ((((value & 0x07) < 3) && (row != 15))
                    ? new AudioRowDocument(Duty: null, Envelope: null, Note: AudioRowDocument.Hold)
                    : new AudioRowDocument(Duty: ((value >> 6) & 0x03), Envelope: null, Note: Pentatonic[(value % Pentatonic.Length)])));
            }

            patterns.Add(item: rows);
        }

        return new AudioDocument(
            Effects: null,
            Name: name,
            Order: [0, 1],
            Patterns: patterns,
            Schema: AudioDocument.CurrentSchema,
            // 7..12 frames per row — the hash picks the gait (every world walks at its own pace).
            Tempo: (7 + (bytes[^1] % 6))
        );
    }
}
