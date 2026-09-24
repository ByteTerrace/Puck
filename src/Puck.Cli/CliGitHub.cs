namespace Puck.Cli;

// The GitHub Actions seams every automation verb shares: the run's required values, secret masking, step outputs,
// and the job summary. Each seam is a no-op outside Actions except a required value, which refuses to guess. Every
// variable read here is GitHub-defined and on EnvironmentReadAllowlist.
internal static class CliGitHub {
    public static string ApiUrl => Required(name: "GITHUB_API_URL", value: Environment.GetEnvironmentVariable(variable: "GITHUB_API_URL"));
    public static string GraphQlUrl => Required(name: "GITHUB_GRAPHQL_URL", value: Environment.GetEnvironmentVariable(variable: "GITHUB_GRAPHQL_URL"));
    public static bool IsActions => (Environment.GetEnvironmentVariable(variable: "GITHUB_ACTIONS") == "true");
    public static string Ref => Required(name: "GITHUB_REF", value: Environment.GetEnvironmentVariable(variable: "GITHUB_REF"));
    public static string Repository => Required(name: "GITHUB_REPOSITORY", value: Environment.GetEnvironmentVariable(variable: "GITHUB_REPOSITORY"));
    public static string RunAttempt => Required(name: "GITHUB_RUN_ATTEMPT", value: Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ATTEMPT"));
    public static string RunId => Required(name: "GITHUB_RUN_ID", value: Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID"));
    public static string Sha => Required(name: "GITHUB_SHA", value: Environment.GetEnvironmentVariable(variable: "GITHUB_SHA"));
    // The GitHub CLI's token variable; the workflow sets it from the run's own token.
    public static string Token => Required(name: "GH_TOKEN", value: Environment.GetEnvironmentVariable(variable: "GH_TOKEN"));

    private static string Required(string name, string? value) =>
        (value ?? throw new InvalidOperationException(message: $"{name} is required."));

    public static void Mask(string value) {
        if (IsActions) { Console.WriteLine(value: $"::add-mask::{value}"); }
    }
    public static void Output(string key, string value) {
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } output) {
            File.AppendAllText(
                contents: $"{key}={value}\n",
                path: output
            );
        }
    }
    public static void Summarize(string message) {
        Console.WriteLine(value: message);
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) {
            File.AppendAllText(
                contents: (message + "\n\n"),
                path: summary
            );
        }
    }
}
