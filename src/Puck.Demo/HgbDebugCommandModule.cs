using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The <c>hgb.*</c> console verb family — the SM83 debug control plane for the overworld's bootable cabinets, the
/// forge-authorship and agent-scripting counterpart to the native AGB scene's <c>agb.*</c> suite. Every verb is
/// CABINET-SCOPED (index-first, the <c>boot</c>/<c>win</c>/<c>press</c>/<c>rewind</c> convention): the bricks live on the
/// overworld render node, which sits AT its analyzer coupling ceiling, so each verb is QUEUED through the
/// <see cref="IOverworldControlHost"/> seam (resolved lazily through the render node's blessed creator-frame-source
/// seam, exactly as <see cref="TimeTravelCommandModule"/> does) and applied by the node next frame, on the render thread
/// between step fan-outs — so single-stepping and inspection never race the parallel fleet threads. The command returns
/// an immediate "queued" line; the real (possibly multi-line) output echoes to stdout the next frame. Watchpoint HITS
/// echo asynchronously as frames run. Usage-string-on-bad-input, never throws.
/// </summary>
internal sealed class HgbDebugCommandModule(IRenderNode rootNode) : ICommandModule {
    private readonly ICreatorModeHost? m_creatorHost = (rootNode as ICreatorModeHost);

    // The live overworld control host, resolved lazily through the node's blessed seam (null until the node's first
    // ProduceFrame builds the frame source, and for a non-overworld root) — the TimeTravelCommandModule shape.
    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Verb(op: "peek", description: "Hex-dumps a cabinet's memory (side-effect-free, whole 0x0000-0xFFFF bus): hgb.peek <i> <0xADDR> [len] (len 1..256, default 16).");
        yield return Verb(op: "poke", description: "Writes bytes into a cabinet's RAM — a DEBUG mutation outside replay determinism (drops the rewind ring): hgb.poke <i> <0xADDR> <byte> [byte ...].");
        yield return Verb(op: "regs", description: "Dumps a cabinet's SM83 registers + IME/IE/IF + LCDC/STAT/LY + ROM/WRAM/VRAM bank state: hgb.regs <i>.");
        yield return Verb(op: "status", description: "Reports a cabinet's boot/present model, paused state, frame/cycle counters, and framebuffer hash: hgb.status <i>.");
        yield return Verb(op: "pause", description: "Freezes a cabinet's stepping (hgb.step/frame/until then advance it explicitly): hgb.pause <i>.");
        yield return Verb(op: "resume", description: "Resumes a cabinet's stepping: hgb.resume <i>.");
        yield return Verb(op: "step", description: "Single-steps n SM83 instructions while paused (default 1): hgb.step <i> [n]. Echoes PC + flags.");
        yield return Verb(op: "frame", description: "Runs n whole video frames while paused (default 1): hgb.frame <i> [n]. Echoes frame/PC + framebuffer hash.");
        yield return Verb(op: "until", description: "Runs (forward, capped) until PC == <0xPC> while paused: hgb.until <i> <0xPC>. Echoes hit-or-cap + PC.");
        yield return Verb(op: "snap", description: "Captures a cabinet's whole-machine savestate into its one manual slot: hgb.snap <i>.");
        yield return Verb(op: "restore", description: "Restores a cabinet from its manual slot (invalidates the rewind ring): hgb.restore <i>.");
        yield return Verb(op: "watch", description: "Arms a read/write watchpoint on a cabinet's bus (zero cost when none armed): hgb.watch <i> <0xADDR> [r|w|rw]. A hit reports PC + access + value and pauses the cabinet.");
        yield return Verb(op: "watch.clear", description: "Clears a cabinet's watchpoints (back to zero hot-path cost): hgb.watch.clear <i>.");
        yield return Verb(op: "watch.list", description: "Lists a cabinet's armed watchpoints: hgb.watch.list <i>.");
        yield return Verb(op: "dis", description: "Disassembles SM83 instructions: hgb.dis <i> [0xADDR] [n] (addr default PC, n 1..64 default 8).");
        yield return Verb(op: "tilt", description: "Sets a debug tilt/accelerometer override on a cabinet's MBC7 cart, flowing through the same per-segment fold a bound pad would: hgb.tilt <i> <x> <y> (each -1..1) or hgb.tilt <i> off to resume pad-derived tilt.");
    }

    // The one routing rule (the TimeTravelCommandModule shape): a leading integer token names the cabinet; everything
    // after it is the operation's arguments, forwarded to the queued IOverworldControlHost seam.
    private string Route(string[] args, string op) {
        if ((args.Length >= 1) && TryParseInt(text: args[0], value: out var index) && (index >= 0)) {
            return ((Host is { } host)
                ? host.Debug(index: index, op: op, args: args[1..])
                : "[hgb: unavailable — the overworld is not the active root]");
        }

        return $"[hgb.{op}: usage — hgb.{op} <cabinet> ... (a leading console index)]";
    }

    private CommandDefinition Verb(string op, string description) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => new CommandResult(Route(args: (context.Parse?.GetValue(argument: rest) ?? []), op: op)),
            Name: $"hgb.{op}",
            TextCommand: new Command(description: description, name: $"hgb.{op}") {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
