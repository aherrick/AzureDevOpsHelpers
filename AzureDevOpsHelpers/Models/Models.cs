namespace AzureDevOpsHelpers.Models;

public record User(string Id, string DisplayName, string Email);

public record FileUnifiedDiff(string FilePath, string UnifiedDiff);