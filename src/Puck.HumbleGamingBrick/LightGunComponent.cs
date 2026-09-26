using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The default <see cref="ILightGun"/>: aimed off the screen until a host records an aim. It reads the framebuffer the
/// PPU draws in place, one dot at a time, so a pixel the beam has not yet reached this frame still shows the previous
/// frame's color, the persistence an LCD has. Brightness is the pixel's Rec. 601 luma in integer weights. The held aim
/// is snapshot state, like <see cref="TiltSensorComponent"/>'s held reading.
/// </summary>
public sealed class LightGunComponent : ILightGun, ISnapshotable {
    // Rec. 601 luma weights scaled to sum to 256, and the half-bright threshold on the weighted sum of 8-bit channels.
    private const int BlueWeight = 29;
    private const int GreenWeight = 150;
    private const int LitThreshold = (128 * 256);
    private const int RedWeight = 77;

    private readonly IFramebuffer m_framebuffer;

    private bool m_onScreen;
    private ushort m_x;
    private ushort m_y;

    /// <summary>Initializes a new instance of the <see cref="LightGunComponent"/> class.</summary>
    /// <param name="framebuffer">The machine's framebuffer, the LCD the gun is aimed at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="framebuffer"/> is <see langword="null"/>.</exception>
    public LightGunComponent(IFramebuffer framebuffer) {
        ArgumentNullException.ThrowIfNull(argument: framebuffer);

        m_framebuffer = framebuffer;
    }

    /// <inheritdoc/>
    public bool SensesLight {
        get {
            if (!m_onScreen) {
                return false;
            }

            var pointer = new MachinePointer(
                x: m_x,
                y: m_y
            );
            var width = m_framebuffer.Width;
            var pixel = m_framebuffer.Pixels[((pointer.Row(height: m_framebuffer.Height) * width) + pointer.Column(width: width))];
            var luma = (
                ((((int)((pixel >> 16) & 0xFFu)) * RedWeight) +
                (((int)((pixel >> 8) & 0xFFu)) * GreenWeight)) +
                (((int)(pixel & 0xFFu)) * BlueWeight)
            );

            return (luma >= LitThreshold);
        }
    }

    /// <inheritdoc/>
    public void Aim(MachinePointer pointer) {
        m_onScreen = pointer.OnScreen;
        m_x = pointer.X;
        m_y = pointer.Y;
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Boolean(value: ref m_onScreen);
        transfer.UInt16(value: ref m_x);
        transfer.UInt16(value: ref m_y);
    }
}
