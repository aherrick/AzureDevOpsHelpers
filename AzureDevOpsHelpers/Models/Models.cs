using System.Text.Json;

namespace AzureDevOpsHelpers.Models;

public record User(string Id, string DisplayName, string Email);

public record FileUnifiedDiff(string FilePath, string UnifiedDiff);

public record PullRequestDto(
    JsonElement.ArrayEnumerator Changes,
    string RepositoryId,
    string BaseCommitId,
    string TargetCommitId
);

public class PullRequest
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Status { get; set; }
    public string ProjectName { get; set; }
    public string RepositoryName { get; set; }
    public List<string> ApprovedReviewers { get; set; }
}