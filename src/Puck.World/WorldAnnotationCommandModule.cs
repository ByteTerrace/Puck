using System.Globalization;
using Puck.Commands;
using Puck.Overlays;

namespace Puck.World;

/// <summary>
/// The <c>view.annotate</c> verb: turns the window's annotate mode on and off, keeps, removes and lists the outlines
/// drawn on screen (<see cref="WorldAnnotations"/>). Presentation only, so it submits nothing to the simulation.
/// Core-registered for command-vocabulary parity, since the debug world binds it: a headless boot keeps and lists
/// outlines it has no window to draw.
/// </summary>
/// <param name="annotations">The annotation state.</param>
public sealed class WorldAnnotationCommandModule(WorldAnnotations annotations) : ICommandModule {
    private const string Verb = "view.annotate";
    private const string Form = "[on|off|toggle|clear|undo|list|add <cx> <cy> <rx> [<ry>]]";

    private static string Describe(int index, in OverlayAnnotationCircle circle) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"[{Verb}: {index} center={circle.CenterX:0.000},{circle.CenterY:0.000} radius={circle.RadiusX:0.000},{circle.RadiusY:0.000}]"
    );
    private string State() => (annotations.Active ? "on" : "off");
    private CommandResult Add(WireArgs args) {
        if (
            (args.Count is not (4 or 5)) ||
            !args.TryFloat(index: 1, value: out var centerX) ||
            !args.TryFloat(index: 2, value: out var centerY) ||
            !args.TryFloat(index: 3, value: out var radiusX)
        ) {
            return CommandResult.Usage(form: "add <cx> <cy> <rx> [<ry>]", verb: Verb);
        }

        var radiusY = radiusX;

        if ((args.Count == 5) && !args.TryFloat(index: 4, value: out radiusY)) {
            return CommandResult.Usage(form: "add <cx> <cy> <rx> [<ry>]", verb: Verb);
        }
        if (
            !float.IsFinite(f: centerX) || (centerX < 0f) || (centerX > 1f) ||
            !float.IsFinite(f: centerY) || (centerY < 0f) || (centerY > 1f) ||
            !float.IsFinite(f: radiusX) || (radiusX <= 0f) || (radiusX > 1f) ||
            !float.IsFinite(f: radiusY) || (radiusY <= 0f) || (radiusY > 1f)
        ) {
            return CommandResult.Error(output: $"[{Verb}: add takes a center in [0, 1] and radii in (0, 1], as fractions of the window's width and height]");
        }

        var circle = new OverlayAnnotationCircle(
            CenterX: centerX,
            CenterY: centerY,
            RadiusX: radiusX,
            RadiusY: radiusY
        );

        return (annotations.TryAdd(circle: in circle, index: out var index)
            ? new CommandResult(Output: Describe(circle: in circle, index: index))
            : CommandResult.Error(output: $"[{Verb}: {AnnotationWriter.MaxCircles} outlines are already kept; clear or undo first]"));
    }
    private CommandResult List() {
        var circles = annotations.List();
        var lines = new List<string>(capacity: (circles.Length + 1)) {
            $"[{Verb}: {State()} circles={circles.Length}/{AnnotationWriter.MaxCircles}]",
        };

        for (var index = 0; (index < circles.Length); index++) {
            lines.Add(item: Describe(circle: in circles[index], index: (index + 1)));
        }

        return new CommandResult(Output: string.Join(separator: Environment.NewLine, values: lines));
    }
    private CommandResult Set(bool active) {
        annotations.SetActive(active: active);

        return new CommandResult(Output: $"[{Verb}: {State()} circles={annotations.Count}]");
    }
    private CommandResult Handle(WireArgs args) {
        if (args.Count == 0) {
            return List();
        }

        var single = (args.Count == 1);

        if (single && args.Is(index: 0, value: "on")) {
            return Set(active: true);
        }
        if (single && args.Is(index: 0, value: "off")) {
            return Set(active: false);
        }
        if (single && args.Is(index: 0, value: "toggle")) {
            return Set(active: !annotations.Active);
        }
        if (single && args.Is(index: 0, value: "list")) {
            return List();
        }
        if (single && args.Is(index: 0, value: "clear")) {
            return new CommandResult(Output: $"[{Verb}: cleared {annotations.Clear()}]");
        }
        if (single && args.Is(index: 0, value: "undo")) {
            return (annotations.RemoveLast(index: out var removed)
                ? new CommandResult(Output: $"[{Verb}: removed {removed}]")
                : CommandResult.Error(output: $"[{Verb}: no outline to undo]"));
        }
        if (args.Is(index: 0, value: "add")) {
            return Add(args: args);
        }

        return CommandResult.Usage(form: Form, verb: Verb);
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: Verb,
            description: "On-screen annotations for pointing something out: view.annotate [on|off|toggle|clear|undo|list|add <cx> <cy> <rx> [<ry>]]. In annotate mode a left drag in the window draws the ellipse fitting the drag box (a click draws a small dot ring), a right click clears every outline, and no pointer button or drag reaches a binding or the camera. Leaving the mode keeps the outlines until clear. The outlines draw on the overlay, so world.screenshot captures them. list (and bare view.annotate) echoes the mode and each outline, numbered as drawn, as center x,y and radii x,y in fractions of the window's width and height (origin top-left); add keeps one at those fractions (ry defaults to rx), undo removes the newest. Bind it with a text payload (toggle). Presentation only: nothing reaches the simulation.",
            handler: (_, args) => Handle(args: args),
            routing: CommandRouting.Immediate
        );
    }
}
