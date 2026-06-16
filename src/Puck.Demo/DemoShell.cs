using System.Diagnostics;
using Puck.Commands;
using Puck.Demo.Input;
using Puck.Demo.Scene;
using Puck.Platform;

namespace Puck.Demo;

/// <summary>The per-frame orchestrator. Input routing is the command registry's job, pumped by the shared
/// <see cref="CommandShell"/> with the demo's input adapter; this shell adds the one demo-specific concern
/// the shared pump stays out of — advancing the scene clock by the elapsed wall-clock time after each
/// collect. Commands (<c>layout</c>, <c>scene</c>, <c>pause</c>, layout cycling) mutate the scene through
/// their handlers, so both faces drive one model.</summary>
internal sealed class DemoShell {
    private readonly DemoScene m_scene;
    private readonly CommandShell m_shell;
    private long m_lastTimestamp = Stopwatch.GetTimestamp();

    public DemoShell(
        CommandRegistry registry,
        BindingCommandSource keyboardSource,
        TextCommandSource standardInputSource,
        DemoScene scene
    ) {
        ArgumentNullException.ThrowIfNull(scene);

        m_scene = scene;
        m_shell = new CommandShell(
            inputAdapter: DemoInputMap.ToInputSignal,
            keyboardSource: keyboardSource,
            registry: registry,
            standardInputSource: standardInputSource
        );
    }

    /// <summary>Clears the previous frame's transient command values. Call before enqueuing input.</summary>
    public void BeginFrame() {
        m_shell.BeginFrame();
    }
    /// <summary>Adapts a raw platform packet to an input signal and enqueues it on the keyboard source.</summary>
    public void Enqueue(InputPacket packet) {
        m_shell.Enqueue(packet: packet);
    }
    /// <summary>Collects this frame's commands, then advances the scene by the elapsed wall-clock time.</summary>
    public void Update() {
        m_shell.Collect();

        var now = Stopwatch.GetTimestamp();
        var deltaSeconds = (float)((now - m_lastTimestamp) / (double)Stopwatch.Frequency);

        m_lastTimestamp = now;
        m_scene.Advance(deltaSeconds: deltaSeconds);
    }
}
