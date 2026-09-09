#!/usr/bin/env dotnet
#:property PublishAot=false
using System.Net.Http.Headers;

try {
    if (args is ["--help" or "-h"]) {
        Console.WriteLine(value: "Apply a completed Check source formatting artifact using FORMAT_RUN_ID, GITHUB_REPOSITORY and GH_TOKEN. Run only from the trusted default branch.");
        return 0;
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(value: 2), BaseAddress = new Uri(uriString: (Required(name: "GITHUB_API_URL").TrimEnd(trimChar: '/') + "/")) };

    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme: "Bearer", parameter: Required(name: "GH_TOKEN"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd(input: "Puck-Formatter/1.0");
    client.DefaultRequestHeaders.Add(name: "X-GitHub-Api-Version", value: "2022-11-28");
    await new Puck.FormatSubmission(client: client).RunAsync(repository: Required(name: "GITHUB_REPOSITORY"), runId: long.Parse(s: Required(name: "FORMAT_RUN_ID"), provider: System.Globalization.CultureInfo.InvariantCulture), graphUrl: Required(name: "GITHUB_GRAPHQL_URL"));
    return 0;
} catch (Exception error) {
    Console.Error.WriteLine(value: $"format submit: {error.Message}");
    return 1;
}
static string Required(string name) => (Environment.GetEnvironmentVariable(variable: name) ?? throw new InvalidOperationException(message: $"Missing {name}."));
