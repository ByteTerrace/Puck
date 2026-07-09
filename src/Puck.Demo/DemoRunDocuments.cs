using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The synthesizer that turns the legacy CLI flags into the SAME <see cref="PuckRunDocument"/> the <c>--run</c> path
/// consumes. Every <c>--validate-overworld</c>/<c>--overworld</c> flag is a thin alias that builds a document, so there is
/// no second imperative path to keep in sync. The flags flow straight from <c>Program</c>'s parsed CLI values —
/// there is no separate legacy flag-bundle record parallel to this synthesis.
/// </summary>
internal static class DemoRunDocuments {
    /// <summary>Turns the resolved CLI flags into the run document the single data-driven path consumes: the
    /// self-contained <c>--validate-overworld</c> gate, the IMMERSED overworld's four StandingMachines all seated
    /// with the same cartridge for <c>--rom &lt;path&gt;</c>, or the OVERWORLD for <c>--overworld</c> or no flags at
    /// all (the demo IS the overworld).</summary>
    /// <param name="backend">The <c>--backend</c> the live render hosts on (<c>vulkan</c>/<c>directx</c>).</param>
    /// <param name="exitAfterSeconds">The <c>--exit-after-seconds</c> auto-exit duration.</param>
    /// <param name="presentMode">The <c>--present-mode</c> swapchain present mode.</param>
    /// <param name="surfaceFormat">The <c>--surface-format</c> back-buffer format.</param>
    /// <param name="validateOverworld">The <c>--validate-overworld</c> pure-CPU determinism + replay self-check for the
    /// action demo.</param>
    /// <param name="overworld">The <c>--overworld</c> live controller-driven action demo (Vulkan host).</param>
    /// <param name="romPath">The <c>--rom</c> cartridge path: boot straight into the game — the IMMERSED overworld,
    /// one machine per connecting player (null = not requested).</param>
    /// <param name="romExit">The <c>--rom-exit</c> fourth-wall condition spec (<c>"&lt;0xADDR&gt;&lt;op&gt;&lt;value&gt;"</c>,
    /// e.g. <c>"0xDA22&gt;=1"</c>); null = no instrumentation.</param>
    /// <returns>The synthesized run document (always valid).</returns>
    public static PuckRunDocument Synthesize(string backend, int exitAfterSeconds, string presentMode, string surfaceFormat, bool validateOverworld, bool overworld, string? romPath, string? romExit) {
        // The validation gate renders OFFSCREEN on a forced Vulkan host (host.backend:"directx" is rejected for it);
        // a live render hosts on the requested backend.
        var host = new HostDocument {
            Backend = (validateOverworld ? null : backend),
            ExitAfterSeconds = exitAfterSeconds,
            PresentMode = presentMode,
            SurfaceFormat = surfaceFormat,
        };

        if (validateOverworld) {
            return Gate(host: host, gate: "overworld");
        }

        // --rom: boot straight INTO the cartridge — the IMMERSED overworld. One stand per potential player (up to
        // MaxConsoles), all seating the same ROM: each connecting pad's player is auto-seated at (boots + takes
        // over) their own stand and sees only the game panes; when any machine's exit condition fires (--rom-exit,
        // e.g. "0xDA22>=1" for a representative cartridge's save flag) the fourth wall breaks and the room is revealed — all active
        // players standing at their machines, the games continuing on the diegetic screens. Without --rom-exit the
        // run is a plain immersed multi-machine boot (exit by closing the window / exitAfterSeconds).
        if (romPath is { } bootRomPath) {
            var exit = ((romExit is { } spec) ? ParseExitSpec(spec: spec) : null);

            return new PuckRunDocument {
                // World = null: the --rom boot loads no sculpted world (revealing into the bare default room), so the
                // synthesized document stays byte-unchanged — loading a world is a run-document (or world.load) choice.
                Graph = new OverworldNode { Consoles = StandingMachines(romPath: bootRomPath, exit: exit), Immersed = true, World = null },
                Host = (host with { Backend = "vulkan" }),
                Version = PuckRunDocument.CurrentVersion,
            };
        }

        // The OVERWORLD is the demo: the default with no flags at all, and the explicit --overworld alias. It opens IMMERSED
        // in the WORLD-LENS game — up to four players, each inside their own world-lens on the room: you walk the room
        // and your brick mirrors you, seeing the room through the lens (no room pane, just the game panes tiling).
        // First player to reach their goal breaks the fourth wall — the winner's screen fills the space and the ROOM is
        // revealed, all players in it, freed to walk away and act. Needs no showcase ROM on disk (the world-lens is a
        // built-in, pure-CPU cart); the walk-in-and-boot overworld lives on behind an explicit --run document. Vulkan host.
        return new PuckRunDocument {
            Graph = new OverworldNode {
                Consoles = WorldLensMachines(),
                Immersed = true,
                // World = null: the default demo loads no sculpted world (the bare room), so it is byte-unchanged.
                // Making the default reveal into a world is a later content choice, expressed in a run document.
                World = null,
            },
            Host = (host with { Backend = "vulkan" }),
            Version = PuckRunDocument.CurrentVersion,
        };
    }

