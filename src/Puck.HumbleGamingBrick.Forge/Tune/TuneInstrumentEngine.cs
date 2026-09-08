using System.Text.Json;
using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Forge.Tune;

/// <summary>
/// The <see cref="IScreenMachineEngine"/> for a diegetic, player-operated instrument — a screen whose content is a
/// <c>puck.audio.v1</c> document (see <see cref="Puck.Assets.Documents.AudioDocument"/>) rather than a cartridge ROM.
/// Its <see cref="Id"/> is <c>tune-instrument</c>; it takes no options. <see cref="Create"/> parses, validates, and
/// normalizes the content through <see cref="Puck.Assets.Documents.AudioCanonicalizer"/>, compiles it to a jukebox
/// cart through <see cref="TuneRom.Build"/>, and boots it on a real <see cref="Puck.HumbleGamingBrick.MachineHost"/>
/// — the same host <c>gaming-brick</c> uses, so the instrument gets a real diegetic screen (the jukebox's own
/// title/transport display) and real audio (<c>IAudioMachine</c>) for free, engaged and pad-driven exactly like any
/// other screen machine, plus <see cref="Puck.Abstractions.Machines.IInstrumentClockSource"/> — the tempo capability
/// no other engine here reports.
/// </summary>
public sealed class TuneInstrumentEngine : IScreenMachineEngine {
    /// <inheritdoc/>
    public string Id => "tune-instrument";

    /// <inheritdoc/>
    public IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
        new TuneInstrumentMachine(
            audioSampleRate: audioSampleRate,
            content: contentBytes,
            savePath: savePath
        );

    /// <summary>Parses, validates, and normalizes a <c>puck.audio.v1</c> content image — the one place this engine's
    /// content format is understood, shared by construction and by a live content swap.</summary>
    /// <param name="content">The raw content bytes read from the screen's content path.</param>
    /// <returns>The normalized document.</returns>
    /// <exception cref="ArgumentException">The bytes do not parse, or fail <c>puck.audio.v1</c> validation —
    /// caught by <c>Puck.World.Server.WorldMachineHost</c>'s own boot/insert fault handling, which recognizes only
    /// this exception type.</exception>
    internal static Puck.Assets.Documents.AudioDocument ParseContent(byte[] content) {
        Puck.Assets.Documents.AudioDocument? document;

        try {
            document = JsonSerializer.Deserialize<Puck.Assets.Documents.AudioDocument>(
                utf8Json: content,
                options: Puck.Assets.Documents.DocumentJsonOptions.Shared
            );
        } catch (JsonException exception) {
            throw new ArgumentException(message: $"tune-instrument content is not valid puck.audio.v1 JSON: {exception.Message}");
        }

        if (document is null) {
            throw new ArgumentException(message: "tune-instrument content parsed to no document.");
        }

        try {
            return Puck.Assets.Documents.AudioCanonicalizer.Canonicalize(document: document).Document;
        } catch (Puck.Assets.Documents.DocumentValidationException exception) {
            throw new ArgumentException(message: $"tune-instrument content failed puck.audio.v1 validation: {exception.Message}");
        }
    }
}
