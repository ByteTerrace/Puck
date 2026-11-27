using System.Globalization;

using Puck.Commands;
using Puck.SdfVm;

namespace Puck.World;

// world.slice.live: the live slicing plane, the same cut as world.slice drawn translucent in the world over every view of
// the World's own residency (SdfLiveFieldSlice), resampled as the plane moves or the document reloads.
public sealed partial class WorldSliceCommandModule {
    private const string LiveVerb = "world.slice.live";
    private const float DefaultLiveOpacity = 0.85f;

    // What world.slice.live reads beside world.slice's plane: the panel the plane draws and its opacity.
    private sealed class LiveOptions {
        public SdfFieldSliceMode Mode = SdfFieldSliceMode.Distance;
        public float Opacity = DefaultLiveOpacity;
    }

    private CommandDefinition LiveCommand() => CommandDefinition.WithWireArgs(
        audience: CommandAudience.Anyone,
        bindability: CommandBindability.Bindable,
        name: LiveVerb,
        description: $"Draws {Verb}'s cut live in the world, a translucent square on the plane over every view of the World's own residency: {WorldSliceLiveForms.Spelling}. The plane and square read as {Verb}'s (the explicit form's (u, v) is (Z, Y) on an x cut, (X, Z) on a y cut and (X, Y) on a z cut; the placement form frames {WorldSliceRequest.PlacementMargin} times the creation's reach), without a path. mode picks the panel {Verb}'s PNG draws: distance (default; warm outside, cool inside, a line every band, a white surface line), gradient (|grad d|: white exact, blue a bound, red an overestimate, the surface black), or both (distance on the square's -u half, gradient on its +u half); grey answers no shape and dull magenta is clipped, as in the PNG. opacity sets how much the square covers in front of a surface (default {DefaultLiveOpacity}); behind a surface it keeps a fifth of that. res defaults to {WorldSliceRequest.DefaultLiveResolution} ({SdfFieldSlice.MinResolution}..{SdfFieldSlice.MaxResolution}). The mesh bake's sampler samples the plane once for each placement and program, a frame's bake share at a time, so a standing plane costs nothing, and the square keeps its last image where it was sampled until a moved plane or a reloaded document answers. A {Verb} sampling at the time defers the live plane's sampling until it lands. off removes the plane; with none set nothing is sampled, uploaded or drawn. Refused with no renderer.",
        forms: WorldSliceLiveForms,
        handler: (context, args) => {
            if (renderProbe.Residency is not { } residency) {
                return CommandResult.Error(output: $"[{LiveVerb}: no renderer — a live slice draws over the World's residency, which a headless host does not build]");
            }
            if ((args.Count == 1) && string.Equals(a: args[0].ToString(), b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                residency.FieldSlice.Plane = null;
                return new CommandResult(Output: $"[{LiveVerb}: off]");
            }

            var tokens = new string[args.Count];

            for (var index = 0; (index < tokens.Length); index++) { tokens[index] = args[index].ToString(); }
            if ((tokens.Length > 0) && !TryResolveNear(context: context, placement: ref tokens[0], refusal: out var nearRefusal, verb: LiveVerb)) {
                return nearRefusal;
            }

            var options = new LiveOptions();

            if (!TryParse(args: tokens, definition: server.ReadView(context).Definition, live: options, refusal: out var refusal, request: out var request)) {
                return refusal;
            }

            residency.FieldSlice.Plane = new SdfFieldSlicePlane(
                Axis: request.Axis,
                Bands: request.Bands,
                Centre: request.Centre,
                Half: request.Half,
                Mode: options.Mode,
                Offset: request.Offset,
                Opacity: options.Opacity,
                Resolution: request.Resolution
            );
            return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"[{LiveVerb}: {request.Axis.ToString().ToLowerInvariant()}={request.Offset:0.###} centre=({request.Centre.X:0.###},{request.Centre.Y:0.###}) half={request.Half:0.###} {request.Resolution}x{request.Resolution} bands={request.Bands:0.####} mode={options.Mode.ToString().ToLowerInvariant()} opacity={options.Opacity:0.##}]"));
        }
    );

    private static bool TryMode(string token, out SdfFieldSliceMode mode) {
        switch (token.ToLowerInvariant()) {
            case "distance":
                mode = SdfFieldSliceMode.Distance;
                return true;
            case "gradient":
                mode = SdfFieldSliceMode.Gradient;
                return true;
            case "both":
                mode = SdfFieldSliceMode.Both;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
