namespace Puck.Platform.Windows;

/// <summary>One adapter output of an <see cref="IDisplayAdapterOutputs"/> enumeration.</summary>
public interface IDisplayAdapterOutput : IDisposable {
    /// <summary>Reads the color space the output presents in (<c>IDXGIOutput6::GetDesc1</c>).</summary>
    /// <returns>The color space, or null for one Puck does not name or a read that fails.</returns>
    DisplayColorSpace? ReadColorSpace();
}
