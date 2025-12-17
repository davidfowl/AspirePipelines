#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding credential-based login to container registries.
/// </summary>
public static class ContainerRegistryLoginExtensions
{
    /// <summary>
    /// Adds a login step to the container registry that authenticates using username/password credentials.
    /// The login step runs before any push operations (required by push-prereq).
    /// </summary>
    /// <param name="builder">The container registry resource builder.</param>
    /// <param name="username">The registry username (can be a parameter resource).</param>
    /// <param name="password">The registry password (can be a parameter resource).</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<ContainerRegistryResource> WithCredentialsLogin(
        this IResourceBuilder<ContainerRegistryResource> builder,
        IResourceBuilder<ParameterResource> username,
        IResourceBuilder<ParameterResource> password)
    {
        return builder.WithCredentialsLogin(username.Resource, password.Resource);
    }

    /// <summary>
    /// Adds a login step to the container registry that authenticates using username/password credentials.
    /// The login step runs before any push operations (required by push-prereq).
    /// </summary>
    /// <param name="builder">The container registry resource builder.</param>
    /// <param name="username">The registry username parameter.</param>
    /// <param name="password">The registry password parameter.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<ContainerRegistryResource> WithCredentialsLogin(
        this IResourceBuilder<ContainerRegistryResource> builder,
        ParameterResource username,
        ParameterResource password)
    {
        var registry = builder.Resource;
        var name = registry.Name;

        // Store credentials as annotations for the login helper to retrieve
        builder.WithAnnotation(new ContainerRegistryCredentialsAnnotation(username, password));

        // Add the login pipeline step (matching ACR's pattern)
        builder.WithAnnotation(new PipelineStepAnnotation(context =>
        {
            var loginStep = new PipelineStep
            {
                Name = $"login-to-registry-{name}",
                Action = ctx => ContainerRegistryLoginHelpers.LoginToRegistryAsync(registry, ctx),
                Tags = ["registry-login"],
                DependsOnSteps = [WellKnownPipelineSteps.ProcessParameters],
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
                Resource = registry
            };
            return loginStep;
        }));

        return builder;
    }

}

/// <summary>
/// Annotation that stores credentials for a container registry using parameters.
/// Parameters can hold either literal values or be prompted for during deployment.
/// </summary>
internal sealed class ContainerRegistryCredentialsAnnotation(
    ParameterResource username,
    ParameterResource password) : IResourceAnnotation
{
    public ParameterResource Username { get; } = username;
    public ParameterResource Password { get; } = password;
}
