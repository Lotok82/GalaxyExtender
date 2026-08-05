using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Guards the "no credentials in source control" rule so it survives contact with future commits.
/// Conventions in a README get forgotten; a failing test does not.
///
/// The webhook URL and the per-client API keys are live credentials. They belong in
/// `dotnet user-secrets` locally and environment variables on the host — never in a tracked file.
/// </summary>
public sealed class ConfigurationHygieneTests
{
    /// <summary>Files that are committed, so must never contain a real value.</summary>
    private static readonly string[] TrackedConfigFileNames =
    [
        "appsettings.json",
        "appsettings.Development.json"
    ];

    /// <summary>Files allowed to hold real values, therefore required to be git-ignored.</summary>
    private static readonly string[] MustBeGitIgnored =
    [
        "appsettings.Production.json",
        "appsettings.Local.json"
    ];

    [Fact]
    public void Tracked_config_files_contain_no_discord_webhook_url()
    {
        foreach (var (fileName, root) in TrackedConfigDocuments())
        {
            foreach (var (path, value) in StringValues(root, "$"))
            {
                Assert.False(
                    value.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase),
                    $"{fileName} at {path} looks like a live Discord webhook URL. Move it to user-secrets " +
                    $"(local) or the Discord__WebhookUrl environment variable (host).");
            }
        }
    }

    [Fact]
    public void Tracked_config_files_leave_the_webhook_url_empty()
    {
        foreach (var (fileName, root) in TrackedConfigDocuments())
        {
            if (!root.TryGetProperty("Discord", out var discord) ||
                !discord.TryGetProperty("WebhookUrl", out var webhookUrl))
            {
                continue;
            }

            Assert.True(
                string.IsNullOrWhiteSpace(webhookUrl.GetString()),
                $"{fileName} sets Discord:WebhookUrl. Tracked config may only carry the empty " +
                $"placeholder that documents the shape.");
        }
    }

    [Fact]
    public void Tracked_api_keys_are_obvious_placeholders()
    {
        foreach (var (fileName, root) in TrackedConfigDocuments())
        {
            if (!root.TryGetProperty("Relay", out var relay) ||
                !relay.TryGetProperty("ApiKeys", out var apiKeys) ||
                apiKeys.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var key in apiKeys.EnumerateObject())
            {
                var value = key.Value.GetString() ?? string.Empty;
                Assert.Contains("not-a-secret", value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Config_files_that_may_hold_real_values_are_git_ignored()
    {
        var gitignorePath = Path.Combine(RelayRoot().FullName, ".gitignore");
        Assert.True(File.Exists(gitignorePath), $"Expected a .gitignore at {gitignorePath}");

        var gitignore = File.ReadAllText(gitignorePath);

        foreach (var fileName in MustBeGitIgnored)
        {
            Assert.Contains(fileName, gitignore, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<(string FileName, JsonElement Root)> TrackedConfigDocuments()
    {
        var projectDirectory = Path.Combine(RelayRoot().FullName, "src", "GalaxyExtender.Relay");

        foreach (var fileName in TrackedConfigFileNames)
        {
            var path = Path.Combine(projectDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            yield return (fileName, document.RootElement.Clone());
        }
    }

    /// <summary>
    /// Walks up from the test binaries to the folder holding the solution, so the test does not
    /// depend on how deep the build output happens to be nested.
    /// </summary>
    private static DirectoryInfo RelayRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "GalaxyExtender.Relay.sln")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException(
                "Could not locate the Relay root (searched upwards for GalaxyExtender.Relay.sln).");
    }

    private static IEnumerable<(string Path, string Value)> StringValues(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return (path, element.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var result in StringValues(property.Value, $"{path}.{property.Name}"))
                    {
                        yield return result;
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var result in StringValues(item, $"{path}[{index}]"))
                    {
                        yield return result;
                    }

                    index++;
                }

                index = 0;
                break;
        }
    }
}
