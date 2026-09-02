using Microsoft.Agents.AI;

namespace Puck.World.Agents.Harness;

/// <summary>Options for the constrained Microsoft Agent Framework Harness composition used by a Puck world agent.</summary>
public sealed class WorldAgentHarnessOptions {
    /// <summary>Gets or initializes the stable agent id.</summary>
    public string? Id { get; init; }

    /// <summary>Gets or initializes the display name.</summary>
    public string Name { get; init; } = "puck-world-agent";

    /// <summary>Gets or initializes instructions specific to this participant's role or objective.</summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets or initializes an explicit source of user-defined skills. A null source disables skill discovery rather
    /// than allowing Harness to scan the process working directory implicitly.
    /// </summary>
    public AgentSkillsSource? SkillsSource { get; init; }

    /// <summary>Gets or initializes whether Harness should expose its persistent todo provider.</summary>
    public bool EnablePlanning { get; init; } = true;

    /// <summary>Gets or initializes whether mutating Puck tools emit Harness approval requests before invocation.
    /// Puck grants remain authoritative even when this consent layer is disabled.</summary>
    public bool RequireActionApproval { get; init; } = true;

    /// <summary>Gets or initializes whether Harness emits OpenTelemetry agent and chat-client activities.</summary>
    public bool EnableOpenTelemetry { get; init; } = true;

    /// <summary>Gets or initializes the maximum model/tool iterations in one request.</summary>
    public int MaximumIterationsPerRequest { get; init; } = 12;

}
