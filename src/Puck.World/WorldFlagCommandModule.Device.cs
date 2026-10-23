using System.Text.Json;
using Puck.Abstractions.Presentation;

namespace Puck.World;

// world.flag's device work: reading whether the live render root can take a capture, and arming it there.
public sealed partial class WorldFlagCommandModule {
    // Why the built renderer cannot capture now, or null when it can.
    private static string? CaptureRefusal(WorldRenderProbe probe) => ((probe.Root is not { } root)
        ? "the renderer is not built yet"
        : ((root.PendingCapturePath is { } outstanding)
            ? $"a capture of {outstanding} is still pending"
            : null
        )
    );
    // The record's capture fields: the PNG's name when a capture is armed, or null and why none is.
    private static void WriteCapture(Utf8JsonWriter writer, string? refusal, string capturePath) {
        if (refusal is not null) {
            writer.WriteNull(propertyName: "capture");
            writer.WriteString(
                propertyName: "captureRefused",
                value: refusal
            );
        } else {
            writer.WriteString(
                propertyName: "capture",
                value: Path.GetFileName(path: capturePath)
            );
        }
    }
    // Arms a one-shot capture of the next composed frame to the path and returns the echo's capture word.
    private string Arm(WorldRenderProbe probe, string capturePath) {
        var request = new FrameCaptureRequest(path: capturePath);

        _ = request.Completion.ContinueWith(
            continuationAction: static (completed, state) => {
                if (completed.Result.Error is { } error) {
                    Console.Error.WriteLine(value: $"[capture] refused {state}: {error.Message}");
                }
            },
            scheduler: TaskScheduler.Default,
            state: capturePath
        );
        captureScheduler.ArmUnscheduled(
            request: request,
            target: probe.Root!
        );

        return $"capture pending {capturePath}";
    }
}
