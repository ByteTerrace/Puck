using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins phase 0's scope to the root the verb was handed. <c>dotnet format whitespace &lt;project&gt;</c> formats every
/// COMPILE ITEM of that project, and this repository links <c>build/VerifiedCodeAttribute.cs</c> into all of them, so
/// an unscoped run of <c>format src/Puck.Commands</c> rewrote a file two directories above the root — a diff the
/// caller never asked for, in a file the corpus never enumerated.
/// <para>The pattern's shape is the whole fix, and it is unforgiving: <c>dotnet format</c> matches
/// <c>--include</c> against each document's WORKING-DIRECTORY-relative path and only reads a directory when the
/// pattern ends in a separator. An absolute path, or a directory named without the trailing slash, matches nothing —
/// and matching nothing is silent, so a verify run would report a clean tree it never opened.</para>
/// </summary>
public sealed class FormatWhitespaceScopeTests {
    [Fact]
    public void ARootBelowTheWorkingDirectoryBecomesARelativeDirectoryPattern() {
        Assert.Equal(
            actual: WhitespacePhase.IncludePattern(scanRoot: "C:\\repo\\src\\Puck.Commands", workingDirectory: "C:\\repo"),
            expected: "src/Puck.Commands/"
        );
    }
    [Fact]
    public void TheWorkingDirectoryItselfBecomesTheCurrentDirectoryPattern() {
        // Path.GetRelativePath answers "." here, and a bare "." matches nothing at all — only "./" reads as the
        // directory. Formatting everything the projects carry is the right answer for a root that IS the working
        // directory, but only because the pattern still says so.
        Assert.Equal(
            actual: WhitespacePhase.IncludePattern(scanRoot: "C:\\repo", workingDirectory: "C:\\repo"),
            expected: "./"
        );
    }
    [Fact]
    public void ARootOutsideTheWorkingDirectoryIsNotSpelledAtAll() {
        // A "../" pattern matches nothing, which would make a verify run pass over a tree it never read. Answering
        // null instead is what lets the caller run unscoped and say so.
        Assert.Null(@object: WhitespacePhase.IncludePattern(scanRoot: "C:\\elsewhere\\src", workingDirectory: "C:\\repo"));
        Assert.Null(@object: WhitespacePhase.IncludePattern(scanRoot: "C:\\repo", workingDirectory: "C:\\repo\\src"));
    }
    [Fact]
    public void ADifferentVolumeIsNotSpelledAtAll() {
        // Path.GetRelativePath hands back the absolute path when no relative one exists, and an absolute --include
        // matches nothing.
        Assert.Null(@object: WhitespacePhase.IncludePattern(scanRoot: "D:\\other\\src", workingDirectory: "C:\\repo"));
    }
}
