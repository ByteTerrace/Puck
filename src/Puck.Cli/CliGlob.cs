using System.Text;

using Resharp;

namespace Puck.Cli;

// A single include/exclude glob compiled to the search engine's regex, shared by the verbs that take -g
// and --not. A glob with no '/' matches the basename only; one with a '/' matches the cwd-relative path.
internal sealed class CliGlob {
    private readonly Regex m_regex;

    public bool BasenameOnly { get; }

    public CliGlob(string glob) {
        var normalized = glob.Replace(newChar: '/', oldChar: '\\');

        BasenameOnly = !normalized.Contains(value: '/');
        m_regex = new Regex(pattern: GlobToRegex(glob: normalized), options: ResharpOptions.HighThroughputDefaults);
    }

    public bool IsMatch(string value) => m_regex.IsMatch(input: value);

    // Anchored whole-string translation. `**` -> any (incl. '/'), `*` -> any run except '/',
    // `?` -> one non-'/'. Every other char is a literal, escaping the engine's metacharacters
    // (which include _ & ~ on top of the usual set).
    private static string GlobToRegex(string glob) {
        var sb = new StringBuilder(value: "^");
        var i = 0;

        while (i < glob.Length) {
            var c = glob[i];

            if (c == '*') {
                if (((i + 1) < glob.Length) && (glob[(i + 1)] == '*')) {
                    sb.Append(value: ".*");
                    i += 2;
                } else {
                    sb.Append(value: "[^/]*");
                    i++;
                }
            } else if (c == '?') {
                sb.Append(value: "[^/]");
                i++;
            } else {
                if (char.IsLetterOrDigit(c: c) || (c == '/')) {
                    sb.Append(value: c);
                } else {
                    sb.Append(value: '\\').Append(value: c);
                }

                i++;
            }
        }

        return sb.Append(value: '$').ToString();
    }
}
