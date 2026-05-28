namespace Orbit.Sdk.Api.GraphQL;

/// <summary>GraphQL query/mutation strings for the ORBIT server API.</summary>
public static class OrbitQueries
{
    public const string ActiveUser = @"
        query {
            activeUser {
                id
                name
                email
                avatar
            }
        }";

    public const string GetProjects = @"
        query {
            activeUser {
                projects(limit: 50) {
                    items {
                        id
                        name
                        description
                        updatedAt
                    }
                }
            }
        }";

    public const string GetProject = @"
        query($id: String!) {
            project(id: $id) {
                id
                name
                description
                updatedAt
            }
        }";

    public const string CreateProject = @"
        mutation($input: ProjectCreateInput!) {
            projectMutations {
                create(input: $input) {
                    id
                    name
                    description
                }
            }
        }";

    public const string GetModels = @"
        query($id: String!) {
            project(id: $id) {
                models(limit: 50) {
                    items {
                        id
                        name
                        updatedAt
                    }
                }
            }
        }";

    public const string CreateModel = @"
        mutation($input: CreateModelInput!) {
            modelMutations {
                create(input: $input) {
                    id
                    name
                }
            }
        }";

    public const string GetVersions = @"
        query($projectId: String!, $modelId: String!) {
            project(id: $projectId) {
                model(id: $modelId) {
                    versions(limit: 20) {
                        items {
                            id
                            message
                            referencedObject
                            sourceApplication
                            createdAt
                        }
                    }
                }
            }
        }";

    public const string CreateVersion = @"
        mutation($input: CreateVersionInput!) {
            modelMutations {
                create(input: $input) {
                    id
                    message
                    referencedObject
                    createdAt
                }
            }
        }";
}
