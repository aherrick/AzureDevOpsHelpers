using AzureDevOpsHelpers.Services;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

var ds = new DataService(config["org"], config["pat"]);

//await ds.GetOpenPullRequests("", config["azureAIEndpoint"], config["azureAIAPIKey"]);

var prId = 12242;
var project = "Supply Chain Cloud";

var yo = await ds.GetComments(project, prId);

//var yo = await ds.GetUniffedDiffForPullRequest(project: project, prId);

//await ds.AddAICommentsToPullRequest(
//    fileUnifiedDiffs: yo,
//    pullRequestId: prId,
//    project: project,
//    config["azureAIEndpoint"],
//    config["azureAIAPIKey"],
//    config["azureAIModel"]
//);

;