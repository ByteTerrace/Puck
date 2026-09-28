using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Where a counting package pass (<see cref="RenderGraphPackage.CountsKernelWork"/>) hands its shaders their row of the
/// node's kernel counters each frame: the pass-block offset of <see cref="ShaderWorkCounters.Row"/> and the pass-group
/// binding of <see cref="ShaderWorkCounters.Buffer"/>, resolved once when the recorder is created. Every counting package's
/// recorder writes its row through one of these, so the row and the buffer reach every counting shader alike.
/// </summary>
public sealed class RenderGraphPackageWorkCounters {
    private readonly IGpuBindings m_bindings;
    private readonly uint m_binding;
    private readonly string m_pass;
    private readonly int m_rowOffset;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphPackageWorkCounters"/> class for one package
    /// pass.</summary>
    /// <param name="context">The pass, whose <see cref="RenderGraphPackageRecorderContext.Parameters"/> lay out its pass
    /// block.</param>
    /// <param name="sets">The pass's sets, whose pass group binds the counter buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="sets"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The pass's interface does not declare the work counters.</exception>
    public RenderGraphPackageWorkCounters(RenderGraphPackageRecorderContext context, RenderGraphPackageSets sets) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: sets);

        m_bindings = context.Services.Bindings;
        m_binding = sets.BindingOf(member: ShaderWorkCounters.Buffer);
        m_pass = context.Pass;
        m_rowOffset = ((int)context.Parameters.BlockOffsetOf(member: ShaderWorkCounters.Row));
    }

    /// <summary>Writes a frame's row into the recording's pass block and binds the frame slot's counter buffer in the
    /// slot's pass set.</summary>
    /// <param name="recording">The frame's recording, whose <see cref="RenderGraphPackageRecording.WorkCounters"/> names
    /// the row.</param>
    /// <param name="passSet">The slot's pass set (<see cref="RenderGraphPackageSets.PassSet"/>).</param>
    /// <exception cref="InvalidOperationException">The recording carries no work counters.</exception>
    public void Write(in RenderGraphPackageRecording recording, nint passSet) {
        var counters = (recording.WorkCounters ?? throw new InvalidOperationException(message: $"Pass '{m_pass}' counts the work its shaders do, but its recording carries no work counters."));

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: recording.PassBlock[m_rowOffset..],
            value: counters.Row
        );
        m_bindings.WriteBuffer(
            binding: m_binding,
            bufferHandle: counters.Buffer.BufferHandle,
            bufferSize: counters.Buffer.SizeBytes,
            descriptorSetHandle: passSet,
            elementStride: sizeof(uint),
            kind: GpuBindingKind.ReadWriteBuffer
        );
    }
}
