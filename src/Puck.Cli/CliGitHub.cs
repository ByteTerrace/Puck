namespace Puck.Cli;

// The GitHub Actions seams every automation verb shares: required environment, secret masking, step outputs,
// and the job summary. Each is a no-op outside Actions except EnvironmentVariable, which refuses to guess.
internal static class CliGitHub {
    public static bool IsActions => (Environment.GetEnvironmentVariable(variable: "GITHUB_ACTIONS") == "true");

    public static string EnvironmentVariable(string name) =>
        (Environment.GetEnvironmentVariable(variable: name) ?? throw new InvalidOperationException(message: $"{name} is required."));
    public static void Mask(string value) {
        if (IsActions) { Console.WriteLine(value: $"::add-mask::{value}"); }
    }
    public static void Output(string key, string value) {
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } output) { File.AppendAllText(contents: $"{key}={value}\n", path: output); }
    }
    public static void Summarize(string message) {
        Console.WriteLine(value: message);
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) { File.AppendAllText(contents: (message + "\n\n"), path: summary); }
    }
}
