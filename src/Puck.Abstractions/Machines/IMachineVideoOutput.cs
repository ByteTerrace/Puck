using System.Numerics;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Machines;

/// <summary>A presentation output that any number of screens may sample. Publishing never advances simulation.</summary>
public interface IMachineVideoOutput {
    /// <summary>Gets the shader-readable image view, or zero before publication or after device loss.</summary>
    nint NativeImageViewHandle { get; }
    /// <summary>Gets the frame's average emitted color, normalized to 0..1.</summary>
    Vector3 EmittedLight { get; }
    /// <summary>Publishes the most recent complete frame on the selected GPU. Unchanged frames need no upload.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="gpu">Backend-neutral upload services.</param>
    void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu);
    /// <summary>Retires GPU resources without changing the machine's simulation state.</summary>
    void NotifyDeviceLost();
}

/// <summary>Optional named video outputs. Names and output identities remain stable for the runtime's lifetime.</summary>
public interface IMachineVideoOutputs {
    /// <summary>Gets the available outputs by provider-owned name.</summary>
    IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; }
}
