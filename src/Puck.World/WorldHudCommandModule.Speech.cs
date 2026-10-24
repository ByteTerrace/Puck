using Puck.Commands;
using Puck.World.Audio;

namespace Puck.World;

internal sealed partial class WorldHudCommandModule {
    private CommandResult RenderSpeech(WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Usage(form: "<panelId> <elementId> <output.wav>", verb: "audio.render");
        }
        var panelId = args[0].ToString();
        var elementId = args[1].ToString();
        var panel = server.Definition.Hud.Panels.FirstOrDefault(predicate: row => (row.Id == panelId));
        var element = panel?.Elements.FirstOrDefault(predicate: row => (row.Id == elementId));

        if (element is not { Kind: WorldHudElementKind.Text, Voice: { } voice }) {
            return CommandResult.Error(output: "[audio.render: expected a HUD Text element with an authored voice]");
        }
        var text = WorldHudSpeechFeed.ResolveText(bindings: bindings, element: element, seat: -1);

        if (!WorldTextSpeechFactory.TryCreate(server.Definition, voice, text, out var source, out var error)) {
            return CommandResult.Error(output: $"[audio.render: {error}]");
        }

        try {
            var path = Puck.Abstractions.PuckPaths.Normalize(path: args[2].ToString());

            if (Path.GetDirectoryName(path: path) is { } directory) {
                Directory.CreateDirectory(path: directory);
            }
            using var output = File.Create(path: path);

            WorldSpeechRenderer.WriteWave(output: output, source: source!);
            return new CommandResult(Output: $"[audio.render: '{path}' frames={source!.TotalFrames} characters={source.Schedule.Count}]");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)) {
            return CommandResult.Error(output: $"[audio.render: {exception.Message}]");
        }
    }
}
