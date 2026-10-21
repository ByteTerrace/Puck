using System.Globalization;
using System.Numerics;

using Puck.Assets;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Server;

namespace Puck.World;

/// <summary>What <c>world.slice</c> asks for: the plane, the square on it, its sampling, and the file it writes.</summary>
/// <param name="Axis">The axis the plane is normal to.</param>
/// <param name="Offset">The plane's coordinate along <paramref name="Axis"/>, in metres.</param>
/// <param name="Centre">The square's centre in the plane's <c>(u, v)</c> coordinates (<see cref="SdfFieldSlice"/>), in
/// metres.</param>
/// <param name="Half">The square's half side, in metres.</param>
/// <param name="Resolution">The samples a side.</param>
/// <param name="Bands">The distance between iso-distance bands, in metres.</param>
/// <param name="Path">The full path of the PNG the slice writes.</param>
public readonly record struct WorldSliceRequest(SdfAxis Axis, float Offset, Vector2 Centre, float Half, int Resolution, float Bands, string Path) {
    /// <summary>Gets the samples a side a request asks for when it names none.</summary>
    public const int DefaultResolution = 192;
    /// <summary>Gets how far past a placement's reach its square extends, as a share of that reach.</summary>
    public const float PlacementMargin = 1.25f;
    /// <summary>Gets the bands a square holds across its half side when the request names no spacing.</summary>
    public const float DefaultBandsPerHalf = 10f;
}

/// <summary>
/// <c>world.slice</c>: cuts the World's own residency's field on an axis-aligned plane through the mesh bake's field
/// sampler (<see cref="SdfFieldSlice"/>) and writes the distance diagnostic to a PNG: iso-distance bands with the sign and
/// the zero line on the left, the gradient magnitude on the right (white is one, an exact distance; blue a bound below
/// one; red an overestimate above it). The completion line on stderr carries the gradient's statistics, so a script reads
/// a shape's exactness without opening the image. Presentation only: it needs a renderer and changes nothing a replay
/// hashes.
/// </summary>
/// <param name="server">The authoritative server whose document names the placements a slice may centre on.</param>
/// <param name="renderProbe">The presentation owner's live renderer.</param>
/// <param name="source">The booted document, beside which <c>world.slice.save</c> writes into its <c>slices</c>
/// folder.</param>
public sealed partial class WorldSliceCommandModule(WorldServer server, WorldRenderProbe renderProbe, WorldDefinitionSource source) : ICommandModule {
    /// <summary>The folder under the booted document's package that <c>world.slice.save</c> writes into.</summary>
    public const string FolderName = "slices";

    /// <summary>The placement token <c>world.slice.save</c> reads as the placement of the spawn point nearest the
    /// calling seat's body.</summary>
    public const string NearToken = "near";

    private const string SaveVerb = "world.slice.save";
    private const string Verb = "world.slice";
    private const string Form = "<x|y|z> <offset> <u> <v> <half> <path.png> | <placement> <x|y|z> <path.png> [offset <m>] — then [bands <m>] [res <n>]";

    /// <summary>Parses a <c>world.slice</c> line's arguments: an explicit plane and square
    /// (<c>&lt;x|y|z&gt; &lt;offset&gt; &lt;u&gt; &lt;v&gt; &lt;half&gt; &lt;path.png&gt;</c>) or a placement's (<c>&lt;placement&gt;
    /// &lt;x|y|z&gt; &lt;path.png&gt; [offset &lt;m&gt;]</c>: the plane through the placement's position, offset along the axis,
    /// the square about it reaching <see cref="WorldSliceRequest.PlacementMargin"/> times its creation's reach at its
    /// scale), then optional <c>bands &lt;m&gt;</c> and <c>res &lt;n&gt;</c>.</summary>
    /// <param name="args">The arguments after the verb.</param>
    /// <param name="definition">The document whose placements and creations a placement form reads.</param>
    /// <param name="request">The parsed request, on success.</param>
    /// <param name="refusal">The refusal, naming what was wrong, on failure.</param>
    /// <returns><see langword="true"/> when the arguments parse.</returns>
    public static bool TryParse(IReadOnlyList<string> args, WorldDefinition definition, out WorldSliceRequest request, out string refusal) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(definition);

