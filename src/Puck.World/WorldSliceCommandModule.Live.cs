using System.Globalization;
using System.Numerics;

using Puck.Commands;
using Puck.SdfVm;

namespace Puck.World;

// world.slice.live: the live slicing plane over every view of the World's own residency, the field evaluated at every
// render pixel where the plane shows (SdfFieldView, the field pass of passes/sdf-world-field.comp.hlsl).
public sealed partial class WorldSliceCommandModule {
    private const string LiveVerb = "world.slice.live";
    private const float DefaultLiveOpacity = 0.85f;
    private const float DefaultLiveBand = 0.1f;
    private const float DefaultCameraDistance = 2f;

    private static readonly CommandForms WorldSliceLiveForms = CommandForms.Of(verb: LiveVerb,
        "off",
        "camera [<distance-m>] [bands <m>] [mode distance|gradient|both] [opacity <0..1>]",
        "eye [<height-m>] [bands <m>] [mode distance|gradient|both] [opacity <0..1>]",
        "pointer [x|y|z] [bands <m>] [mode distance|gradient|both] [opacity <0..1>]",
        "<x|y|z> <offset> [bands <m>] [mode distance|gradient|both] [opacity <0..1>]",
        "<placement|near> <x|y|z> [offset <m>] [bands <m>] [mode distance|gradient|both] [opacity <0..1>]");

