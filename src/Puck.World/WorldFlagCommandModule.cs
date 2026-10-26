using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// <c>world.flag &lt;exhibit&gt; [note…]</c>: a player's "this looks wrong" mark on an exhibit. It records the exhibit, the
/// note, the tick, the flagging seat's authoritative pose and its render camera to
/// <c>&lt;package&gt;/flags/&lt;exhibit&gt;.json</c> beside the booted document, and arms a one-shot capture of the next
/// composed frame to <c>&lt;package&gt;/flags/&lt;exhibit&gt;.png</c>, so an agent reading the folder later has the view
/// the player saw and the pose to return to. The folder is the package's own (the debug world git-ignores
/// <c>worlds/debug/flags/</c>); a caller never names a path, and the exhibit token is restricted to a file-safe
/// alphabet so no argument reaches outside the folder.
/// </summary>
/// <remarks>Bindable, so a binding row or a document-authored command (the debug world's <c>gallery.flag</c>) may
/// call it. Only the host console and a local seat may flag: a peer or an addon has no seat camera here, and the folder
/// is this machine's. A flag of the same exhibit overwrites the earlier one, as the latest look is the one to act on.
/// The capture arms the way <c>world.screenshot</c> does and the same stderr <c>[capture]</c> line says when the
/// PNG exists; headless, or while another capture is pending, the record is still written and the echo says no
/// capture was armed.</remarks>
public sealed partial class WorldFlagCommandModule(IServerLink link, WorldClient client, WorldCaptureScheduler captureScheduler, Func<string> packageDirectory, WorldSeatViewports? viewports = null, WorldRenderProbe? renderProbe = null) : ICommandModule {
    /// <summary>The folder under the package the records and captures land in.</summary>
    public const string FolderName = "flags";
    /// <summary>The exhibit token that names the spawn point nearest the flagging seat's body, so a key bound to
    /// <c>world.flag near</c> flags whatever exhibit the player stands at, in a world that names a spawn point after
    /// each exhibit it stands before.</summary>
    public const string NearToken = "near";

