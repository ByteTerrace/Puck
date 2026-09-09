namespace Puck.Cli.Automation;

internal static class AutomationCommand {
    public static async Task<int> RunAsync(string command, string[] args) {
        try {
            return (command, args) switch {
                ("docs", ["build", .. var rest]) => await DocsBuildCommand.RunAsync(args: rest),
                ("world", ["prepare", .. var rest]) => WorldPrepareCommand.Run(args: rest),
                ("world", ["probe", .. var rest]) => await WorldProbeCommand.RunAsync(args: rest),
                ("wasm", ["build", .. var rest]) => await WasmBuildCommand.RunAsync(args: rest),
                ("bundle", ["create", var directory, var commit]) => BundleCommand.Create(commit: commit, directory: directory),
                ("bundle", ["verify", var directory, var commit]) => BundleCommand.Verify(commit: commit, directory: directory),
                _ => Usage(args: args, command: command),
            };
        } catch (Exception error) {
            Console.Error.WriteLine(value: $"{command}: {error.Message}");
            return 1;
        }
    }

    private static int Usage(string command, string[] args) {
        Console.WriteLine(value: command switch {
            "docs" => "puck docs build [output-directory]",
            "world" => "puck world prepare <worlds-directory> <output-directory>\npuck world probe <host> <port> <public-key-file>",
            "wasm" => "puck wasm build",
            "bundle" => "puck bundle <create|verify> <directory> <commit>",
            _ => throw new ArgumentException(message: $"Unknown automation command: {command}"),
        });
        return ((args is ["-h" or "--help"]) ? 0 : 2);
    }
}
