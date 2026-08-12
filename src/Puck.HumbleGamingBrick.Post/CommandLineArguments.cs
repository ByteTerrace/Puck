namespace Puck.HumbleGamingBrick.Post;

internal static class CommandLineArguments {
    internal static string? Value(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(
                a: args[index],
                b: name,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return args[(index + 1)];
            }
        }

        return null;
    }
}