        request = default;
        if (args.Count < 3) {
            refusal = $"{Verb}: expected {Form}";
            return false;
        }

        SdfAxis axis;
        float offset;
        Vector2 centre;
        float half;
        string path;
        int next;

        if (TryAxis(token: args[0], axis: out axis)) {
            if ((args.Count < 6) || !TryNumber(token: args[1], value: out offset) || !TryNumber(token: args[2], value: out var u) ||
                !TryNumber(token: args[3], value: out var v) || !TryNumber(token: args[4], value: out half) || !(half > 0f)) {
                refusal = $"{Verb}: expected <x|y|z> <offset> <u> <v> <half> <path.png> with finite numbers and a positive half";
                return false;
            }
            centre = new Vector2(x: u, y: v);
            path = args[5];
            next = 6;
        } else {
            if (WorldDefinitionRows.FindPlacement(id: args[0], placements: definition.Placements) is not { } placement) {
                refusal = $"{Verb}: '{args[0]}' is neither an axis (x|y|z) nor a placement — placements: {string.Join(separator: ", ", values: definition.Placements.Select(selector: static row => row.Id))}";
                return false;
            }
            if (!TryAxis(token: args[1], axis: out axis)) {
                refusal = $"{Verb}: expected an axis (x|y|z) after placement '{placement.Id}', got '{args[1]}'";
                return false;
            }

            var position = (Vector3)placement.Position;
            var creation = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.PrototypeId);
            var reach = ((creation is null) ? 1f : PlacementReach(document: creation.EngineDocument));

            half = (WorldSliceRequest.PlacementMargin * reach * MathF.Abs(x: placement.Scale));
            if (!float.IsFinite(f: half) || !(half > 0f)) {
                refusal = $"{Verb}: placement '{placement.Id}' has no finite bound to frame (an unclipped lattice) — use {Verb} <x|y|z> <offset> <u> <v> <half> <path.png>";
                return false;
            }
            path = args[2];
            next = 3;
            offset = Component(axis: axis, value: position);
            if ((args.Count > (next + 1)) && string.Equals(a: args[next], b: "offset", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (!TryNumber(token: args[(next + 1)], value: out var shift)) {
                    refusal = $"{Verb}: offset expects a finite number of metres, got '{args[(next + 1)]}'";
                    return false;
                }
                offset += shift;
                next += 2;
            }
            centre = Plane(axis: axis, value: position);
        }

        var bands = (half / WorldSliceRequest.DefaultBandsPerHalf);
        var resolution = WorldSliceRequest.DefaultResolution;

