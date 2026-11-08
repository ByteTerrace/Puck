using Puck.Commands;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The SDF kernel reload surface, <c>world.shaders.reload</c> and <c>world.shaders.status</c>: every presentation shape
/// that renders an SDF residency, windowed or offscreen, registers it, since reloading kernels needs a renderer and no
/// window. A reload compiles the kernel sources its tree carries with the World's toolchain into the per-user shader
/// cache the build compiles kernels into (<see cref="ShaderCompiler.DefaultCacheDirectory"/>), so the build after an
/// edit publishes what the reload compiled, and a reload of an unedited source is a hit on what the build compiled.
/// </summary>
/// <param name="renderProbe">The render probe whose residency the verbs reload and report.</param>
/// <param name="compiler">The World's shader compiler, whose toolchain a carried kernel source compiles with.</param>
internal sealed class WorldShaderReloadCommandModule(WorldRenderProbe renderProbe, ShaderCompiler compiler) : ICommandModule {
    private static readonly CommandForms WorldShadersReloadForms = CommandForms.Of(verb: "world.shaders.reload", "[tree]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.tier",
            description: "Selects which tier shades authored programs, for comparing them: [automatic|interpreted]. automatic records with the published specialized kernels wherever they hold every active program; interpreted records with the interpreter. Changing it re-renders the shading passes; specialization continues in either mode, and world.shaders.status reports each program's state.",
            handler: (_, args) => {
                if (renderProbe.Residency is not { } node) { return new CommandResult(Output: "[world.shaders.tier: renderer not ready]"); }
                if ((args.Count > 1) || ((args.Count == 1) && !args.Is(index: 0, value: "automatic") && !args.Is(index: 0, value: "interpreted"))) {
                    return CommandResult.Error(output: "[world.shaders.tier: automatic|interpreted]");
                }
                if (args.Count == 1) { node.InterpretShade = args.Is(index: 0, value: "interpreted"); }
                return new CommandResult(Output: $"[world.shaders.tier: {(node.InterpretShade ? "interpreted" : "automatic")}]");
            });
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.reload",
            description: $"Reloads SDF kernels on the next produced frame: {WorldShadersReloadForms.Spelling}. Reads the kernels the tree's passes directory carries and keeps the rest: a kernel's sdf-*.comp.hlsl source compiles to the running backend alone, through the per-user shader cache the build shares (an unchanged source is a cache hit), and a kernel carried only as bytecode loads as it stands. Defaults to the deployed Assets/Shaders/Sdf, which carries the build's bytecode and the sources it compiled (an unedited build's sources are cache hits); a source checkout names src/Puck.SdfVm/Assets/Shaders/Sdf, so editing a kernel and reloading is one step. Uses the current backend. Kernels install as they compile, quickest first, so a light kernel's change shows while a heavy kernel still compiles, and each installed batch prints '[shaders.reload: request=N installed pipelines=K]'. Replaces changed pipelines while retaining world state, GPU buffers and textures; a compile error (reported with its file and line), a failed load or pipeline build, or kernels that do not read this host's interface (compiled against another SDF instruction set, or binding what the host does not place there), keeps the kernels installed before it; DXIL kernels are checked through the dxcompiler beside dxc. The request completes off the frame thread and reports its outcome on stderr ('[shaders.reload: request=N applied|unchanged|failed ...]'); a text session's later lines wait for it, a session that settles its results (an operator attachment's call) answers with the outcome in world.shaders.status's form, and world.shaders.status reports it too. Binding/ABI changes require a host rebuild; child engines and overlay/postprocess shaders are outside this command.",
            forms: WorldShadersReloadForms,
            handler: (context, args) => {
                if (renderProbe.Residency is not { } node) {
                    return CommandResult.Error(output: "[world.shaders.reload: renderer not ready]");
                }
                try {
                    if (!node.RequestShaderReload(compiler: compiler, tree: ((args.Count == 0)
                        ? null
                        : args.Tail(start: 0)))) {
                        return CommandResult.Error(output: "[world.shaders.reload: another request is pending — world.shaders.status]");
                    }
                    var status = node.ShaderReloadStatus;
                    var request = status.RequestId;

                    // The issuing text session's later lines wait for the outcome, so a script reads what the reload did
                    // rather than racing the compile and pipeline build; the reload always settles, failing on a device
                    // loss or a released residency.
                    context.TextSession?.HoldWhile(
                        hold: () => ((node.ShaderReloadStatus is { State: "pending" } pending) && (pending.RequestId == request)),
                        released: deadlinePassed => (deadlinePassed
                            ? null
                            : Describe(status: node.ShaderReloadStatus, verb: "world.shaders.reload") with { IsError = (node.ShaderReloadStatus.State == "failed") })
                    );

                    return new CommandResult(Output: $"[world.shaders.reload: request={status.RequestId} pending directory={status.Directory}]");
                } catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException)) {
                    return CommandResult.Error(output: $"[world.shaders.reload: {exception.Message}]");
                }
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.status",
            description: "Reports the most recent compiled SDF shader reload: request number, state (idle/pending/applied/unchanged/failed), generation, changed pipeline count, directory and failure reason. A request is complete only after pending changes to an outcome. Then one '[world.shaders.program: ...]' line for each authored program the residency shades or compiles: its identity, author name and state (specialized, compiling or interpreted, with the failure that left it interpreted), after a '[world.shaders.tier: ...]' line.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: "[world.shaders.status: no arguments]");
                }
                if (renderProbe.Residency is not { } node) {
                    return new CommandResult(Output: "[world.shaders.status: renderer not ready]");
                }
                var reload = Describe(
                    status: node.ShaderReloadStatus,
                    verb: "world.shaders.status"
                );
                var programs = node.ShadeStatus;
                var lines = programs.Select(selector: static p =>
                    $"[world.shaders.program: identity={p.Identity} name={p.Name} state={p.State}{((p.Error is { } error) ? $" error={error}" : string.Empty)}]");

                return reload with {
                    Output = string.Join(separator: '\n', values: lines.Prepend(element: $"[world.shaders.tier: {(node.InterpretShade ? "interpreted" : "automatic")} programs={programs.Count} specialized={programs.Count(static p => p.State == "specialized")} compiling={programs.Count(static p => p.State == "compiling")} interpreted={programs.Count(static p => p.State == "interpreted")}]").Prepend(element: reload.Output)),
                };
            }
        );
    }

    // The status line world.shaders.status answers, and a settling session's world.shaders.reload answers as its hold ends.
    private static CommandResult Describe(SdfShaderReloadStatus status, string verb) => new(Output: $"[{verb}: request={status.RequestId} state={status.State} generation={status.Generation} pipelines={status.ChangedPipelines} directory={(status.Directory ?? "default")}{((status.Error is { } error)
        ? $" error={error}"
        : "")}]");
}
