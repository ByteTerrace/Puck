namespace Puck.Abstractions.Lighting;

/// <summary>
/// A platform's lamp-array transport: enumerates the present RGB lamp arrays and keeps the opened set current.
/// The Windows implementation (HID LampArray over the Virtual HID Framework) lives in <c>Puck.Platform</c>; a
/// platform with no lamp support exposes an empty <see cref="Devices"/>. The source owns the lifetime of the
/// devices it opens — disposing the source disposes them all.
/// </summary>
public interface ILampArrayDeviceSource : IDisposable {
    /// <summary>Gets the currently opened lamp arrays. Stable between <see cref="Rescan"/> calls.</summary>
    IReadOnlyList<ILampArrayDevice> Devices { get; }

    /// <summary>
    /// Re-enumerates the platform's lamp arrays, opening any newly-present device and disposing any that has gone
    /// away, then updates <see cref="Devices"/>. A device that is still present keeps its existing handle.
    /// </summary>
    void Rescan();
}