        while (next < args.Count) {
            var key = args[next];

            if ((next + 1) >= args.Count) {
                refusal = $"{Verb}: '{key}' expects a value — {Form}";
                return false;
            }
            if (string.Equals(a: key, b: "bands", comparisonType: StringComparison.OrdinalIgnoreCase) && TryNumber(token: args[(next + 1)], value: out var spacing) && (spacing > 0f)) {
                bands = spacing;
            } else if (string.Equals(a: key, b: "res", comparisonType: StringComparison.OrdinalIgnoreCase) && int.TryParse(s: args[(next + 1)], provider: CultureInfo.InvariantCulture, result: out var samples) &&
                (samples >= SdfFieldSlice.MinResolution) && (samples <= SdfFieldSlice.MaxResolution)) {
                resolution = samples;
            } else {
                refusal = $"{Verb}: cannot read '{key} {args[(next + 1)]}' — bands takes a positive number of metres, res a whole number in [{SdfFieldSlice.MinResolution}, {SdfFieldSlice.MaxResolution}]";
                return false;
            }
            next += 2;
        }
        if (!path.EndsWith(value: ".png", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            refusal = $"{Verb}: the path must name a .png file, got '{path}'";
            return false;
        }

        request = new WorldSliceRequest(Axis: axis, Bands: bands, Centre: centre, Half: half, Offset: offset, Path: System.IO.Path.GetFullPath(path: path), Resolution: resolution);
        refusal = string.Empty;
        return true;
    }
    /// <summary>Writes an answered slice to its PNG and returns the line that reports it.</summary>
    /// <param name="slice">The answered slice.</param>
    /// <param name="path">The PNG's full path; its directory is created when absent.</param>
    /// <returns>The report line, <c>[world.slice] &lt;statistics&gt; -&gt; &lt;path&gt;</c>, or the failure to write.</returns>
    public static string Write(SdfFieldSlice slice, string path) {
        ArgumentNullException.ThrowIfNull(slice);

        try {
            if (System.IO.Path.GetDirectoryName(path: path) is { Length: > 0 } directory) { _ = Directory.CreateDirectory(path: directory); }
            PngEncoder.Write(height: slice.Resolution, path: path, rgba: slice.Render(), width: (2 * slice.Resolution));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return $"[{Verb}] failed to write {path}: {exception.Message}";
        }
        return $"[{Verb}] {slice.Describe()} -> {path}";
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: Verb,
            description: $"Cuts the World's field on an axis-aligned plane and writes the SDF distance diagnostic to a PNG: world.slice {Form}. The explicit form names the plane (x|y|z = offset) and the square on it by its centre (u, v) and half side, where (u, v) is (Z, Y) on an x cut, (X, Z) on a y cut and (X, Y) on a z cut; the placement form cuts through a placement's position (plus offset along the axis) over a square reaching {WorldSliceRequest.PlacementMargin} times its creation's reach. bands sets the iso-distance spacing in metres (default a tenth of the half side), res the samples a side ({SdfFieldSlice.MinResolution}..{SdfFieldSlice.MaxResolution}, default {WorldSliceRequest.DefaultResolution}). The mesh bake's sampler (the one field walk) reads the program at every sample with its tetrahedral gradient, one frame's bake share at a time, before any pending bake group. The image is two panels: on the left the distance, warm outside, cool inside, a thin line every band and a white surface line, so bands that bunch or spread show a field that is not the distance; on the right |grad d|, white within {SdfFieldSlice.GradientTolerance * 100f}% of one (exact), blue below (a bound underestimates), red above (an overestimate), the surface black; grey answers no shape. Where the answered field stops rising away from the surface (at least {SdfFieldSlice.ClipShare * 100f}% of the outside samples rise slower than {SdfFieldSlice.ClipGradient}), the samples from that ceiling up are clipped: dull magenta on both panels and left out of the statistics. Echoes the armed slice on stdout; the written file and the statistics (the field's range, the inside share, clipped= and its ceiling, world= the share the unowned segments answered, then |grad d| min, mean, max and the share off one, overall and near-surface within three pixels of the surface) land on stderr as [world.slice] ... -> <path>; world.wait bakes <seconds> holds a script until it lands. One slice at a time. Refused with no renderer.",
            handler: (_, args) => {
                var tokens = new string[args.Count];

                for (var index = 0; (index < tokens.Length); index++) { tokens[index] = args[index].ToString(); }
                if (!TryParse(args: tokens, definition: server.Definition, refusal: out var refusal, request: out var request)) {
                    return CommandResult.Error(output: $"[{refusal}]");
                }
                return Arm(request: request);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SaveVerb,
            description: $"Slices the field through a placement into the booted package's own {FolderName} folder: {SaveVerb} <placement|near> <x|y|z> [offset <m>] [bands <m>] [res <n>], as {Verb}'s placement form, writing <package>/{FolderName}/<placement>-<axis>.png. The caller names no path, so a binding row or an authored command (the debug world's gallery.slice) may call it; the placement id must be a file-safe token (letters, digits, '-', '_', '.').",
            handler: (context, args) => {
                if (args.Count < 2) {
                    return CommandResult.Error(output: $"[{SaveVerb}: expected <placement|near> <x|y|z> [offset <m>] [bands <m>] [res <n>]]");
                }

                var placement = args[0].ToString();

                if (string.Equals(a: placement, b: NearToken, comparisonType: StringComparison.Ordinal)) {
                    var seat = ((context.Principal.Kind == PrincipalKind.Seat)
                        ? context.Principal.Index
                        : context.Slot
                    );

                    if (NearestSpawnedPlacement(body: seat) is not { } nearest) {
                        return CommandResult.Error(output: $"[{SaveVerb}: 'near' names the placement of the spawn point nearest seat {(seat + 1)}'s body, and none stands there]");
                    }

                    placement = nearest;
                }
                var axis = args[1].ToString().ToLowerInvariant();

                if (!IsFileToken(token: placement)) {
                    return CommandResult.Error(output: $"[{SaveVerb}: placement '{placement}' is not a file-safe token]");
                }

                var tokens = new string[(args.Count + 1)];

                tokens[0] = placement;
                tokens[1] = axis;
                tokens[2] = System.IO.Path.Combine(
                    path1: WorldDocumentPaths.DirectoryOf(documentPath: source.SourcePath),
                    path2: FolderName,
                    path3: $"{placement}-{axis}.png"
                );
                for (var index = 2; (index < args.Count); index++) { tokens[(index + 1)] = args[index].ToString(); }
                if (!TryParse(args: tokens, definition: server.Definition, refusal: out var refusal, request: out var request)) {
                    return CommandResult.Error(output: $"[{refusal}]");
                }
                return Arm(request: request);
            }
        );
    }

