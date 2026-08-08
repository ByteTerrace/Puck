namespace Puck.Analyzers;

internal static class JsonEscape {
    public static bool TryDecode(char escape, out char value) {
        value = escape switch {
            '"' => '"',
            '\\' => '\\',
            '/' => '/',
            'b' => '\b',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            _ => default,
        };

        return (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't');
    }
}
