using Puck.Abstractions.Sources;

namespace Puck.World;

/// <summary>The source instances a scheduled capture reads by screen, and the images the deterministic ones state they
/// show, which the capture scheduler holds a capture of a source instance to through <see cref="ImageSourceVerdict"/>.
/// Every member runs on the host pump.</summary>
public interface IWorldCaptureSources {
    /// <summary>Returns the name of the source instance a screen reads.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen reads none.</returns>
    string? InstanceOf(int screen);
    /// <summary>Returns the reference of the source a render-graph instance renders: the image a deterministic source
    /// states it shows.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The reference, or <see langword="null"/> when the instance is no source that states its image.</returns>
    IImageSourceReference? ReferenceOf(string instance);
}
