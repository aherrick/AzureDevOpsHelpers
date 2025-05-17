![dotnet](https://github.com/aherrick/AzureDevOpsHelpers/actions/workflows/dotnet.yml/badge.svg)

# Azure DevOps Helper

A DevOps utility for working with Azure DevOps.

## Features

- Fetches all users.
- Retrieves project entitlements for each user.
- Exports user and project data to a CSV file.
- Retrieve the unified diff of files for a specified pull request.
- Retrieve open pull requests.

## Usage

Instantiate the `DataService` class with your organization name and personal access token (PAT):

```csharp
var dataService = new DataService("your-org-name", "your-personal-access-token");
await dataService.ExportUsersAndProjects();
