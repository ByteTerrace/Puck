using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>
/// The machine outputs a presenter published on the current device, so that device's uploads are retired before it
/// goes. Machines outlive the render device, so the presenter that published an output is the owner that must release
/// the upload the output holds on it. An output is recorded before it publishes: a publish can create its upload and
/// then throw (a <see cref="DeviceLostException"/> from the submit), and an output recorded only after a successful
/// publish would keep that upload past the device it was made on. Single-threaded, like the presenter that owns it.
/// </summary>
public sealed class PublishedMachineOutputs {
    private readonly HashSet<(string Instance, string Output)> m_published = [];

    /// <summary>Gets how many outputs are recorded on the current device.</summary>
    public int Count => m_published.Count;

    /// <summary>Records an output, then publishes its latest frame on the device.</summary>
    /// <param name="instance">The machine instance's name.</param>
    /// <param name="output">The output's name within the instance.</param>
    /// <param name="machine">The output to publish.</param>
    /// <param name="deviceContext">The device the output uploads to.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void Publish(string instance, string output, IMachineVideoOutput machine, IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(argument: instance);
        ArgumentNullException.ThrowIfNull(argument: output);
        ArgumentNullException.ThrowIfNull(argument: machine);

        _ = m_published.Add(item: (instance, output));
        machine.PublishFrame(deviceContext: deviceContext);
    }
    /// <summary>Releases the upload every recorded output holds on the current device and forgets them all. An instance
    /// removed since it published resolves to <see langword="null"/>, because its host released the upload when the
    /// instance went.</summary>
    /// <param name="resolve">Resolves an (instance, output) pair to the live output, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolve"/> is <see langword="null"/>.</exception>
    public void Retire(Func<string, string, IMachineVideoOutput?> resolve) {
        ArgumentNullException.ThrowIfNull(argument: resolve);

        foreach (var (instance, output) in m_published) {
            resolve(
                arg1: instance,
                arg2: output
            )?.NotifyDeviceLost();
        }

        m_published.Clear();
    }
}
