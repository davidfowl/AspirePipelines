using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Docker SSH pipeline resources to a distributed application.
/// </summary>
public static class DockerPipelineExtensions
{
    /// <summary>
    /// Adds SSH deployment support to a Docker Compose environment resource, enabling deployment 
    /// of containerized applications to remote Docker hosts via SSH.
    /// This deployment pipeline is only active during publish mode and provides an interactive configuration
    /// experience for SSH connection settings and deployment targets.
    /// </summary>
    /// <param name="resourceBuilder">The Docker Compose environment resource builder.</param>
    /// <returns>The resource builder for method chaining.</returns>
    /// <remarks>
    /// The SSH deployment pipeline allows deploying Docker containers to remote hosts via SSH.
    /// It provides an interactive setup during publish that prompts for:
    /// - Target server hostname or IP address
    /// - SSH authentication credentials (username, password, or key-based authentication)
    /// - Remote deployment directory
    /// - SSH connection settings
    /// 
    /// This deployment pipeline is only active when publishing the application and has no effect during local development.
    /// </remarks>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithSshDeploySupport(
        this IResourceBuilder<DockerComposeEnvironmentResource> resourceBuilder)
    {
        // REVIEW: This needs to be disposed...
        var pipelineResource = new DockerSSHPipeline();
#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        return resourceBuilder.WithAnnotation(new DeployingCallbackAnnotation(pipelineResource.Deploy));
#pragma warning restore ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    }
}