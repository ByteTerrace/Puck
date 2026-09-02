using Puck.Commands;
using static Puck.Commands.CommandArgs;

namespace Puck.World;

/// <summary>
/// Shared player-index parsing for the world verbs: the trailing (or positional) integer index the drive-a-player and
/// roster-management verbs constrain to <c>[min, max]</c>. A local index-in-range convenience over
/// <see cref="Puck.Commands.CommandArgs.TryParseInt(string, out int)"/>. Also owns the trailing
/// <c>instance:&lt;name&gt;</c> token grammar — see <see cref="InstanceTokenPrefix"/>. The bracket-splice echo
/// surgery every instance-addressed verb shares lives in
/// <see cref="Puck.Commands.CommandEcho.SpliceTag(string, string, string)"/>.
/// </summary>
internal static class WorldArgs {
    /// <summary>Parses an integer index token at <paramref name="at"/> constrained to <c>[min, max]</c>. When
    /// <paramref name="fallback"/> is non-<see langword="null"/> the token is optional — an absent token yields the
    /// fallback (and <see langword="true"/>); when it is <see langword="null"/> the token is required — an absent token
    /// fails. A present token that does not parse or falls outside the range always fails.</summary>
    /// <param name="args">The full argument array.</param>
    /// <param name="at">The index into <paramref name="args"/> the token is expected at.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <param name="fallback">The default when the token is absent, or <see langword="null"/> to require it.</param>
    /// <param name="value">The parsed (or fallback) value; <c>0</c> on failure.</param>
    /// <returns>Whether a valid index (or the fallback) was resolved.</returns>
    public static bool TryParseIndex(string[] args, int at, int min, int max, int? fallback, out int value) {
        if (args.Length <= at) {
            value = (fallback ?? 0);

            return fallback.HasValue;
        }

        return (
            TryParseInt(
            text: args[at],
            value: out value
        ) &&
            (value >= min) &&
            (value <= max)
        );
    }
    /// <summary>The zero-copy peer of <see cref="TryParseIndex(string[], int, int, int, int?, out int)"/> over a
    /// <see cref="WireArgs"/>, parsing the index token straight from its span. Same optional/required contract as the
    /// array overload.</summary>
    /// <param name="args">The wire arguments.</param>
    /// <param name="at">The token position the index is expected at.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <param name="fallback">The default when the token is absent, or <see langword="null"/> to require it.</param>
    /// <param name="value">The parsed (or fallback) value; <c>0</c> on failure.</param>
    /// <returns>Whether a valid index (or the fallback) was resolved.</returns>
    public static bool TryParseIndex(in WireArgs args, int at, int min, int max, int? fallback, out int value) {
        if (args.Count <= at) {
            value = (fallback ?? 0);

            return fallback.HasValue;
        }

        return (
            args.TryInt(
            index: at,
            value: out value
        ) &&
            (value >= min) &&
            (value <= max)
        );
    }

    /// <summary>The reserved trailing-token prefix every instance-addressed verb shares (case-insensitive match).</summary>
    public const string InstanceTokenPrefix = "instance:";

    /// <summary>Whether <paramref name="token"/> opens with <see cref="InstanceTokenPrefix"/> — the test every
    /// instance-addressed verb uses to decide whether a positional slot IS the trailing instance token before
    /// parsing it further.</summary>
    /// <param name="token">The candidate token.</param>
    /// <returns>Whether the token carries the reserved prefix.</returns>
    public static bool IsInstanceToken(ReadOnlySpan<char> token) => token.StartsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: InstanceTokenPrefix
    );
    /// <summary>Parses the name out of a token already confirmed by <see cref="IsInstanceToken"/>: decodes the echo's
    /// escapes (see the remarks), refuses an empty name, and refuses the literal boot-instance name as redundant
    /// addressing (the boot world is already the default — omit the token to address it). Does not check whether a
    /// running instance answers to the resolved name; a caller that needs the resolved <see cref="WorldInstance"/>
    /// follows up with
    /// <see cref="TryResolveInstance(ReadOnlySpan{char}, string, WorldInstanceHost, out WorldInstance?, out CommandResult?)"/>,
    /// and a caller that defers existence to its own downstream refusal (e.g. <c>world.rate</c>'s <c>TryPause</c>/
    /// <c>TryResume</c>/<c>TryDescribeRate</c>) stops here.</summary>
    /// <param name="token">The token, prefix included.</param>
    /// <param name="verb">The verb name the refusal text is scoped to.</param>
    /// <param name="name">The parsed instance name on success.</param>
    /// <param name="error">The refusal, on failure.</param>
    /// <returns>Whether a candidate name was parsed.</returns>
    /// <remarks>This is the READ side of the tag every instance-addressed echo writes with
    /// <see cref="Puck.Commands.CommandEcho.SpliceTag(string, string, string)"/>, and the tag exists to be copied off
    /// an echo and handed straight back. By the time the token arrives, System.CommandLine's splitter has removed the
    /// value's surrounding quotes and left its escapes alone — that splitter knows nothing else — so the name is
    /// finished here with <see cref="Puck.Commands.CommandEcho.Unescape(string)"/>. Without it a world named
    /// <c>C:\my games</c> came back with its backslash doubled and one named <c>say "hi"</c> came back with both quotes
    /// replaced by backslashes, which is to say the round trip the tag promises did not hold for either.</remarks>
    public static bool TryParseInstanceName(ReadOnlySpan<char> token, string verb, out string name, out CommandResult? error) {
        var candidate = CommandEcho.Unescape(value: token[InstanceTokenPrefix.Length..].ToString());

        if (string.IsNullOrWhiteSpace(value: candidate)) {
            name = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: instance: must name a running instance — see world.instance.status]");

            return false;
        }

        if (string.Equals(
            a: candidate,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            name = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: '{WorldInstanceHost.BootInstanceName}' is the world this process booted with — omit instance: to address it]");

            return false;
        }

        name = candidate;
        error = null;

        return true;
    }
    /// <summary>Parses and resolves a trailing instance token to its running instance, layering the "no instance
    /// named '…'" refusal over <see cref="TryParseInstanceName"/> — the form every instance-addressed read-back
    /// that needs the instance object itself (not just its name) shares.</summary>
    /// <param name="token">The token, prefix included.</param>
    /// <param name="verb">The verb name the refusal text is scoped to.</param>
    /// <param name="instances">The host to resolve the name against.</param>
    /// <param name="instance">The resolved instance on success.</param>
    /// <param name="error">The refusal, on failure.</param>
    /// <returns>Whether the token resolved to a running instance.</returns>
    public static bool TryResolveInstance(ReadOnlySpan<char> token, string verb, WorldInstanceHost instances, out WorldInstance? instance, out CommandResult? error) {
        if (!TryParseInstanceName(
            error: out error,
            name: out var name,
            token: token,
            verb: verb
        )) {
            instance = null;

            return false;
        }

        if (
            !instances.TryGet(
            instance: out var resolved,
            name: name
        ) ||
            (resolved is null)
        ) {
            instance = null;
            error = CommandResult.Error(output: $"[{verb}: no instance named '{name}' — see world.instance.status]");

            return false;
        }

        instance = resolved;
        error = null;

        return true;
    }
}
