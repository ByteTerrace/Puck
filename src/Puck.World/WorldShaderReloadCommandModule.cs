using Puck.Commands;
using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The SDF kernel reload surface, <c>world.shaders.reload</c> and <c>world.shaders.status</c>: every presentation shape
/// that renders an SDF residency, windowed or offscreen, registers it, since reloading kernels needs a renderer and no
/// window. A reload compiles the kernel sources its tree carries with the World's <see cref="ShaderCompiler"/>.
/// </summary>
/// <param name="renderProbe">The render probe whose residency the verbs reload and report.</param>
/// <param name="compiler">The World's shader compiler, which a carried kernel source compiles with.</param>
internal sealed class WorldShaderReloadCommandModule(WorldRenderProbe renderProbe, ShaderCompiler compiler) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.reload",
            description: "Reloads SDF kernels on the next produced frame: world.shaders.reload [tree]. Reads the kernels the tree's passes directory carries and keeps the rest: a kernel's sdf-*.comp.hlsl source compiles with the world's shader compiler (an unchanged source is a cache hit), and a kernel carried only as bytecode loads as it stands. Defaults to the deployed Assets/Shaders/Sdf, which carries bytecode; a source checkout names src/Puck.SdfVm/Assets/Shaders/Sdf, so editing a kernel and reloading is one step. Uses the current backend. Replaces changed pipelines while retaining world state, GPU buffers and textures; a compile error (reported with its file and line), a failed load or pipeline build, or kernels that do not read this host's interface (compiled against another SDF instruction set, or binding what the host does not place there), keeps the previous set; DXIL kernels are checked through the dxcompiler beside dxc. The request completes off the frame thread and reports its outcome on stderr ('[shaders.reload: request=N applied|unchanged|failed ...]'); a text session's later lines wait for it, and world.shaders.status reports it too. Binding/ABI changes require a host rebuild; child engines and overlay/postprocess shaders are outside this command.",
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
                    context.TextSession?.HoldWhile(hold: () => ((node.ShaderReloadStatus is { State: "pending" } pending) && (pending.RequestId == request)));

                    return new CommandResult(Output: $"[world.shaders.reload: request={status.RequestId} pending directory={status.Directory}]");
                } catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException)) {
                    return CommandResult.Error(output: $"[world.shaders.reload: {exception.Message}]");
                }
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.status",
            description: "Reports the most recent compiled SDF shader reload: request number, state (idle/pending/applied/unchanged/failed), generation, changed pipeline count, directory and failure reason. A request is complete only after pending changes to an outcome.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: "[world.shaders.status: no arguments]");
                }
                if (renderProbe.Residency is not { } node) {
                    return new CommandResult(Output: "[world.shaders.status: renderer not ready]");
                }
                var status = node.ShaderReloadStatus;

                return new CommandResult(Output: $"[world.shaders.status: request={status.RequestId} state={status.State} generation={status.Generation} pipelines={status.ChangedPipelines} directory={(status.Directory ?? "default")}{((status.Error is { } error)
                    ? $" error={error}"
                    : "")}]");
            }
        );
    }
}
