using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using AzureDevOpsHelpers.Models;

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

                var response = await MakeApiCall(uriBuilder.ToString());
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
        var response = await MakeApiCall(url);
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

    private async Task<JsonDocument> MakeApiCall(string url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"))
        );

        HttpResponseMessage response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine(
                $"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}"
            );
            return null;
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}