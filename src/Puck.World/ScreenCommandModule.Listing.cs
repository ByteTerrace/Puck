using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The declared screens' listing (<c>world.screens</c>) and the views' refresh cadence
/// (<c>world.view-refresh</c>). Both read only the screen binder and the live definition, which every boot shape
/// composes, so an offscreen or headless boot answers them exactly as a windowed one does.</summary>
internal sealed partial class ScreenCommandModule {
    // The source-kind keyword for a screen's declared source — the stable token a piped proof asserts against. A
    // producer source reads as its producer id.
    private static string ScreenSourceKind(WorldScreenSource source) {
        return source switch {
            WorldScreenSource.Machine machine => $"machine:{machine.Instance}:{machine.Output}",
            WorldScreenSource.Producer producer => producer.Id,
            WorldScreenSource.View => "view",
            WorldScreenSource.Probe probe => $"probe:{probe.Id}",
            WorldScreenSource.Session session => $"session:{session.Destination}",
            WorldScreenSource.Text text => $"text:{text.Lines.Count}-line",
            _ => "none",
        };
    }
    // The world.screens listing: one segment per declared screen — index, source kind, live bound/unbound state (a
    // nonzero provider handle this frame), engage policy, and the destination a pointer hit on it goes to. A query (not AcknowledgementOnly): its listing always surfaces, so a
    // piped proof can assert the test-pattern screen is bound and the None screen stays unbound (dark glass).
    private CommandResult ScreensHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[world.screens: no arguments — lists every declared screen]");
        }

        // The LIVE definition's rows (never the boot snapshot), so a screen mutation's new source narrates honestly, then
        // each creation face showing a source, as the presentation last derived it.
        var declaredScreens = new List<WorldScreen>(collection: m_server.Definition.Screens);

        foreach (var face in m_binder.Mappings.Screens) {
            if (
                (face.Index >= WorldPrototypeFacets.DerivedFaceBase) &&
                (face.Source is not WorldScreenSource.None)
            ) {
                declaredScreens.Add(item: face);
            }
        }

        if (declaredScreens.Count == 0) {
            return new CommandResult(Output: "[world.screens: none declared]");
        }

        var builder = new StringBuilder(value: "[world.screens:");

        for (var index = 0; (index < declaredScreens.Count); index++) {
            var screen = declaredScreens[index];
            var bound = (m_binder.CurrentHandle(index: screen.Index) != 0);
            // The engaged marker (only when players are engaged) — reflects the route state, kept bracket-agnostic so the
            // proof regexes are undisturbed.
            var engaged = m_engagement.PlayersOn(screenIndex: screen.Index);
            var engagedText = ((engaged.Count > 0)
                ? $" engaged:{string.Join(
                    separator: "+",
                    values: engaged.Select(selector: static entry => (entry.Capture
                    ? $"p{entry.Display}"
                    : $"p{entry.Display}(mirror)"))
                )}"
                : ""
            );
            // How a camera or capture image crosses devices: the shared fence, or the producer's CPU wait and why.
            var orderText = ((m_binder.FenceOrderAt(index: screen.Index) is { } order)
                ? $" order:{order}"
                : ""
            );

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0)
                ? " "
                : " | ")}{screen.Index} {ScreenSourceKind(source: screen.Source)} {(bound
                ? "bound"
                : "unbound")} {(screen.Route.Engageable
                ? "engageable"
                : "fixed")} input:{(screen.Route.Input ?? SourceDestination.Presentation)}{engagedText}{orderText} mapping {(m_binder.Mappings.Describe(screen: screen.Index) ?? "none (no slot)")}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    // The views' refresh cadence: no argument echoes the divisor and the camera views registered, an integer from 1
    // through 8 sets it.
    private CommandResult ViewRefreshHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return new CommandResult(Output: $"[world.view-refresh: every {m_binder.ViewRefreshDivisor} produced frame(s); {m_binder.ActiveCameraViewCount} camera view(s) registered]");
        }

        if (
            !args.TryInt(
            index: 0,
            value: out var divisor
        ) ||
            (divisor < 1) ||
            (divisor > 8)
        ) {
            return CommandResult.Error(output: $"[world.view-refresh: expected an integer divisor from 1 through 8, got '{args[0]}']");
        }

        m_binder.SetViewRefreshDivisor(divisor: divisor);

        return new CommandResult(Output: $"[world.view-refresh: every {divisor} produced frame(s)]");
    }
}
