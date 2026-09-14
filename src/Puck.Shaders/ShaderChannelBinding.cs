namespace Puck.Shaders;

/// <summary>Maps a Shadertoy sampler name to a descriptor binding.</summary>
public readonly record struct ShaderChannelBinding(string Name, uint Binding, uint Count = 1);
