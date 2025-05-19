using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml.Linq;
using AzureDevOpsHelpers.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.Http;
using Microsoft.SemanticKernel;
using Polly;

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
        StringBuilder diffBuilder = new();

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

    public async Task<List<PullRequest>> GetOpenPullRequests(
        string projectName,
        string azureAIEndpoint,
        string azureAIAPIKey
    )
    {
        var prompt = """
            You are an advanced Comment Analysis AI designed to process and summarize pull request comments.
            Your task is to generate a concise summary (1–2 sentences, 15–40 words) capturing the main themes of all provided comments and determine if any comment indicates the pull request is on hold or dependent on another pull request, returning the result in a structured JSON object.
            The input is a string with comments concatenated by newlines.

            ### Requirements:
            - Produce a clear, concise summary (1–2 sentences, 15–40 words) capturing only the core themes or points of all comments.
            - Avoid vague language (e.g., "some reference", "etc.") and skip specific commands unless critical to the theme.
            - Detect if any comment suggests the PR is on hold or dependent (e.g., "waiting for PR #123", "depends on PR", "blocked by PR", "on hold until").
            - Respond with a JSON object only — no markdown, no code fences, no extra formatting or text.

            ### Output Format:
            {
              "summary": "Clear and concise summary of all comments in 1–2 sentences",
              "onHold": true | false
            }

            ### Constraints:
            - Output must be a valid, raw RFC8259-compliant JSON object with no extra characters.
            - Do not include any markdown, code fences (e.g., ```json), or explanation text.
            - If no comment indicates the PR is on hold or dependent, set "onHold" to false.
            - If no comments are provided, return:
              {
                "summary": "No user comments provided.",
                "onHold": false
              }

            ### Input:
            {{ $input }}
            """;

        var httpClient = GetRetryHttpClient();

        var kernel = GetSKChatCompletion(azureAIEndpoint, azureAIAPIKey, httpClient);

        var pullRequests = new List<PullRequest>();

        // Get project ID
        string projectsUrl = $"https://dev.azure.com/{org}/_apis/projects?api-version=7.0";
        var projectsJson = await GET(projectsUrl);
        var projects = JsonSerializer.Deserialize<JsonElement>(projectsJson).GetProperty("value");

        string projectId = null;
        foreach (var project in projects.EnumerateArray())
        {
            if (
                project
                    .GetProperty("name")
                    .GetString()
                    .Equals(projectName, StringComparison.OrdinalIgnoreCase)
            )
            {
                projectId = project.GetProperty("id").GetString();
                break;
            }
        }

        // Get repositories
        var reposUrl =
            $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories?api-version=7.0";
        var reposJson = await GET(reposUrl);
        var repos = JsonSerializer.Deserialize<JsonElement>(reposJson).GetProperty("value");

        foreach (var repo in repos.EnumerateArray())
        {
            string repoId = repo.GetProperty("id").GetString();
            string repoName = repo.GetProperty("name").GetString();

            // Get open pull requests
            var prUrl =
                $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests?searchCriteria.status=active&api-version=7.0";
            var prJson = await GET(prUrl);
            var prs = JsonSerializer.Deserialize<JsonElement>(prJson).GetProperty("value");

            foreach (var pr in prs.EnumerateArray())
            {
                var prId = pr.GetProperty("pullRequestId").GetInt32();
                var title = pr.GetProperty("title").GetString();
                var description = pr.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : "No description provided";
                var status = pr.GetProperty("status").GetString();

                // Get reviewers
                var reviewersUrl =
                    $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{prId}/reviewers?api-version=7.0";
                var reviewersJson = await GET(reviewersUrl);
                var reviewers = JsonSerializer
                    .Deserialize<JsonElement>(reviewersJson)
                    .GetProperty("value");

                var approvedReviewers = new List<string>();
                foreach (var reviewer in reviewers.EnumerateArray())
                {
                    if (reviewer.GetProperty("vote").GetInt32() == 10)
                    {
                        approvedReviewers.Add(reviewer.GetProperty("displayName").GetString());
                    }
                }

                // Get PR comments
                var commentsUrl =
                    $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{prId}/threads?api-version=7.0";
                var commentsJson = await GET(commentsUrl);
                var threads = JsonSerializer
                    .Deserialize<JsonElement>(commentsJson)
                    .GetProperty("value");
                var comments = new List<string>();
                foreach (var thread in threads.EnumerateArray())
                {
                    if (thread.TryGetProperty("comments", out var threadComments))
                    {
                        foreach (var comment in threadComments.EnumerateArray())
                        {
                            if (
                                comment.TryGetProperty("content", out var content)
                                && comment.TryGetProperty("commentType", out var commentType)
                                && commentType.GetString() != "system"
                            )
                            {
                                string commentText = content.GetString();
                                if (!string.IsNullOrEmpty(commentText))
                                {
                                    comments.Add(commentText);
                                }
                            }
                        }
                    }
                }

                string summary;
                bool onHold;

                if (comments.Count == 0)
                {
                    summary = "No comments available.";
                    onHold = false;
                }
                else
                {
                    var commentsInput = string.Join("\n", comments);
                    var filledPrompt = prompt.Replace("{{ $input }}", commentsInput);

                    var result = await kernel.InvokePromptAsync(filledPrompt);

                    var commentAnalysis = JsonSerializer.Deserialize<JsonElement>(
                        result.ToString()
                    );
                    summary = commentAnalysis.GetProperty("summary").GetString();
                    onHold = commentAnalysis.GetProperty("onHold").GetBoolean();
                }

                Console.WriteLine("-------------------------");
                Console.WriteLine($"Project: {projectName}");
                Console.WriteLine($"Repository: {repoName}");
                Console.WriteLine($"PR #{prId}: {title}");
                Console.WriteLine($"Description: {description}");
                Console.WriteLine($"Status: {status}");
                Console.WriteLine(
                    $"Approved By: {(approvedReviewers.Count > 0 ? string.Join(", ", approvedReviewers) : "None")}"
                );
                Console.WriteLine($"Comment Summary: {summary}");
                Console.WriteLine($"On Hold: {onHold}");

                pullRequests.Add(
                    new PullRequest
                    {
                        Id = prId,
                        Title = title,
                        Description = description,
                        Status = status,
                        ProjectName = projectName,
                        RepositoryName = repoName,
                        ApprovedReviewers = approvedReviewers,
                    }
                );
            }
        }

        return pullRequests;
    }

    public async Task AddAICommentsToPullRequest(
        List<FileUnifiedDiff> fileUnifiedDiffs,
        int pullRequestId,
        string project,
        string repoName,
        string azureAIEndpoint,
        string azureAIAPIKey
    )
    {
        var httpClient = GetRetryHttpClient();
        var kernel = GetSKChatCompletion(azureAIEndpoint, azureAIAPIKey, httpClient);

        foreach (var diff in fileUnifiedDiffs)
        {
            Console.WriteLine(diff.UnifiedDiff);

            Console.WriteLine(
                "---------------------------------------------------------------------------------------"
            );

            var diffContent = diff.UnifiedDiff.Trim();
            var prompt = $$""""
                You are a senior developer reviewing a pull request. The code changes are provided in unified diff format.

                Only include constructive criticism or suggestions for improvement.
                Do not include compliments, approvals, or praise (e.g., "good job", "well done").
                If a change looks fine, do not comment on it at all.

                Analyze the diff and return your review as a JSON array of threads. Each thread should include:
                - threadContext: with filePath, rightFileStart and rightFileEnd (line and offset)
                - comments: array of helpful review comments with content, parentCommentId: 0, and commentType: 1
                - status: set to "active"

                Use the diff to infer the line numbers. Only include comments that are relevant and helpful.

                ⚠️ OUTPUT FORMAT INSTRUCTIONS:
                Return only valid, raw JSON.
                Do NOT include markdown.
                Do NOT include code fences (no ```json or similar).
                Do NOT include any explanations, text, or formatting outside the JSON.

                Example:
                [
                  {
                    "threadContext": {
                      "filePath": "{{diff.FilePath}}",
                      "rightFileStart": { "line": 15, "offset": 1 },
                      "rightFileEnd": { "line": 15, "offset": 1 }
                    },
                    "status": "active",
                    "comments": [
                      {
                        "parentCommentId": 0,
                        "commentType": 1,
                        "content": "Avoid hardcoded values; use configuration."
                      }
                    ]
                  }
                ]

                Diff:

                {{diffContent}}
                """";

            var function = kernel.CreateFunctionFromPrompt(prompt);
            var result = await kernel.InvokeAsync(function);
            var tester = result.ToString();

            Console.WriteLine(tester);
            var threads = JsonSerializer.Deserialize<List<JsonElement>>(result.ToString() ?? "[]");

            var repoJsonDoc = JsonDocument.Parse(
                await GET(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoName}?api-version=7.1-preview.1"
                )
            );

            var repoId = repoJsonDoc.RootElement.GetProperty("id").GetString();

            foreach (var thread in threads)
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(thread),
                    Encoding.UTF8,
                    "application/json"
                );

                var bodySerialize = JsonSerializer.Serialize(thread);

                var response = await POST(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoId}/pullRequests/{pullRequestId}/threads?api-version=7.1-preview.1",
                    bodySerialize
                );

                Console.WriteLine(response);
            }
        }
    }

    #region Helpers

    private static HttpClient GetRetryHttpClient()
    {
        var retryPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: (retryAttempt, response, context) =>
                {
                    var retryAfter = response?.Result?.Headers?.RetryAfter?.Delta;
                    if (retryAfter.HasValue)
                    {
                        return retryAfter.Value + TimeSpan.FromSeconds(1);
                    }

                    // fallback
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
                    return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + jitter;
                },
                onRetryAsync: (outcome, timespan, retryAttempt, context) =>
                {
                    Console.WriteLine($"Retry {retryAttempt} for {outcome.Result?.StatusCode}");
                    return Task.CompletedTask;
                }
            );

        var handler = new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = new HttpClientHandler(),
        };

        return new HttpClient(handler);
    }

    public static Kernel GetSKChatCompletion(
        string azureAIEndpoint,
        string azureOpenAIKey,
        HttpClient httpClient
    )
    {
        return Kernel
            .CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: "gpt-4o",
                endpoint: azureAIEndpoint,
                apiKey: azureOpenAIKey,
                httpClient: httpClient
            )
            .Build();
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

    #endregion Helpers
}