    // The placement sharing its id with the spawn point nearest the body (a gallery exhibit's spawn stands before it,
    // named after it), or null when the body is inactive or no such spawn point names a placement.
    private string? NearestSpawnedPlacement(int body) {
        if (server.Body(index: body) is not { } subject) {
            return null;
        }

        var definition = server.Definition;
        var position = subject.FixedPosition.ToVector3();
        string? nearest = null;
        var best = float.PositiveInfinity;

        foreach (var point in definition.SpawnPoints) {
            var distance = Vector3.DistanceSquared(value1: point.Position, value2: position);

            if (
                (distance < best) &&
                (WorldDefinitionRows.FindPlacement(id: point.Id, placements: definition.Placements) is not null)
            ) {
                best = distance;
                nearest = point.Id;
            }
        }

        return nearest;
    }
    // A creation's reach; an unbounded lattice clipped by a creation-wide intersection is bounded by its clip, which only
    // the composed bound sees (a scope-free creation's shapes are not one field, so their blends bound nothing).
    private static float PlacementReach(CreationDocument document) {
        var reach = CreationGeometry.Reach(document: document);

        return (float.IsFinite(f: reach) || !CreationStampEmitter.RequiresScope(document: document))
            ? reach
            : CreationStampEmitter.RenderReach(document: document, scale: 1f, fontFor: null, composeBlends: true);
    }
    private static bool IsFileToken(string token) =>
        ((token.Length is > 0 and <= 96) && token.All(predicate: static c => (char.IsAsciiLetterOrDigit(c: c) || (c is '-' or '_' or '.'))) && (token is not ("." or "..")));

    private static bool TryAxis(string token, out SdfAxis axis) {
        axis = token.ToLowerInvariant() switch {
            "x" => SdfAxis.X,
            "y" => SdfAxis.Y,
            "z" => SdfAxis.Z,
            _ => (SdfAxis)uint.MaxValue,
        };
        return (axis <= SdfAxis.Z);
    }
    private static bool TryNumber(string token, out float value) =>
        (float.TryParse(s: token, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out value) && float.IsFinite(f: value));
    private static float Component(SdfAxis axis, Vector3 value) => axis switch {
        SdfAxis.X => value.X,
        SdfAxis.Y => value.Y,
        _ => value.Z,
    };
    // A point's (u, v) on a plane normal to the axis (SdfFieldSlice's convention).
    private static Vector2 Plane(SdfAxis axis, Vector3 value) => axis switch {
        SdfAxis.X => new Vector2(x: value.Z, y: value.Y),
        SdfAxis.Y => new Vector2(x: value.X, y: value.Z),
        _ => new Vector2(x: value.X, y: value.Y),
    };
    private CommandResult Arm(WorldSliceRequest request) => ((renderProbe.Residency is { } residency)
        ? ArmOn(request: request, residency: residency)
        : CommandResult.Error(output: $"[{Verb}: no renderer — a slice samples the World's residency, which a headless host does not build]"));
}
