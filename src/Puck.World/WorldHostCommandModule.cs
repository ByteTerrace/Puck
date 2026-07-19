using System.Globalization;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The host-section verb surface — <c>world.host</c> (the three-way read-back), <c>world.host.set</c> (whole-row upsert),
/// and <c>world.host.tune</c> (per-field RMW sugar). The two write verbs route <see cref="CommandRouting.Simulation"/>
/// into the same <see cref="WorldMutation.SetHostDefaults"/> the editor drives, so the stdin barrier serializes a
/// following read-after-write for free. A SEPARATE module from <see cref="WorldMutationCommandModule"/> because the
/// <c>world.host</c> read needs <see cref="PresentPacingControl"/> and <see cref="GpuTimingControl"/> (the two live
/// levers), which would push that class past its analyzer ceiling.
/// </summary>
/// <remarks>The host section is DOCUMENT-DEFAULTS class: <c>world.host.tune</c> / <c>world.host.set</c> move the
/// DOCUMENT (next boot for the boot-only fields; the value the next boot wakes on for the two live-lever fields), never
/// the live levers — <c>world.target</c> / <c>world.timing</c> own those. <c>world.host</c>'s three columns make the
/// split visible: which fields the CLI overrode (DOCUMENT vs RESOLVED) and which levers have drifted (DOCUMENT vs LIVE).</remarks>
internal sealed class WorldHostCommandModule(WorldServer server, IServerLink link, WorldHostSettings hostSettings, PresentPacingControl pacing) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.host",
            description: "Reads the host section three ways (Immediate): the DOCUMENT row (the authored host defaults, absence coalesced to the built-in default), the RESOLVED boot values (the document overlaid by the CLI window/backend flags), and the LIVE lever values (world.target's present Hz + world.timing's armed state) — so an author sees which fields the CLI overrode and which levers have drifted.",
            handler: (_, _) => new CommandResult(Output: DescribeHost())
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.host.set",
            description: "Replaces the whole host-section defaults row from one inline-JSON WorldHostDefaults {backend, width, height, surfaceFormat, fullscreen, presentMode, targetHertz, exitAfterSeconds, rayQuery, timing, genlock}: world.host.set <json>. DOCUMENT-defaults class — the boot-only fields apply at the next boot; targetHertz/timing set the value the next boot wakes on (world.save folds the live levers back). A full-document revalidation rejects loudly.",
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (string.IsNullOrWhiteSpace(value: raw)) {
                    return Usage(verb: "world.host.set", form: "<json>");
                }

                try {
                    if (JsonSerializer.Deserialize(json: raw, jsonTypeInfo: WorldJsonContext.Default.WorldHostDefaults) is not { } host) {
                        return new CommandResult(Output: "[world.host.set: the JSON parsed to null]") { IsError = true };
                    }

                    return Submit(host: host);
                } catch (JsonException exception) {
                    return new CommandResult(Output: $"[world.host.set: {exception.Message.ReplaceLineEndings(replacementText: " ")}]") { IsError = true };
                }
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.host.tune",
            description: "Console sugar: read-modify-write ONE host field into a whole-row upsert: world.host.tune <field> <value>. Fields (camelCase JSON names): backend (auto|directx|vulkan), width/height (1..16384), surfaceFormat (r8g8b8a8|b8g8r8a8), fullscreen/rayQuery/timing (true|false), presentMode (vsync|mailbox|immediate|adaptive), targetHertz (non-negative double; 0 = automatic display pacing), exitAfterSeconds (non-negative integer; 0 = run until closed), genlock (a non-whitespace string, or - to clear it to null). Host is a single always-resolving row, so there is no insert path.",
            handler: (_, args) => {
                if (args.Length != 2) {
                    return Usage(verb: "world.host.tune", form: "<field> <value>");
                }

                if (WithHostField(host: (server.Definition.Host ?? WorldHostDefaults.Default), field: args[0], value: args[1], error: out var error) is not { } tuned) {
                    return new CommandResult(Output: $"[world.host.tune: {error}]") { IsError = true };
                }

                return Submit(host: tuned);
            },
            routing: CommandRouting.Simulation
        );
    }

    // The three-way read-back: DOCUMENT (the coalesced authored row), RESOLVED (the boot values after CLI override), and
    // LIVE (the two session levers). One line, pipe-separated, so it stays greppable over stdout.
    private string DescribeHost() {
        var document = (server.Definition.Host ?? WorldHostDefaults.Default);
        var targetRate = (pacing.TargetHertz > 0.0 ? pacing.TargetHertz.ToString(provider: CultureInfo.InvariantCulture) : "display");

        return $"[world.host: document {{{DescribeRow(host: document)}}} " +
            $"resolved {{backend={WorldHostTokens.BackendToken(backend: (hostSettings.HostsOnDirectX ? WorldBackendPreference.DirectX : WorldBackendPreference.Vulkan))} " +
            $"width={hostSettings.Width} height={hostSettings.Height} surfaceFormat={WorldHostTokens.SurfaceFormatToken(format: hostSettings.SurfaceFormat)} " +
            $"fullscreen={Bool(value: hostSettings.Fullscreen)} presentMode={PresentModeToken(mode: hostSettings.PresentMode)} " +
            $"targetHertz={HertzToken(hertz: hostSettings.TargetHertz)} exitAfterSeconds={hostSettings.ExitAfterSeconds} " +
            $"rayQuery={Bool(value: hostSettings.RayQuery)} timing={Bool(value: hostSettings.Timing)} genlock={Genlock(value: hostSettings.Genlock)}}} " +
            $"live {{targetHertz={targetRate} timing={(GpuTimingControl.Shared.Armed ? "on" : "off")}}}]";
    }

    private static string DescribeRow(WorldHostDefaults host) =>
        $"backend={WorldHostTokens.BackendToken(backend: host.Backend)} width={host.Width} height={host.Height} " +
        $"surfaceFormat={WorldHostTokens.SurfaceFormatToken(format: host.SurfaceFormat)} fullscreen={Bool(value: host.Fullscreen)} " +
        $"presentMode={PresentModeToken(mode: host.PresentMode)} targetHertz={HertzToken(hertz: host.TargetHertz)} " +
        $"exitAfterSeconds={host.ExitAfterSeconds} rayQuery={Bool(value: host.RayQuery)} timing={Bool(value: host.Timing)} genlock={Genlock(value: host.Genlock)}";

    // RMW ONE host field (camelCase JSON names, per the P3 rule so the verb and the document never disagree), or null
    // with a reason when the field name is unknown (echoing all eleven) or the value is malformed for that field.
    private static WorldHostDefaults? WithHostField(WorldHostDefaults host, string field, string value, out string error) {
        error = string.Empty;

        switch (field) {
            case "backend":
                if (WorldHostTokens.ParseBackend(token: value) is not { } backend) {
                    error = $"bad backend '{value}' — {WorldHostTokens.BackendAuto}|{WorldHostTokens.BackendDirectX}|{WorldHostTokens.BackendVulkan}";

                    return null;
                }

                return (host with { Backend = backend });
            case "width":
                return TryDimension(value: value, name: "width", apply: dimension => (host with { Width = dimension }), error: out error);
            case "height":
                return TryDimension(value: value, name: "height", apply: dimension => (host with { Height = dimension }), error: out error);
            case "surfaceFormat":
                if (WorldHostTokens.ParseSurfaceFormat(token: value) is not { } surfaceFormat) {
                    error = $"bad surfaceFormat '{value}' — {WorldHostTokens.SurfaceFormatRgba}|{WorldHostTokens.SurfaceFormatBgra}";

                    return null;
                }

                return (host with { SurfaceFormat = surfaceFormat });
            case "fullscreen":
                return TryBool(value: value, name: "fullscreen", apply: flag => (host with { Fullscreen = flag }), error: out error);
            case "rayQuery":
                return TryBool(value: value, name: "rayQuery", apply: flag => (host with { RayQuery = flag }), error: out error);
            case "timing":
                return TryBool(value: value, name: "timing", apply: flag => (host with { Timing = flag }), error: out error);
            case "presentMode":
                // Explicit-token grammar only (like backend/surfaceFormat) — Enum.TryParse would passthrough-accept numeric
                // strings ("0" → Vsync), which the stated vsync|mailbox|immediate|adaptive vocabulary does not offer.
                PresentMode? mode = value.ToLowerInvariant() switch {
                    "vsync" => PresentMode.Vsync,
                    "mailbox" => PresentMode.Mailbox,
                    "immediate" => PresentMode.Immediate,
                    "adaptive" => PresentMode.Adaptive,
                    _ => null,
                };

                if (mode is not { } presentMode) {
                    error = $"bad presentMode '{value}' — vsync|mailbox|immediate|adaptive";

                    return null;
                }

                return (host with { PresentMode = presentMode });
            case "targetHertz":
                if (!double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var hertz) || !double.IsFinite(d: hertz) || (hertz < 0.0)) {
                    error = $"bad targetHertz '{value}' — a non-negative finite number (0 = automatic display pacing)";

                    return null;
                }

                return (host with { TargetHertz = hertz });
            case "exitAfterSeconds":
                if (!int.TryParse(s: value, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var exit) || (exit < 0)) {
                    error = $"bad exitAfterSeconds '{value}' — a non-negative integer (0 = run until closed)";

                    return null;
                }

                return (host with { ExitAfterSeconds = exit });
            case "genlock":
                // The P3 clear token: '-' clears the only nullable field to null; anything else sets it (shape-validated).
                return (host with { Genlock = ((value == "-") ? null : value) });
            default:
                error = $"unknown field '{field}' — backend|width|height|surfaceFormat|fullscreen|presentMode|targetHertz|exitAfterSeconds|rayQuery|timing|genlock";

                return null;
        }
    }

    private static WorldHostDefaults? TryDimension(string value, string name, Func<int, WorldHostDefaults> apply, out string error) {
        if (!int.TryParse(s: value, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var dimension) || (dimension < 1) || (dimension > 16384)) {
            error = $"bad {name} '{value}' — an integer in 1..16384";

            return null;
        }

        error = string.Empty;

        return apply(arg: dimension);
    }

    private static WorldHostDefaults? TryBool(string value, string name, Func<bool, WorldHostDefaults> apply, out string error) {
        bool? flag = value.ToLowerInvariant() switch {
            "true" => true,
            "false" => false,
            _ => null,
        };

        if (flag is not { } resolved) {
            error = $"bad {name} '{value}' — true|false";

            return null;
        }

        error = string.Empty;

        return apply(arg: resolved);
    }

    private CommandResult Submit(WorldHostDefaults host) {
        link.SubmitWorldMutation(mutation: new WorldMutation.SetHostDefaults(Principal: WorldPrincipal.Console, Host: host));

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) => new(Output: $"[{verb}: expected {form}]") {
        IsError = true,
    };

    private static string Bool(bool value) => (value ? "true" : "false");

    private static string HertzToken(double hertz) => hertz.ToString(provider: CultureInfo.InvariantCulture);

    private static string Genlock(string? value) => (value ?? "(none)");

    private static string PresentModeToken(PresentMode mode) => mode.ToString().ToLowerInvariant();

    // The raw argument text after the verb token — reconstructed from the submitted line so inline-JSON quotes survive
    // the console tokenizer (the WorldMutationCommandModule.Row idiom).
    private static string RawArgument(CommandContext context, string[] args) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();
            var separator = span.IndexOfAny(value0: ' ', value1: '\t');

            return ((separator < 0) ? string.Empty : span[(separator + 1)..].Trim().ToString());
        }

        return string.Join(separator: ' ', values: args);
    }
}
