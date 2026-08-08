using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo.AgbDebug;

/// <summary>
/// The <c>agb.*</c> console verb family — the control plane for the native AGB (ARM7TDMI) debug scene.
/// <c>agb.debug</c> toggles the fullscreen single-game scene (routed THROUGH the render node so it wins the creating
/// slot's takeover, mutually exclusive with creator/world-sculpt/tracker/sdf-debug); every other verb drives the
/// <see cref="AgbDebugService"/> directly. The service is reached by DI injection rather than through the render node's
/// host interface — the render node and the overworld frame source both sit at their exact analyzer coupling ceilings
/// (CA1506), so the AGB machine state cannot be composed onto either (the sanctioned <see cref="IServiceProvider"/> /
/// Tracker escape; see <see cref="ICommandModule"/>'s remarks). Usage-string-on-bad-input, never throws.
/// </summary>
internal sealed class AgbDebugCommandModule(AgbDebugService service, IRenderNode rootNode) : ICommandModule {
    private readonly ICreatorModeHost? m_host = (rootNode as ICreatorModeHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Toggles the fullscreen native AGB (ARM7TDMI) debug scene: agb.debug [rom-path]. ROM resolution: explicit path -> the pending native/--rom cartridge -> a built-in generated ARM micro-ROM. agb.debug again leaves.",
            handler: (_, args) => new CommandResult(((m_host is { } host)
                ? host.ToggleAgbDebugMode(romPath: ((args.Length > 0) ? args[0] : null))
                : "[agb.debug: unavailable — the overworld is not the active root]")),
            name: "agb.debug"
        );
        yield return WithArgs(
            description: "Overrides the BIOS image used on the NEXT boot: agb.bios <path-to-16KiB-bios>. Wins over PUCK_AGB_BIOS; the default is the embedded replacement (zeroed) stub.",
            handler: (_, args) => new CommandResult(service.SetBios(path: ((args.Length > 0) ? args[0] : ""))),
            name: "agb.bios"
        );
        yield return Plain(
            description: "Freezes the AGB host cadence (the machine stops advancing until agb.resume / agb.step / agb.frame).",
            handler: _ => new CommandResult(service.Pause()),
            name: "agb.pause"
        );
        yield return Plain(
            description: "Resumes the AGB host cadence (one whole frame per produced frame).",
            handler: _ => new CommandResult(service.Resume()),
            name: "agb.resume"
        );
        yield return WithArgs(
            description: "Single-steps n instructions while paused (default 1): agb.step [n]. Echoes PC/CPSR.",
            handler: (_, args) => new CommandResult(service.Step(count: ParseCount(args: args))),
            name: "agb.step"
        );
        yield return WithArgs(
            description: "Runs n whole frames while paused (default 1): agb.frame [n]. Echoes the frame count + framebuffer hash.",
            handler: (_, args) => new CommandResult(service.Frame(count: ParseCount(args: args))),
            name: "agb.frame"
        );
        yield return WithArgs(
            description: "Runs instructions until R15 == <hex-pc> (FORWARD only, capped at 50M): agb.until <hex-pc>. Echoes hit-or-cap + final PC.",
            handler: (_, args) => new CommandResult(service.Until(targetHex: ((args.Length > 0) ? args[0] : ""))),
            name: "agb.until"
        );
        yield return WithArgs(
            description: "Traces the next n instructions' fetched opcode + PC + ARM/THUMB + CPSR + register deltas (default 1, max 1000): agb.trace [n].",
            handler: (_, args) => new CommandResult(service.Trace(count: ParseCount(args: args))),
            name: "agb.trace"
        );
        yield return Plain(
            description: "Dumps r0-r15 + CPSR (with the decoded NZCV/ARM-THUMB/mode) + the banked SPSR when the current mode has one.",
            handler: _ => new CommandResult(service.Regs()),
            name: "agb.regs"
        );
        yield return WithArgs(
            description: "Dumps one I/O register halfword (a 0x-prefixed offset) or the whole 0x000-0x3FE block: agb.io [offset].",
            handler: (_, args) => new CommandResult(service.Io(offsetArg: ((args.Length > 0) ? args[0] : null))),
            name: "agb.io"
        );
        yield return Plain(
            description: "Reports the booted ROM, BIOS kind, paused state, frame count, master cycles, and framebuffer hash.",
            handler: _ => new CommandResult(service.Status()),
            name: "agb.status"
        );
        yield return WithArgs(
            description: "Hex-dumps memory (side-effect-free) over the whole 32-bit bus: agb.peek <0xADDR> [len] (len 1..256, default 16).",
            handler: (_, args) => new CommandResult(service.Peek(addressArg: ((args.Length > 0) ? args[0] : ""), lengthArg: ((args.Length > 1) ? args[1] : null))),
            name: "agb.peek"
        );
        yield return WithArgs(
            description: "Writes bytes into memory — a DEBUG mutation outside replay determinism (drops the rewind ring): agb.poke <0xADDR> <byte> [byte ...].",
            handler: (_, args) => new CommandResult((args.Length > 0) ? service.Poke(addressArg: args[0], byteArgs: args[1..]) : service.Poke(addressArg: "", byteArgs: [])),
            name: "agb.poke"
        );
        yield return WithArgs(
            description: "Disassembles instructions (ARM/THUMB per CPSR): agb.dis [0xADDR] [n] (addr default R15, n 1..64 default 8).",
            handler: (_, args) => new CommandResult(service.Dis(addressArg: ((args.Length > 0) ? args[0] : null), countArg: ((args.Length > 1) ? args[1] : null))),
            name: "agb.dis"
        );
        yield return WithArgs(
            description: "Captures the whole-machine savestate into an in-memory slot (default 0): agb.snap [slot]. Echoes frame/PC/cycle/framebuffer-hash + image size.",
            handler: (_, args) => new CommandResult(service.Snap(slot: ParseSlot(args: args))),
            name: "agb.snap"
        );
        yield return WithArgs(
            description: "Restores the whole-machine savestate from an in-memory slot (default 0): agb.restore [slot]. Rewinds the machine with no reboot; echoes frame/PC/cycle/framebuffer-hash.",
            handler: (_, args) => new CommandResult(service.Restore(slot: ParseSlot(args: args))),
            name: "agb.restore"
        );
        yield return WithArgs(
            description: "Sets the booted cartridge's recorded solar-sensor light level, 0 (darkest) to 255 (brightest): agb.light <0-255>. Recorded per-frame host input, held until the next call; a no-op on a cartridge with no solar sensor.",
            handler: (_, args) => new CommandResult(service.SetLightLevel(levelArg: ((args.Length > 0) ? args[0] : ""))),
            name: "agb.light"
        );
        // The machine-neutral rewind/rewind.status/runahead/fastforward family lives in TimeTravelCommandModule — ONE
        // verb surface routing between the overworld cabinets (index-first) and this debug scene (index-less).
    }

    // n from an optional first arg (default 1); a non-numeric token falls back to 1 (the service clamps the minimum).
    private static int ParseCount(string[] args) =>
        (((args.Length > 0) && TryParseInt(text: args[0], value: out var n)) ? n : 1);

    // slot from an optional first arg (default 0); a non-numeric token falls back to 0 (the service range-checks it).
    private static int ParseSlot(string[] args) =>
        (((args.Length > 0) && TryParseInt(text: args[0], value: out var slot)) ? slot : 0);
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
