namespace Puck.Shaders;

/// <summary>An include file contributing to a compile's cache key.</summary>
public readonly record struct ShaderSourceDependency(string Path, string ContentHash);
