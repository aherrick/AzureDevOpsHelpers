using System.Net.Http;
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

                var response = JsonDocument.Parse(await GET(uriBuilder.ToString()));
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
        var response = JsonDocument.Parse(await GET(url));
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

    private async Task<PullRequestDto> GetPullRequestChanges(
        string project,
        string repoName,
        int pullRequestId
    )
    {
        var repoJsonDoc = JsonDocument.Parse(
            await GET(
                $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoName}?api-version=7.1-preview.1"
            )
        );

        var repoId = repoJsonDoc.RootElement.GetProperty("id").GetString();

        var prJsonDoc = JsonDocument.Parse(
            await GET(
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
            await GET(
                $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/diffs/commits?baseVersionType=commit&baseVersion={baseCommitId}&targetVersionType=commit&targetVersion={targetCommitId}&api-version=7.1-preview.1"
            )
        );

        return new PullRequestDto(
            changesJsonDoc.RootElement.GetProperty("changes").EnumerateArray(),
            repoId,
            baseCommitId,
            targetCommitId
        );
    }

    public async Task GetDiffForPullRequest(string project, string repoName, int pullRequestId)
    {
        var prDto = await GetPullRequestChanges(project, repoName, pullRequestId);

        foreach (var change in prDto.Changes)
        {
            if (
                change.TryGetProperty("item", out var item)
                && item.TryGetProperty("path", out var path)
                && item.TryGetProperty("gitObjectType", out var type)
                && type.GetString() == "blob"
            )
            {
                var filePath = path.GetString();

                var fileDiffBuilder = new StringBuilder();

                // Add filepath header
                fileDiffBuilder.AppendLine($"src: {filePath}");
                fileDiffBuilder.AppendLine();

                var body = new
                {
                    contributionIds = new[] { "ms.vss-code-web.file-diff-data-provider" },
                    dataProviderContext = new
                    {
                        properties = new
                        {
                            repositoryId = prDto.RepositoryId,
                            diffParameters = new
                            {
                                includeCharDiffs = true,
                                modifiedPath = filePath,
                                modifiedVersion = $"GC{prDto.BaseCommitId}",
                                originalPath = filePath,
                                originalVersion = $"GC{prDto.TargetCommitId}",
                                partialDiff = true,
                                forceLoad = false,
                            },
                        },
                    },
                };

                var bodySerialize = JsonSerializer.Serialize(body);

                var result = await POST(
                    $"https://dev.azure.com/{org}/_apis/Contribution/HierarchyQuery/project/{project}?api-version=5.0-preview.1",
                    bodySerialize
                );

                using var jsonDoc = JsonDocument.Parse(result);
                var blocks = jsonDoc
                    .RootElement.GetProperty("dataProviders")
                    .GetProperty("ms.vss-code-web.file-diff-data-provider")
                    .GetProperty("blocks")
                    .EnumerateArray()
                    .ToList();

                // Group blocks into sections based on truncation attributes
                List<List<JsonElement>> sections = [];
                List<JsonElement> currentSection = [];
                bool? lastTruncatedAfter = null;

                foreach (var block in blocks)
                {
                    bool truncatedBefore = false;
                    bool truncatedAfter = false;

                    if (block.TryGetProperty("truncatedBefore", out var truncatedBeforeProp))
                    {
                        truncatedBefore = truncatedBeforeProp.GetBoolean();
                    }
                    if (block.TryGetProperty("truncatedAfter", out var truncatedAfterProp))
                    {
                        truncatedAfter = truncatedAfterProp.GetBoolean();
                    }

                    if (lastTruncatedAfter == true && truncatedBefore)
                    {
                        sections.Add(currentSection);
                        currentSection = [];
                    }

                    currentSection.Add(block);
                    lastTruncatedAfter = truncatedAfter;
                }
                if (currentSection.Count > 0)
                {
                    sections.Add(currentSection);
                }

                // Process each section
                int currentMLine = 0; // Running line number for modified file

                for (int i = 0; i < sections.Count; i++)
                {
                    var section = sections[i];

                    foreach (var block in section)
                    {
                        int changeType = block.GetProperty("changeType").GetInt32();
                        var oLines = block
                            .GetProperty("oLines")
                            .EnumerateArray()
                            .Select(e => e.GetString()!)
                            .ToList();
                        var mLines = block
                            .GetProperty("mLines")
                            .EnumerateArray()
                            .Select(e => e.GetString()!)
                            .ToList();
                        int oLine = block.GetProperty("oLine").GetInt32();

                        if (changeType == 0) // Unchanged
                        {
                            for (int j = 0; j < oLines.Count; j++)
                            {
                                diffBuilder.AppendLine($" {oLine + j,4} |    {oLines[j]}");
                                currentMLine++;
                            }
                        }
                        else if (changeType == 2) // Deleted
                        {
                            for (int j = 0; j < oLines.Count; j++)
                            {
                                diffBuilder.AppendLine($"-{oLine + j,4} |    {oLines[j]}");
                            }
                            // Do not increment currentMLine for deletions, as they don't appear in modified file
                        }
                        else if (changeType == 1) // Added
                        {
                            for (int j = 0; j < mLines.Count; j++)
                            {
                                diffBuilder.AppendLine($"+{currentMLine + j,4} |    {mLines[j]}");
                            }
                            currentMLine += mLines.Count;
                        }
                    }

                    // Add separator between sections, but not after the last one
                    if (i < sections.Count - 1)
                    {
                        diffBuilder.AppendLine("---");
                        diffBuilder.AppendLine();
                    }
                }

                // Add empty line after each file's diff
                diffBuilder.AppendLine();
            }
        }

        // Output the complete diff
        Console.WriteLine(diffBuilder.ToString());

        Console.ReadLine();
    }

    public async Task<List<FileUnifiedDiff>> GetUniffedDiffForPullRequest(
        string project,
        string repoName,
        int pullRequestId
    )
    {
        var filleUnifiedDiffDtos = new List<FileUnifiedDiff>();

        var prDto = await GetPullRequestChanges(project, repoName, pullRequestId);

        foreach (var change in prDto.Changes)
        {
            if (
                change.TryGetProperty("item", out var item)
                && item.TryGetProperty("path", out var path)
                && item.TryGetProperty("gitObjectType", out var type)
                && type.GetString() == "blob"
            )
            {
                var filePath = path.GetString()!;

                var oldText = await GET(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{prDto.RepositoryId}/items?path={Uri.EscapeDataString(filePath)}&versionDescriptor.version={prDto.BaseCommitId}&versionDescriptor.versionType=commit&api-version=7.1"
                );

                var newText = await GET(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{prDto.RepositoryId}/items?path={Uri.EscapeDataString(filePath)}&versionDescriptor.version={prDto.TargetCommitId}&versionDescriptor.versionType=commit&api-version=7.1"
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

    private async Task<string> GET(string url)
    {
        return await MakeApiCall(url, HttpMethod.Get, null);
    }

    private async Task<string> POST(string url, string content = null)
    {
        return await MakeApiCall(url, HttpMethod.Post, content);
    }

    private async Task<string> MakeApiCall(string url, HttpMethod method, string content)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"))
        );

        HttpResponseMessage response;
        if (method == HttpMethod.Post)
        {
            var httpContent =
                content != null
                    ? new StringContent(content, Encoding.UTF8, "application/json")
                    : null;
            response = await client.PostAsync(url, httpContent);
        }
        else
        {
            response = await client.GetAsync(url);
        }

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