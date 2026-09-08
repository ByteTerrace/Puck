using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Puck.Commands;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>hud</c> section's read-back surface. <c>world.hud</c> is an <see cref="CommandRouting.Immediate"/> read of
/// the live definition plus every bound element's live-resolved value (through the same
/// <see cref="IHudBindingResolver"/> the renderer uses) and the schema-cap reservation usage — plus, with a
/// <c>seat:&lt;n&gt;</c> filter, the same read-back for that local seat's private player-scope panel (edited through
/// <c>identity.hud &lt;panel-json&gt; [player]</c> (<see cref="IdentityCommandModule"/>), an ungated owner-side door
/// — this module writes no player-scope mutation itself, it only reads the live handle that door already wrote). The
/// world-scope section is authored through the general <see cref="WorldRowCommandModule"/> —
/// <c>world.row.set</c>/<c>world.row.remove</c> over <c>hud.panels</c>, and <c>world.row.set hud.defaults
/// &lt;json&gt;</c> — with an element riding its owning panel's whole-row upsert rather than a door of its own.
/// A separate module from
/// <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings. <c>world.hud.template</c>
/// is the one verb here with no document row behind it at all: an ad hoc template string, checked against the closed
/// <see cref="HudBindingVocabulary"/> and the live document's own <c>state</c> section, resolved on demand and never
/// stored — the console-only twin of what a <see cref="WorldHudElement.Template"/> row does when authored.
/// </summary>
internal sealed class WorldHudCommandModule(WorldServer server, IHudBindingResolver bindings, PlayerRoster roster, WorldOverlayFacts facts) : ICommandModule {
    private const string SeatFilterPrefix = "seat:";

