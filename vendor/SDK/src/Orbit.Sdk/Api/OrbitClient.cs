using Orbit.Sdk.Api.Models;
using Orbit.Sdk.Api.GraphQL;

namespace Orbit.Sdk.Api;

/// <summary>
/// Main entry point for the ORBIT API.
/// Wraps the GraphQL client and provides typed access to projects, models, and versions.
/// </summary>
public class OrbitClient
{
    private readonly OrbitGraphQLClient _gql;
    public string ServerUrl { get; }

    public OrbitClient(string serverUrl, string authToken)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        _gql = new OrbitGraphQLClient(ServerUrl, authToken);
    }

    // ── User ────────────────────────────────────────────────────────────────

    public Task<OrbitUser?> GetActiveUserAsync(CancellationToken ct = default) =>
        _gql.QueryAsync<OrbitUser>(OrbitQueries.ActiveUser, "activeUser", ct);

    // ── Projects ─────────────────────────────────────────────────────────────

    public Task<List<OrbitProject>> GetProjectsAsync(CancellationToken ct = default) =>
        _gql.QueryListAsync<OrbitProject>(OrbitQueries.GetProjects, "activeUser.projects.items", ct);

    public Task<OrbitProject?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        _gql.QueryAsync<OrbitProject>(OrbitQueries.GetProject,
            "project", ct, new { id = projectId });

    public Task<OrbitProject> CreateProjectAsync(string name, string? description = null, CancellationToken ct = default) =>
        _gql.MutateAsync<OrbitProject>(OrbitQueries.CreateProject,
            "projectMutations.create", ct,
            new { input = new { name, description } });

    // ── Models ───────────────────────────────────────────────────────────────

    public Task<List<OrbitModel>> GetModelsAsync(string projectId, CancellationToken ct = default) =>
        _gql.QueryListAsync<OrbitModel>(OrbitQueries.GetModels,
            "project.models.items", ct, new { id = projectId });

    public Task<OrbitModel> CreateModelAsync(string projectId, string name, CancellationToken ct = default) =>
        _gql.MutateAsync<OrbitModel>(OrbitQueries.CreateModel,
            "modelMutations.create", ct,
            new { input = new { projectId, name } });

    // ── Versions ─────────────────────────────────────────────────────────────

    public Task<List<OrbitVersion>> GetVersionsAsync(string projectId, string modelId, CancellationToken ct = default) =>
        _gql.QueryListAsync<OrbitVersion>(OrbitQueries.GetVersions,
            "project.model.versions.items", ct, new { projectId, modelId });

    public Task<OrbitVersion> CreateVersionAsync(
        string projectId, string modelId, string objectId,
        string? message = null, string sourceApplication = "OrbitRhino",
        int totalChildrenCount = 0,
        CancellationToken ct = default) =>
        _gql.MutateAsync<OrbitVersion>(OrbitQueries.CreateVersion,
            "versionMutations.create", ct,
            new { input = new { projectId, modelId, objectId, message, sourceApplication, totalChildrenCount } });
}
