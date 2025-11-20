using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Infrastructure;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        // Register infrastructure services
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<IProcessExecutor, ProcessExecutor>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<IFileSystem, FileSystemAdapter>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<Docker.Pipelines.Utilities.DockerCommandExecutor>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<Docker.Pipelines.Utilities.EnvironmentFileReader>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<Docker.Pipelines.Utilities.SSHConfigurationDiscovery>();

        // Create a factory that will resolve dependencies from the service provider
        DockerSSHPipeline? pipelineResource = null;

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        return resourceBuilder.WithPipelineStepFactory(context =>
        {
            // Lazy create the pipeline resource with resolved dependencies
            if (pipelineResource == null)
            {
                var dockerCommandExecutor = context.PipelineContext.Services.GetRequiredService<Docker.Pipelines.Utilities.DockerCommandExecutor>();
                var environmentFileReader = context.PipelineContext.Services.GetRequiredService<Docker.Pipelines.Utilities.EnvironmentFileReader>();
                var sshConfigurationDiscovery = context.PipelineContext.Services.GetRequiredService<Docker.Pipelines.Utilities.SSHConfigurationDiscovery>();

                pipelineResource = new DockerSSHPipeline(
                    resourceBuilder.Resource,
                    dockerCommandExecutor,
                    environmentFileReader,
                    sshConfigurationDiscovery);
            }

            return pipelineResource.CreateSteps(context);
        })
        .WithPipelineConfiguration(async context =>
        {
            // Lazy create the pipeline resource with resolved dependencies if not already created
            if (pipelineResource == null)
            {
                var dockerCommandExecutor = context.Services.GetRequiredService<Docker.Pipelines.Utilities.DockerCommandExecutor>();
                var environmentFileReader = context.Services.GetRequiredService<Docker.Pipelines.Utilities.EnvironmentFileReader>();
                var sshConfigurationDiscovery = context.Services.GetRequiredService<Docker.Pipelines.Utilities.SSHConfigurationDiscovery>();

                pipelineResource = new DockerSSHPipeline(
                    resourceBuilder.Resource,
                    dockerCommandExecutor,
                    environmentFileReader,
                    sshConfigurationDiscovery);
            }

            await pipelineResource.ConfigurePipelineAsync(context);
        });
#pragma warning restore ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    }
}