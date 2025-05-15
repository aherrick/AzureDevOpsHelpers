using System.Text.Json;

namespace AzureDevOpsHelpers.Models;

public record User(string Id, string DisplayName, string Email);

public record FileUnifiedDiff(string FilePath, string UnifiedDiff);

public record PullRequestDto(JsonElement.ArrayEnumerator Changes, string RepositoryId, string BaseCommitId, string TargetCommitId)