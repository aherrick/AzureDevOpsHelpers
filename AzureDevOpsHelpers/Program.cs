using AzureDevOpsHelpers.Services;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

var ds = new DataService(config["org"], config["pat"]);

//await ds.GetOpenPullRequests("", config["azureAIEndpoint"], config["azureAIAPIKey"]);

var yo = await ds.GetUniffedDiffForPullRequest(
    project: "Supply Chain Cloud",
    repoName: "SupplyChainCloud",
    11768
);

await ds.AddAICommentsToPullRequest(
    fileUnifiedDiffs: yo,
    pullRequestId: 11768,
    project: "Supply Chain Cloud",
    repoName: "SupplyChainCloud",
    config["azureAIEndpoint"],
    config["azureAIAPIKey"],
    config["azureAIModel"]
);

;