    // The id of the declared spawn point nearest the body's presented position, or null when the world declares none.
    private string? NearestSpawnPoint(int body) {
        var position = client.Position(index: body);
        string? nearest = null;
        var best = float.PositiveInfinity;

        foreach (var point in client.Definition.SpawnPoints) {
            var distance = Vector3.DistanceSquared(value1: point.Position, value2: position);

            if (distance < best) {
                best = distance;
                nearest = point.Id;
            }
        }

        return nearest;
    }
    private static bool IsExhibitToken(ReadOnlySpan<char> token) {
        if (token.IsEmpty || (token.Length > 96)) { return false; }
        foreach (var c in token) {
            if (!(char.IsAsciiLetterOrDigit(c: c) || (c is '-' or '_' or '.'))) { return false; }
        }
        return !(token.SequenceEqual(other: ".") || token.SequenceEqual(other: ".."));
    }
    private static void WriteVector(Utf8JsonWriter writer, string name, Vector3 value) {
        writer.WriteStartArray(propertyName: name);
        writer.WriteNumberValue(value: value.X);
        writer.WriteNumberValue(value: value.Y);
        writer.WriteNumberValue(value: value.Z);
        writer.WriteEndArray();
    }
    private CommandResult Flag(CommandContext context, in WireArgs args) {
        var principal = context.Principal;

        if (principal.Kind is not (PrincipalKind.Console or PrincipalKind.Seat)) {
            return CommandResult.Error(output: "[world.flag: flagging requires the host console or a local seat]");
        }
        if (args.Count == 0) {
            return CommandResult.Error(output: "[world.flag: expected world.flag <exhibit> [note…]]");
        }
        if (!IsExhibitToken(token: args[0])) {
            return CommandResult.Error(output: $"[world.flag: exhibit '{args[0].ToString()}' is not a file-safe token (letters, digits, '-', '_', '.')]");
        }

        var slot = ((principal.Kind == PrincipalKind.Seat)
            ? principal.Index
            : context.Slot
        );
        var exhibit = args[0].ToString();

        if (string.Equals(a: exhibit, b: NearToken, comparisonType: StringComparison.Ordinal)) {
            if (NearestSpawnPoint(body: slot) is not { } nearest) {
                return CommandResult.Error(output: "[world.flag: 'near' names the spawn point nearest the seat's body, and the world declares none]");
            }
            if (!IsExhibitToken(token: nearest)) {
                return CommandResult.Error(output: $"[world.flag: the nearest spawn point '{nearest}' is not a file-safe token]");
            }

            exhibit = nearest;
        }

        var note = string.Empty;

        for (var index = 1; (index < args.Count); index++) {
            note = ((note.Length == 0)
                ? args[index].ToString()
                : string.Concat(str0: note, str1: " ", str2: args[index].ToString())
            );
        }

        var pose = "[world.flag: no pose answer]";

        link.Query(
            query: new WorldQuery.PlayerWhere(Index: slot),
            completion: answer => pose = answer.Text
        );

        // Headless composes no seat viewports: the record then carries the pose alone.
        var view = (viewports?.Seat(slot: slot) ?? default);
        var tick = client.StateMirror.Tick;
        string folder;
        string recordPath;
        string capturePath;

        try {
            folder = Path.Combine(
                path1: packageDirectory(),
                path2: FolderName
            );
            _ = Directory.CreateDirectory(path: folder);
            recordPath = Path.GetFullPath(path: Path.Combine(
                path1: folder,
                path2: (exhibit + ".json")
            ));
            capturePath = Path.GetFullPath(path: Path.Combine(
                path1: folder,
                path2: (exhibit + ".png")
            ));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            return CommandResult.Error(output: $"[world.flag: could not create the flags folder ({exception.Message})]");
        }

        // The capture is decided before the record is written so the record says whether a PNG is coming.
        var captureRefusal = ((renderProbe is null)
            ? "no renderer to capture (headless)"
            : CaptureRefusal(probe: renderProbe)
        );

        try {
            using var stream = new FileStream(
                access: FileAccess.Write,
                mode: FileMode.Create,
                path: recordPath,
                share: FileShare.None
            );
            using var writer = new Utf8JsonWriter(
                utf8Json: stream,
                options: new JsonWriterOptions { Indented = true, }
            );

            writer.WriteStartObject();
            writer.WriteString(
                propertyName: "exhibit",
                value: exhibit
            );
            writer.WriteString(
                propertyName: "note",
                value: note
            );
            writer.WriteNumber(
                propertyName: "tick",
                value: tick
            );
            writer.WriteNumber(
                propertyName: "seat",
                value: (slot + 1)
            );
            writer.WriteString(
                propertyName: "principal",
                value: principal.Describe()
            );
            writer.WriteString(
                propertyName: "pose",
                value: pose
            );
            if (view.Present) {
                writer.WriteStartObject(propertyName: "camera");
                WriteVector(
                    name: "position",
                    value: view.Camera.Position,
                    writer: writer
                );
                WriteVector(
                    name: "forward",
                    value: view.Camera.Forward,
                    writer: writer
                );
                WriteVector(
                    name: "up",
                    value: view.Camera.Up,
                    writer: writer
                );
                writer.WriteNumber(
                    propertyName: "tanHalfFieldOfView",
                    value: view.Camera.TanHalfFieldOfView
                );
                writer.WriteEndObject();
            } else {
                writer.WriteNull(propertyName: "camera");
            }
            WriteCapture(
                capturePath: capturePath,
                refusal: captureRefusal,
                writer: writer
            );
            writer.WriteEndObject();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            return CommandResult.Error(output: $"[world.flag: could not write {recordPath} ({exception.Message})]");
        }

        var captureWord = ((captureRefusal is null)
            ? Arm(capturePath: capturePath, probe: renderProbe!)
            : $"no capture armed ({captureRefusal})"
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.flag: {exhibit} \"{note}\" at tick {tick} seat {(slot + 1)} recorded {recordPath}; {captureWord}; {pose}]"
        ));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.flag",
            description: "Flags an exhibit that looks wrong: world.flag <exhibit|near> [note…]; near names the spawn point nearest the flagging seat's body. Writes <package>/flags/<exhibit>.json beside the booted document (exhibit, note, tick, the flagging seat, its authoritative pose as body.where reads it, and its render camera) and arms a one-shot capture of the next composed frame to <package>/flags/<exhibit>.png, announced on stderr as world.screenshot's is. The exhibit is a file-safe token (letters, digits, '-', '_', '.'); a caller never names a path. A flag of the same exhibit overwrites the earlier one. Host console or local seat only (the folder is this machine's and a peer has no seat camera here); headless, or while another capture is pending, the record is still written and the echo says no capture was armed. Bindable, so a binding row or an authored command may call it.",
            handler: (context, args) => Flag(
                args: in args,
                context: context
            )
        );
    }
}
