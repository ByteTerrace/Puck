using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Motion;

/// <summary>A settled still the World announced: <c>[still] &lt;path&gt; tick=&lt;T&gt; hash=&lt;h&gt; pose=&lt;p&gt; shown=&lt;S&gt;</c>.</summary>
/// <param name="Path">The composed frame's file, forward slashes.</param>
/// <param name="Tick">The tick the still is pinned to.</param>
/// <param name="Hash">The capture-scope state hash at that tick.</param>
/// <param name="Pose">The pose-scope state hash at that tick, which carries no clock.</param>
/// <param name="Shown">The tick the served frame shows.</param>
/// <param name="ShowsAnotherTick">Whether the World noted that the frame shows another tick than the one pinned.</param>
public sealed record MotionStillStamp(string Path, ulong Tick, string Hash, string Pose, string Shown, bool ShowsAnotherTick);
/// <summary>One camera's file of a still: <c>[still] slot &lt;camera&gt; &lt;W&gt;x&lt;H&gt; at &lt;x&gt;,&lt;y&gt; -&gt; &lt;path&gt;</c>.</summary>
/// <param name="Camera">The slot's camera.</param>
/// <param name="Path">The slot's file, forward slashes.</param>
public sealed record MotionSlotFile(string Camera, string Path);
/// <summary>What a pinned line answered: <c>[world.at] tick &lt;T&gt; ran|refused: &lt;line&gt; -&gt; &lt;answer&gt;</c>.</summary>
/// <param name="Tick">The tick the line ran at.</param>
/// <param name="Refused">Whether the line was refused.</param>
/// <param name="Line">The console line.</param>
/// <param name="Answer">What it answered, on one line; empty when it answered nothing.</param>
public sealed record MotionPinnedAnswer(ulong Tick, bool Refused, string Line, string Answer);
/// <summary>Reads the lines a World writes while a <c>puck motion</c> run drives it.</summary>
public static partial class MotionTranscript {
    [GeneratedRegex(pattern: @"^\[still\] (?<path>.+) tick=(?<tick>\d+) hash=(?<hash>\S+) pose=(?<pose>\S+) shown=(?<shown>\S+)(?<rest>.*)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex StillPattern();
    [GeneratedRegex(pattern: @"^\[still\] slot (?<camera>\S+) \d+x\d+ at \d+,\d+ -> (?<path>.+)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();
    [GeneratedRegex(pattern: @"^\[world\.at\] tick (?<tick>\d+) (?<outcome>ran|refused): (?<text>.*?)(?: -> (?<answer>.*))?$", options: RegexOptions.CultureInvariant)]
    private static partial Regex PinnedPattern();
    [GeneratedRegex(pattern: @"^\[world\.at: tick (?<tick>\d+) pending \d+ ran \d+\]", options: RegexOptions.CultureInvariant)]
    private static partial Regex ReadBackPattern();

    /// <summary>Reads a <c>[still]</c> line for a composed frame.</summary>
    /// <param name="line">The line.</param>
    /// <param name="stamp">The stamp, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the line announces a still.</returns>
    public static bool TryParseStill(string line, [NotNullWhen(returnValue: true)] out MotionStillStamp? stamp) {
        var match = StillPattern().Match(input: line);

        if (!match.Success) {
            stamp = null;

            return false;
        }

        stamp = new MotionStillStamp(
            Hash: match.Groups["hash"].Value,
            Path: match.Groups["path"].Value.Replace(newChar: '/', oldChar: '\\'),
            Pose: match.Groups["pose"].Value,
            Shown: match.Groups["shown"].Value,
            ShowsAnotherTick: match.Groups["rest"].Value.Contains(comparisonType: StringComparison.Ordinal, value: "another tick"),
            Tick: ulong.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["tick"].Value)
        );

        return true;
    }
    /// <summary>Reads a <c>[still] slot</c> line.</summary>
    /// <param name="line">The line.</param>
    /// <param name="slot">The slot file, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the line announces a slot's file.</returns>
    public static bool TryParseSlot(string line, [NotNullWhen(returnValue: true)] out MotionSlotFile? slot) {
        var match = SlotPattern().Match(input: line);

        if (!match.Success) {
            slot = null;

            return false;
        }

        slot = new MotionSlotFile(
            Camera: match.Groups["camera"].Value,
            Path: match.Groups["path"].Value.Replace(newChar: '/', oldChar: '\\')
        );

        return true;
    }
    /// <summary>Reads the narration of a pinned line.</summary>
    /// <param name="line">The line.</param>
    /// <param name="answer">The answer, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the line narrates a pinned line.</returns>
    public static bool TryParsePinned(string line, [NotNullWhen(returnValue: true)] out MotionPinnedAnswer? answer) {
        var match = PinnedPattern().Match(input: line);

        if (!match.Success) {
            answer = null;

            return false;
        }

        answer = new MotionPinnedAnswer(
            Answer: match.Groups["answer"].Value,
            Line: match.Groups["text"].Value,
            Refused: (match.Groups["outcome"].Value == "refused"),
            Tick: ulong.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["tick"].Value)
        );

        return true;
    }
    /// <summary>Reads the tick a bare <c>world.at</c> answers.</summary>
    /// <param name="line">The line.</param>
    /// <param name="tick">The World's completed tick, or 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the line is that answer.</returns>
    public static bool TryParseReadBack(string line, out ulong tick) {
        var match = ReadBackPattern().Match(input: line);

        tick = (match.Success ? ulong.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["tick"].Value) : 0UL);

        return match.Success;
    }
    /// <summary>Returns which creation a <c>body.rig</c> answer names, which says which form body 0 is in.</summary>
    /// <param name="answer">The answer, <c>[body.rig: body:0 creation=&lt;id&gt; …]</c>.</param>
    /// <returns>The creation id, or <see langword="null"/> when the answer names none.</returns>
    public static string? CreationOf(string answer) {
        const string Key = "creation=";
        var start = answer.IndexOf(comparisonType: StringComparison.Ordinal, value: Key);

        if (start < 0) {
            return null;
        }

        start += Key.Length;

        var end = answer.IndexOfAny(anyOf: [' ', ']'], startIndex: start);

        return ((end < 0) ? answer[start..] : answer[start..end]);
    }
}
