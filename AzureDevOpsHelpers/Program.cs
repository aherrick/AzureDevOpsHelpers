using AzureDevOpsHelpers.Services;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

var ds = new DataService(config["org"], config["pat"]);

await ds.GetOpenPullRequests("", config["azureAIEndpoint"], config["azureAIAPIKey"]);