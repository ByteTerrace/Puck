namespace Puck.Shaders;

/// <summary>An include file contributing to a compile's cache key.</summary>
public readonly record struct ShaderSourceDependency(string Path, string ContentHash);

/// <summary>Where one include directive of a closure resolved (<see cref="ShaderSourceClosure.WithIncludes"/>).</summary>
/// <param name="Path">The include's full path.</param>
/// <param name="Directory">The include directory it resolved below, as a full path, or <see langword="null"/> when it lies
/// beside the file whose directive names it.</param>
public readonly record struct ShaderIncludeResolution(string Path, string? Directory);