    private CommandDefinition LiveCommand() => CommandDefinition.WithWireArgs(
        audience: CommandAudience.Anyone,
        bindability: CommandBindability.Bindable,
        name: LiveVerb,
        description: $"Draws a slicing plane through the World's field over every view of the World's own residency, the field evaluated at every render pixel where the plane shows, through the field the renderer bakes (every placement, body and template copy where it stands): {WorldSliceLiveForms.Spelling}. camera faces each view <distance-m> ahead of the eye (default {DefaultCameraDistance}); eye is horizontal <height-m> above the eye (default 0); pointer passes through the surface under the pointer (two metres along its ray over the sky), facing the view or normal to the axis named, and keeps its last point while the pointer is off the view; <x|y|z> <offset> is the fixed plane where that coordinate is offset; the placement form passes through a placement's position (plus offset along the axis), its bands a tenth of {WorldSliceRequest.PlacementMargin} times the creation's reach. bands sets the band spacing in metres (default {DefaultLiveBand}). mode picks what the plane shows: distance (default; warm bands outside, cool inside, darkening toward the surface, a white surface line, so bands that bunch, spread or kink show where a min or max breaks the distance), gradient (|grad d| from four taps: white exact, blue a bound, red an overestimate, the surface black), or both (distance on the view's left half, gradient on its right). opacity sets how much the plane covers in front of a surface (default {DefaultLiveOpacity}); behind a surface it keeps a fifth of that. The plane answers everywhere the resident world reaches: outside every shape's bound it reads the bound, which its gradient shows blue. off removes the plane; with no plane and no proximity view (world.debug-view proximity) nothing is evaluated, uploaded or drawn. Refused with no renderer.",
        forms: WorldSliceLiveForms,
        handler: (context, args) => {
            if (renderProbe.Residency is not { } residency) {
                return CommandResult.Error(output: $"[{LiveVerb}: no renderer — a live slice draws over the World's residency, which a headless host does not build]");
            }

            var tokens = new string[args.Count];

            for (var index = 0; (index < tokens.Length); index++) { tokens[index] = args[index].ToString(); }
            if ((tokens.Length == 1) && string.Equals(a: tokens[0], b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                residency.FieldView.Plane = null;
                residency.FieldView.Pointer = null;
                return new CommandResult(Output: $"[{LiveVerb}: off]");
            }
            if ((tokens.Length > 0) && !TryResolveNear(context: context, placement: ref tokens[0], refusal: out var nearRefusal, verb: LiveVerb)) {
                return nearRefusal;
            }
            if (!TryParseLive(args: tokens, definition: server.ReadView(context).Definition, plane: out var plane, refusal: out var refusal)) {
                return refusal;
            }
            residency.FieldView.Pointer = ((plane.Kind == SdfFieldPlaneKind.Pointer) ? PointerOf(cursor: cursor) : null);
            residency.FieldView.Plane = plane;
            return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"[{LiveVerb}: {plane.Kind.ToString().ToLowerInvariant()} normal=({plane.Normal.X:0.###},{plane.Normal.Y:0.###},{plane.Normal.Z:0.###}) offset={plane.Offset:0.###} bands={plane.Band:0.####} mode={PanelName(panel: plane.Panel)} opacity={plane.Opacity:0.##}]"));
        }
    );

    /// <summary>Parses a <c>world.slice.live</c> line's arguments into its plane: <c>camera [&lt;distance-m&gt;]</c>,
    /// <c>eye [&lt;height-m&gt;]</c>, <c>pointer [x|y|z]</c>, <c>&lt;x|y|z&gt; &lt;offset&gt;</c> or
    /// <c>&lt;placement&gt; &lt;x|y|z&gt; [offset &lt;m&gt;]</c>, then optional <c>bands &lt;m&gt;</c>,
    /// <c>mode distance|gradient|both</c> and <c>opacity &lt;0..1&gt;</c>.</summary>
    /// <param name="args">The arguments after the verb, a placement's <c>near</c> already resolved.</param>
    /// <param name="definition">The document whose placements and creations the placement form reads.</param>
    /// <param name="plane">The parsed plane, on success.</param>
    /// <param name="refusal">The refusal, naming what was wrong, on failure.</param>
    /// <returns><see langword="true"/> when the arguments parse.</returns>
    public static bool TryParseLive(IReadOnlyList<string> args, WorldDefinition definition, out SdfFieldPlane plane, out CommandResult refusal) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(definition);

        plane = null!;
        if (args.Count == 0) {
            refusal = WorldSliceLiveForms.Refuse();
            return false;
        }

        var kind = SdfFieldPlaneKind.Axis;
        var normal = Vector3.Zero;
        var offset = 0f;
        var band = DefaultLiveBand;
        var next = 1;

        switch (args[0].ToLowerInvariant()) {
            case "camera":
                kind = SdfFieldPlaneKind.Camera;
                offset = DefaultCameraDistance;
                if ((args.Count > 1) && TryNumber(token: args[1], value: out var distance)) {
                    if (!(distance > 0f)) {
                        refusal = CommandResult.Error(output: $"[{LiveVerb}: camera takes a positive distance ahead of the eye, got '{args[1]}']");
                        return false;
                    }
                    offset = distance;
                    next = 2;
                }
                break;
            case "eye":
                kind = SdfFieldPlaneKind.Eye;
                if ((args.Count > 1) && TryNumber(token: args[1], value: out var height)) {
                    offset = height;
                    next = 2;
                }
                break;
            case "pointer":
                kind = SdfFieldPlaneKind.Pointer;
                if ((args.Count > 1) && TryAxis(token: args[1], axis: out var pointerAxis)) {
                    normal = AxisNormal(axis: pointerAxis);
                    next = 2;
                }
                break;
            default:
                if (TryAxis(token: args[0], axis: out var axis)) {
                    if ((args.Count < 2) || !TryNumber(token: args[1], value: out offset)) {
                        refusal = CommandResult.Error(output: $"[{LiveVerb}: <x|y|z> <offset> takes a finite offset in metres]");
                        return false;
                    }
                    normal = AxisNormal(axis: axis);
                    next = 2;
                    break;
                }
                if (WorldDefinitionRows.FindPlacement(id: args[0], placements: definition.Placements) is not { } placement) {
                    refusal = CommandResult.Error(output: $"[{LiveVerb}: '{args[0]}' is neither a plane kind (camera|eye|pointer), an axis (x|y|z) nor a placement — placements: {string.Join(separator: ", ", values: definition.Placements.Select(selector: static row => row.Id))}]");
                    return false;
                }
                if ((args.Count < 2) || !TryAxis(token: args[1], axis: out var placementAxis)) {
                    refusal = WorldSliceLiveForms.Refuse(got: ((args.Count < 2) ? string.Empty : args[1]));
                    return false;
                }

                var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);
                var creation = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.PrototypeId);
                var reach = (((creation is null) ? 1f : PlacementReach(document: creation.EngineDocument)) * MathF.Abs(x: frame.Scale) * WorldSliceRequest.PlacementMargin);

                normal = AxisNormal(axis: placementAxis);
                offset = frame.Position[((int)placementAxis)];
                if (float.IsFinite(f: reach) && (reach > 0f)) { band = (reach / WorldSliceRequest.DefaultBandsPerHalf); }
                next = 2;
                if ((args.Count > (next + 1)) && string.Equals(a: args[next], b: "offset", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    if (!TryNumber(token: args[(next + 1)], value: out var shift)) {
                        refusal = CommandResult.Error(output: $"[{LiveVerb}: offset expects a finite number of metres, got '{args[(next + 1)]}']");
                        return false;
                    }
                    offset += shift;
                    next += 2;
                }
                break;
        }

        var panel = SdfFieldPanel.Distance;
        var opacity = DefaultLiveOpacity;

        while (next < args.Count) {
            var key = args[next];

            if ((next + 1) >= args.Count) {
                refusal = CommandResult.Error(output: $"[{LiveVerb}: '{key}' expects a value — {WorldSliceLiveForms.Form}]");
                return false;
            }

            var value = args[(next + 1)];

            if (string.Equals(a: key, b: "bands", comparisonType: StringComparison.OrdinalIgnoreCase) && TryNumber(token: value, value: out var spacing) && (spacing > 0f)) {
                band = spacing;
            } else if (string.Equals(a: key, b: "mode", comparisonType: StringComparison.OrdinalIgnoreCase) && TryPanel(token: value, panel: out var parsed)) {
                panel = parsed;
            } else if (string.Equals(a: key, b: "opacity", comparisonType: StringComparison.OrdinalIgnoreCase) && TryNumber(token: value, value: out var share) && (share > 0f) && (share <= 1f)) {
                opacity = share;
            } else {
                refusal = CommandResult.Error(output: $"[{LiveVerb}: cannot read '{key} {value}' — bands takes a positive number of metres, mode one of distance|gradient|both, opacity a number in (0, 1]]");
                return false;
            }
            next += 2;
        }

        plane = new SdfFieldPlane(Band: band, Kind: kind, Normal: normal, Offset: offset, Opacity: opacity, Panel: panel);
        refusal = default;
        return true;
    }

    // The pointer a pointer plane follows: where the drawn cursor lies in its seat's view, while it has a position there.
    private static Func<Vector2?>? PointerOf(WorldCursorFeed? cursor) => ((cursor is null)
        ? null
        : () => ((cursor.Status is { Reason: not ("no-position" or "no-view" or "outside-viewport") } status) ? status.Local : null));
    private static Vector3 AxisNormal(Puck.SignedDistance.SdfAxis axis) => axis switch {
        Puck.SignedDistance.SdfAxis.X => Vector3.UnitX,
        Puck.SignedDistance.SdfAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };
    private static string PanelName(SdfFieldPanel panel) => panel switch {
        SdfFieldPanel.Gradient => "gradient",
        SdfFieldPanel.Both => "both",
        _ => "distance",
    };
    private static bool TryPanel(string token, out SdfFieldPanel panel) {
        switch (token.ToLowerInvariant()) {
            case "distance":
                panel = SdfFieldPanel.Distance;
                return true;
            case "gradient":
                panel = SdfFieldPanel.Gradient;
                return true;
            case "both":
                panel = SdfFieldPanel.Both;
                return true;
            default:
                panel = default;
                return false;
        }
    }
}
