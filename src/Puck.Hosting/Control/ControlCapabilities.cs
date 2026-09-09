namespace Puck.Hosting;

/// <summary>The host's disclosed command vocabulary and renderer capability. This grants no dispatch authority.</summary>
/// <param name="CommandHelp">Registered command names, descriptions and argument usage; empty when no commands are disclosed.</param>
/// <param name="SupportsCapture">Whether this host explicitly permits framebuffer capture.</param>
public sealed record ControlCapabilities(string CommandHelp, bool SupportsCapture = false);
