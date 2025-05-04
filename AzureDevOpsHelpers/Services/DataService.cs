using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using AzureDevOpsHelpers.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace AzureDevOpsHelpers.Services;

public class DataService(string org, string pat)
{
    public async Task ExportUsersAndProjects()
    {
        const string OutputFile = "AzureDevOps_Users_Projects.csv";

        Console.WriteLine("Fetching all users and their projects from Azure DevOps...");

        using StreamWriter writer = new(OutputFile);
        writer.WriteLine("Name,Email,Projects");

        var users = await GetAllUsers();
        foreach (var user in users)
        {
            Console.WriteLine($"Fetching projects for user: {user.DisplayName}");
            var projects = await GetProjectsForUser(user.Id);
            string projectList = projects.Count > 0 ? string.Join("|", projects) : "";
            writer.WriteLine($"\"{user.DisplayName}\",\"{user.Email}\",\"{projectList}\"");
        }

        Console.WriteLine($"Data export complete. File saved as: {OutputFile}");
    }

    public async Task<List<User>> GetAllUsers()
    {
        List<User> users = [];
        string continuationToken = null;

        do
        {
            try
            {
                string baseUrl = $"https://vsaex.dev.azure.com/{org}/_apis/userentitlements";
                var uriBuilder = new UriBuilder(baseUrl);
                var query = HttpUtility.ParseQueryString(uriBuilder.Query);
                query["api-version"] = "7.1-preview.3";
                if (!string.IsNullOrEmpty(continuationToken))
                {
                    query["continuationToken"] = continuationToken;
                }
                uriBuilder.Query = query.ToString();

                var response = JsonDocument.Parse(await MakeApiCall(uriBuilder.ToString()));
                if (
                    response != null
                    && response.RootElement.TryGetProperty("members", out var members)
                )
                {
                    foreach (var user in members.EnumerateArray())
                    {
                        if (
                            user.TryGetProperty("user", out var userObject)
                            && user.TryGetProperty("id", out var idProp)
                            && userObject.TryGetProperty("displayName", out var displayNameProp)
                            && userObject.TryGetProperty("principalName", out var emailProp)
                        )
                        {
                            users.Add(
                                new User(
                                    idProp.GetString() ?? "",
                                    displayNameProp.GetString() ?? "No Display Name",
                                    emailProp.GetString() ?? "No Email"
                                )
                            );
                        }
                    }
                    continuationToken = response.RootElement.TryGetProperty(
                        "continuationToken",
                        out var token
                    )
                        ? token.GetString()
                        : null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                break;
            }
        } while (!string.IsNullOrEmpty(continuationToken));

        return users;
    }

    public async Task<List<string>> GetProjectsForUser(string userId)
    {
        string url =
            $"https://vsaex.dev.azure.com/{org}/_apis/userentitlements/{userId}?api-version=5.1-preview.2";
        var response = JsonDocument.Parse(await MakeApiCall(url));
        List<string> projects = [];

        if (
            response?.RootElement.TryGetProperty("projectEntitlements", out var entitlements)
            == true
        )
        {
            foreach (var project in entitlements.EnumerateArray())
            {
                projects.Add(
                    project.GetProperty("projectRef").GetProperty("name").GetString()
                        ?? "Unknown Project"
                );
            }
        }
        return projects;
    }

    public async Task<List<FileUnifiedDiff>> GetUniffedDiffForPullRequest(
        string project,
        string repoName,
        int pullRequestId
    )
    {
        var repoJsonDoc = JsonDocument.Parse(
            await MakeApiCall(
                $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoName}?api-version=7.1-preview.1"
            )
        );

        var repoId = repoJsonDoc.RootElement.GetProperty("id").GetString();

        var prJsonDoc = JsonDocument.Parse(
            await MakeApiCall(
                $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/pullRequests/{pullRequestId}?api-version=7.1"
            )
        );

        var baseCommitId = prJsonDoc
            .RootElement.GetProperty("lastMergeTargetCommit")
            .GetProperty("commitId")
            .GetString();

        var targetCommitId = prJsonDoc
            .RootElement.GetProperty("lastMergeSourceCommit")
            .GetProperty("commitId")
            .GetString();

        var changesJsonDoc = JsonDocument.Parse(
            await MakeApiCall(
                $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/diffs/commits?baseVersionType=commit&baseVersion={baseCommitId}&targetVersionType=commit&targetVersion={targetCommitId}&api-version=7.1-preview.1"
            )
        );

        var filleUnifiedDiffDtos = new List<FileUnifiedDiff>();

        foreach (var change in changesJsonDoc.RootElement.GetProperty("changes").EnumerateArray())
        {
            if (
                change.TryGetProperty("item", out var item)
                && item.TryGetProperty("path", out var path)
                && item.TryGetProperty("gitObjectType", out var type)
                && type.GetString() == "blob"
            )
            {
                var filePath = path.GetString()!;

                var oldText = await MakeApiCall(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/items?path={Uri.EscapeDataString(filePath)}&versionDescriptor.version={baseCommitId}&versionDescriptor.versionType=commit&api-version=7.1"
                );

                var newText = await MakeApiCall(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/items?path={Uri.EscapeDataString(filePath)}&versionDescriptor.version={targetCommitId}&versionDescriptor.versionType=commit&api-version=7.1"
                );

                var diff = InlineDiffBuilder.Diff(oldText, newText);

                int oldLine = 1;
                int newLine = 1;
                var fileDiffBuilder = new StringBuilder();

                foreach (var diffLine in diff.Lines)
                {
                    string lineNumberPrefix;
                    string lineContent = diffLine.Text ?? string.Empty;

                    switch (diffLine.Type)
                    {
                        case ChangeType.Unchanged:
                            lineNumberPrefix = $"{oldLine,4} {newLine,4} | ";
                            fileDiffBuilder.AppendLine($"{lineNumberPrefix}  {lineContent}");
                            oldLine++;
                            newLine++;
                            break;

                        case ChangeType.Deleted:
                            lineNumberPrefix = $"{oldLine,4}      | ";
                            fileDiffBuilder.AppendLine($"{lineNumberPrefix}- {lineContent}");
                            oldLine++;
                            break;

                        case ChangeType.Inserted:
                            lineNumberPrefix = $"     {newLine,4} | ";
                            fileDiffBuilder.AppendLine($"{lineNumberPrefix}+ {lineContent}");
                            newLine++;
                            break;

                        case ChangeType.Modified:
                            lineNumberPrefix = $"{oldLine,4}      | ";
                            fileDiffBuilder.AppendLine($"{lineNumberPrefix}- {lineContent}");
                            oldLine++;
                            lineNumberPrefix = $"     {newLine,4} | ";
                            fileDiffBuilder.AppendLine($"{lineNumberPrefix}+ {lineContent}");
                            newLine++;
                            break;
                    }
                }

                filleUnifiedDiffDtos.Add(new FileUnifiedDiff(filePath, fileDiffBuilder.ToString()));
            }
        }

        return filleUnifiedDiffDtos;
    }

    private async Task<string> MakeApiCall(string url, bool parseJson = true)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"))
        );

        HttpResponseMessage response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine(
                $"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}"
            );
            return null;
        }

        return await response.Content.ReadAsStringAsync();
    }
}