    // The world-lens game's cabinets: four Advanced machines, each running the built-in world-lens cart (peripheral
    // "world" binds the room→machine sensor feed) with no pre-inserted file ROM, and each instrumented with the WIN
    // exit — the ROM raises the win flag (0xC004) the frame its player reaches the goal tile, and the host polls it to
    // break the fourth wall. All four identical: player i walks the room, cabinet i's lens mirrors player i.
    private static GamingBrickSource[] WorldLensMachines() {
        var win = new BrickExitCondition { Address = "0xC004", Label = "reached the goal", Op = ">=", Value = 1 };
        var machines = new GamingBrickSource[OverworldNode.MaxConsoles];

        for (var index = 0; (index < machines.Length); index++) {
            machines[index] = new GamingBrickSource { Exit = win, Model = "agb", Peripheral = "world" };
        }

        return machines;
    }

    // The overworld's standing machines: ALWAYS four (OverworldNode.MaxConsoles), each pre-inserted with romPath and booting
    // on the Advanced (agb) costume — the one world's cabinets, identical at start and DEMOTABLE to cgb/dmg live via the
    // mode-verb binds. BOTH boot directions build the same four machines; only the immersion (start inside the ROM vs
    // in the room) and the exit instrumentation differ, so a player who joins inherits whatever mode the world booted in.
    private static GamingBrickSource[] StandingMachines(string romPath, BrickExitCondition? exit) {
        // A .gba ROM boots the NATIVE ARM7TDMI cabinet (the fullscreen AGB scene) rather than the SM83 core in its agb
        // costume — the overworld auto-boots a native stand and the agb.* verbs debug it. The SM83-only exit
        // instrumentation does not apply to a native stand (it polls SM83 work RAM), so it is dropped there.
        var native = IsGbaRom(romPath: romPath);
        var machines = new GamingBrickSource[OverworldNode.MaxConsoles];

        for (var index = 0; (index < machines.Length); index++) {
            machines[index] = (native
                ? new GamingBrickSource { Model = "agb", Native = true, RomPath = romPath }
                : new GamingBrickSource { Exit = exit, Model = "agb", RomPath = romPath });
        }

        return machines;
    }

    // Whether a --rom path names a Game Boy Advance cartridge (the ARM7TDMI native path) rather than an SM83 GB/GBC
    // cartridge. Detected by extension — the reliable signal for a launch flag, and file-I/O-free at document synthesis
    // (document validation never touches the filesystem; the run path loads the bytes).
    private static bool IsGbaRom(string romPath) =>
        romPath.EndsWith(value: ".gba", comparisonType: StringComparison.OrdinalIgnoreCase);

    // Parses a --rom-exit spec ("<0xADDR><op><value>", e.g. "0xDA22>=1") into the document exit condition. A
    // malformed spec is reported LOUDLY and dropped (the boot proceeds without instrumentation) rather than
    // producing an inert half-parsed condition.
    private static BrickExitCondition? ParseExitSpec(string spec) {
        foreach (var op in BrickExitCondition.SupportedOps.OrderByDescending(keySelector: static candidate => candidate.Length)) {
            var split = spec.IndexOf(value: op, comparisonType: StringComparison.Ordinal);

            if (split <= 0) {
                continue;
            }

            var condition = new BrickExitCondition {
                Address = spec[..split].Trim(),
                Label = spec,
                Op = op,
                Value = (int.TryParse(s: spec[(split + op.Length)..].Trim(), result: out var value) ? value : -1),
            };

            if (condition.TryParseAddress(address: out _) && (condition.Value is >= 0 and <= 255)) {
                return condition;
            }

            break;
        }

        Console.Error.WriteLine(value: $"[rom] --rom-exit '{spec}' is not <0xADDR><op><value> (ops: {string.Join(separator: " ", values: BrickExitCondition.SupportedOps)}; address 0xC000-0xDFFF; value 0-255); booting without exit instrumentation.");

        return null;
    }

    private static PuckRunDocument Gate(HostDocument host, string gate) {
        return new PuckRunDocument {
            Host = host,
            Validation = new ValidationDocument { Gate = gate },
            Version = PuckRunDocument.CurrentVersion,
        };
    }
}