    // A frame element's candidates in rank order (source JSON plus its condition, when one is authored) and the
    // candidate winning now for the scope asked about — the same first-holding rule the feed publishes by. slot -1
    // is the world scope (any joined seat).
    private string DescribeFrameCandidates(WorldHudElement element, int slot) {
        var candidates = element.FrameCandidates;
        var winner = OverlayRanking.FirstHolding(
            candidates: candidates,
            evaluator: facts,
            slot: slot,
            when: static candidate => candidate.When
        );
        var builder = new System.Text.StringBuilder(value: " candidates=[");

        for (var index = 0; (index < candidates.Count); index++) {
            var candidate = candidates[index];

            _ = builder.Append(value: ((index == 0) ? "" : " | ")).Append(value: JsonSerializer.Serialize(
                value: candidate.Source,
                jsonTypeInfo: WorldJsonContext.Default.WorldFrameSource
            ));

            if (candidate.When is { } when) {
                _ = builder.Append(value: " when=").Append(value: when.GetType().Name.ToLowerInvariant());
            }
        }

        return builder.Append(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"] winner={((winner >= 0) ? winner.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) : "none")} fadeSeconds={element.FadeSeconds:0.###} fit={element.Fit.ToString().ToLowerInvariant()} mirror={(element.Mirror ? "true" : "false")} radius={element.Radius:0.###} opacity={element.Opacity:0.###}"
        ).ToString();
    }
    private string DescribeElement(string panelId, WorldHudElement element, int slot) {
        var bindingToken = (element.Binding ?? "(none)");
        var frameText = string.Empty;
        var valueText = string.Empty;

        if (
            (WorldHudElementKind.Frame == element.Kind) &&
            (element.FrameCandidates.Count > 0)
        ) {
            frameText = DescribeFrameCandidates(
                element: element,
                slot: slot
            );
        }

        if (element.Template is { Length: > 0 } template) {
            // A validated document element's placeholders are already proven against the closed vocabulary and the
            // document's own state section (WorldDefinitionValidator), so this walk resolves unconditionally — the
            // same trust a render-frame's own template resolve extends.
            valueText = $" template='{template}' resolved='{ResolveTemplateEcho(template: template)}'";
        } else if (
            (element.Binding is { Length: > 0 } binding) &&
            bindings.TryResolve(
            binding: binding,
            fraction: out var fraction,
            text: out var text
        )
        ) {
            valueText = $" value='{text}' fraction={fraction:F3}";
        } else if (element.Text is { Length: > 0 } literal) {
            // An unbound text element draws its AUTHORED LITERAL (WorldHudElement.Text's own doc: "a bound TEXT
            // element's live value REPLACES the authored literal") — the read-back rule owes this the same echo a
            // bound value gets, or a static row (e.g. an addon's own event reaction) would be invisible to
            // world.hud entirely, forcing a caller to fall back to a screenshot for what is otherwise plain text.
            valueText = $" text='{literal}'";
        }

        return $"[world.hud.element '{panelId}'.'{element.Id}' kind={element.Kind.ToString().ToLowerInvariant()} style={element.Style.ToString().ToLowerInvariant()} binding={bindingToken}{frameText}{valueText}]";
    }
    private string DescribeHud() {
        var section = server.Definition.Hud;
        // The cursor policy echoes what the drawn cursor actually uses: the authored row, or 'hidden' for an
        // unauthored one (the engine draws no cursor of its own), so this read-back and the cursor cannot disagree.
        var cursorText = ((section.Defaults.Cursor is { } cursor)
            ? string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"cursorHoverRadius={cursor.HoverRadius:0.##} cursorSizePx={cursor.SizePx:0.##} cursorRole={cursor.Role.ToString().ToLowerInvariant()}"
            )
            : "cursor=hidden"
        );
        var lines = new List<string>(capacity: (1 + section.Panels.Count)) {
            $"[world.hud: enabled {(section.Defaults.Enabled
            ? "true"
            : "false")} {cursorText} panels {section.Panels.Count}/{WorldHudCapacity.MaxWorldPanels}]",
        };

        foreach (var panel in section.Panels) {
            lines.Add(item: $"[world.hud.panel '{panel.Id}' layer={panel.Layer.ToString().ToLowerInvariant()} style={panel.Style.ToString().ToLowerInvariant()} rect=({panel.Rect.X:F2},{panel.Rect.Y:F2},{panel.Rect.Width:F2},{panel.Rect.Height:F2}) elements={panel.Elements.Count}/{WorldHudCapacity.MaxElementsPerPanel}]");

            foreach (var element in panel.Elements) {
                lines.Add(item: DescribeElement(
                    panelId: panel.Id,
                    element: element,
                    slot: -1
                ));
            }
        }

        return string.Join(
            separator: Environment.NewLine,
            values: lines
        );
    }
    private CommandResult DescribeHudHandler(WireArgs args) {
        if (args.Count == 0) {
            return new CommandResult(Output: DescribeHud());
        }

        if (
            (args.Count == 1) &&
            TryParseSeatFilter(
            token: args[0].ToString(),
            seat: out var seat
        )
        ) {
            return DescribeSeatHud(seat: seat);
        }

        return CommandResult.Error(output: "[world.hud: expected no arguments, or seat:<n> (1..4)]");
    }
    private CommandResult DescribeSeatHud(int seat) {
        var slot = PlayerRoster.SlotFromDisplay(number: seat);

        if (roster.ProfileAt(slot: slot) is not { } profile) {
            return CommandResult.Error(output: $"[world.hud seat:{seat}: not joined — see world.players]");
        }

        if (profile.Hud is not { } panel) {
            return new CommandResult(Output: $"[world.hud seat:{seat}: profile '{profile.Name}' authored no player-scope panel — see identity.hud <panel-json> [player]]");
        }

        var lines = new List<string>(capacity: (1 + panel.Elements.Count)) {
            $"[world.hud seat:{seat}: profile '{profile.Name}' panel '{panel.Id}' layer={panel.Layer.ToString().ToLowerInvariant()} style={panel.Style.ToString().ToLowerInvariant()} rect=({panel.Rect.X:F2},{panel.Rect.Y:F2},{panel.Rect.Width:F2},{panel.Rect.Height:F2}) elements={panel.Elements.Count}/{WorldHudCapacity.MaxElementsPerSeatPanel}]",
        };

        foreach (var element in panel.Elements) {
            lines.Add(item: DescribeElement(
                panelId: panel.Id,
                element: element,
                slot: slot
            ));
        }

        return new CommandResult(Output: string.Join(
            separator: Environment.NewLine,
            values: lines
        ));
    }
    // An AD HOC template (never authored on a document row): parsed through the SAME HudTemplate grammar a document
    // element's own Template speaks, then EVERY placeholder is validated against the closed HudBindingVocabulary and
    // the LIVE document's state section BEFORE anything resolves — refusing the whole template by name at the FIRST
    // bad placeholder rather than interpolating some and leaving others blank.
    private CommandResult ResolveDynamicTemplate(string template) {
        if (!HudTemplate.TryParse(
            error: out var parseError,
            segments: out var segments,
            template: template
        )) {
            return CommandResult.Error(output: $"[world.hud.template: {parseError}]");
        }

        foreach (var segment in segments) {
            if (!segment.IsPlaceholder) {
                continue;
            }

            if (!HudBindingVocabulary.TryParse(
                token: segment.Text,
                binding: out var parsed
            )) {
                return CommandResult.Error(output: $"[world.hud.template: placeholder '{{{segment.Text}}}' is not in the closed HudBindingVocabulary]");
            }

            if (parsed.Kind != HudBindingKind.StateNamed) {
                continue;
            }

            if (!TryFindStateRow(
                name: parsed.StateName!,
                row: out var row
            )) {
                return CommandResult.Error(output: $"[world.hud.template: placeholder '{{{segment.Text}}}' names no declared state row '{parsed.StateName}']");
            }

            if (
                (parsed.StateCellKey is { } cellKey) &&
                !row.HasCell(key: cellKey)
            ) {
                return CommandResult.Error(output: $"[world.hud.template: placeholder '{{{segment.Text}}}' names no declared cell '{cellKey}' on state row '{parsed.StateName}']");
            }
        }

        return new CommandResult(Output: $"[world.hud.template: '{ResolveSegments(segments: segments)}']");
    }
    // The ONE substitution walk this module owns: every placeholder resolves through the SAME IHudBindingResolver
    // the renderer emits with, so an echo and the screen cannot disagree about a value. Callers decide what a
    // failed resolve MEANS — world.hud.template refuses ahead of this walk, world.hud's echo trusts a document
    // template already proven at validation — so an unresolvable placeholder simply contributes nothing here.
    private string ResolveSegments(IReadOnlyList<HudTemplateSegment> segments) {
        var resolved = new System.Text.StringBuilder();

        foreach (var segment in segments) {
            if (!segment.IsPlaceholder) {
                resolved.Append(value: segment.Text);

                continue;
            }

            if (bindings.TryResolve(
                binding: segment.Text,
                fraction: out _,
                text: out var value
            )) {
                resolved.Append(value: value);
            }
        }

        return resolved.ToString();
    }
    // HudTemplate.TryParse is the only thing anywhere that reads the brace/escape grammar — the render path is fed
    // ALREADY-PARSED runs by WorldHudFeed, so this echo and the screen agree by construction rather than by two
    // scanners being kept in step. A validated document template always parses; showing the raw text if one somehow
    // does not beats inventing a reading of it.
    private string ResolveTemplateEcho(string template) {
        return (HudTemplate.TryParse(
            error: out _,
            segments: out var segments,
            template: template
        )
            ? ResolveSegments(segments: segments)
            : template
        );
    }
    // The same (row, key) existence check HudRowValidation.ValidateElement applies to a document-authored binding,
    // split into its two steps so a refusal names the thing that is actually missing — asked of the LIVE server
    // definition here, not the pre-built stateRows dictionary validation already has in hand, through the shared
    // row finder, and the key half through the row's own WorldStateRow.HasCell.
    private bool TryFindStateRow(string name, [NotNullWhen(true)] out WorldStateRow? row) {
        row = WorldDefinitionRows.FindStateRow(
            rows: server.Definition.State,
            name: name
        );

        return (row is not null);
    }
    private static bool TryParseSeatFilter(string token, out int seat) {
        seat = 0;

        if (!token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: SeatFilterPrefix
        )) {
            return false;
        }

        return (
            CommandArgs.TryParseInt(
            text: token.AsSpan(start: SeatFilterPrefix.Length),
            value: out seat
        ) &&
            (seat >= 1) &&
            (seat <= PlayerRoster.MaxSlots)
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.hud",
            description: "Reads back the live hud state (Immediate): with no argument, the world-scope defaults row, every panel's id/layer/style/rect/element count against WorldHudCapacity's schema caps, and every element's kind/style/binding. A frame element additionally echoes its ranked source candidates (source JSON plus its condition's kind), the candidate winning now for the scope (world: any joined seat; seat:<n>: that seat), and fadeSeconds/fit/mirror/radius/opacity. With seat:<n> (1..4), that LOCAL seat's PRIVATE player-scope panel instead — authored through identity.hud <panel-json> [player] — or a refusal naming why there is none: world.hud [seat:<n>]. Either form resolves a bound element's LIVE value through the SAME IHudBindingResolver the renderer uses, and a templated element's placeholders through the SAME resolver too, so this read-back and what is on screen can never disagree.",
            handler: (_, args) => DescribeHudHandler(args: args),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.hud.template",
            description: "Resolves an AD HOC template string against the live document ON DEMAND (Immediate) — never authored, never stored: world.hud.template <template text...>. Speaks the SAME HudTemplate brace/escape grammar a document-authored WorldHudElement.Template does ('{world.tick}', '{state.score}', '{state.inventory.coin}', ...; '{{'/'}}' escape a literal brace) and the SAME closed HudBindingVocabulary, checked against the LIVE document's own state section. Every placeholder is validated BEFORE any resolution runs — a malformed template or a placeholder naming an unknown token/row/cell refuses the WHOLE template by name, never a partial or silently-empty result.",
            handler: (context, args) => {
                var template = WorldCommandArguments.Raw(
                    args: in args,
                    context: context
                );

                return ((template.Length == 0)
                    ? CommandResult.Usage(
                        form: "<template text...>",
                        verb: "world.hud.template"
                    )
                    : ResolveDynamicTemplate(template: template)
                );
            },
            routing: CommandRouting.Immediate
        );
    }
}
