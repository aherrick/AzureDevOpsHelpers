using AzureDevOpsHelpers.Services;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

var ds = new DataService(config["org"], config["pat"]);

//await ds.GetOpenPullRequests("", config["azureAIEndpoint"], config["azureAIAPIKey"]);

var yo = await ds.GetUniffedDiffForPullRequest("", "", 0);

await ds.AddAICommentsToPullRequest(
    fileUnifiedDiffs: yo,
    pullRequestId: 0,
    project: "",
    repoName: "",
    config["azureAIEndpoint"],
    config["azureAIAPIKey"]
);

;