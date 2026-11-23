using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>
/// The trailing <c>instance:&lt;name&gt;</c> token grammar every instance-addressed console verb shares: the prefix, the
/// test for it and the name it carries. The bracket-splice surgery that writes the same tag onto an echo lives in
/// <see cref="CommandEcho.SpliceTag(string, string, string)"/>; resolving the name to a running instance is
/// <c>WorldInstanceArgs.TryResolveInstance</c>.
/// </summary>
public static class WorldInstanceToken {
    /// <summary>The reserved trailing-token prefix every instance-addressed verb shares (case-insensitive match).</summary>
    public const string Prefix = "instance:";

    /// <summary>Returns whether <paramref name="token"/> opens with <see cref="Prefix"/>: the test every
    /// instance-addressed verb uses to decide whether a positional slot is the trailing instance token before parsing it
    /// further.</summary>
    /// <param name="token">The candidate token.</param>
    /// <returns>Whether the token carries the reserved prefix.</returns>
    public static bool Is(ReadOnlySpan<char> token) => token.StartsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: Prefix
    );
    /// <summary>Parses the name out of a token already confirmed by <see cref="Is"/>: decodes the echo's escapes (see the
    /// remarks), refuses an empty name, and refuses the literal boot-instance name as redundant addressing (the boot world
    /// is already the default; omit the token to address it). Does not check whether a running instance answers to the
    /// name: a caller that needs the instance follows up with <c>WorldInstanceArgs.TryResolveInstance</c>, and a caller
    /// that defers existence to its own downstream refusal (<c>world.rate</c>'s pause and resume) stops here.</summary>
    /// <param name="token">The token, prefix included.</param>
    /// <param name="verb">The verb name the refusal text is scoped to.</param>
    /// <param name="name">The parsed instance name on success.</param>
    /// <param name="error">The refusal, on failure.</param>
    /// <returns>Whether a candidate name was parsed.</returns>
    /// <remarks>This is the read side of the tag every instance-addressed echo writes with
    /// <see cref="CommandEcho.SpliceTag(string, string, string)"/>, and the tag exists to be copied off an echo and handed
    /// straight back. By the time the token arrives, System.CommandLine's splitter has removed the value's surrounding
    /// quotes and left its escapes alone, so the name is finished here with <see cref="CommandEcho.Unescape(string)"/>:
    /// a world named <c>C:\my games</c> or <c>say "hi"</c> round-trips through the tag only so.</remarks>
    public static bool TryParseName(ReadOnlySpan<char> token, string verb, out string name, out CommandResult? error) {
        var candidate = CommandEcho.Unescape(value: token[Prefix.Length..].ToString());

        if (string.IsNullOrWhiteSpace(value: candidate)) {
            name = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: instance: must name a running instance — see world.instance.status]");

            return false;
        }

        if (WorldDefinitionLoader.IsBootInstance(name: candidate)) {
            name = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: '{WorldDefinitionLoader.BootInstanceName}' is the world this process booted with — omit instance: to address it]");

            return false;
        }

        name = candidate;
        error = null;

        return true;
    }
}
