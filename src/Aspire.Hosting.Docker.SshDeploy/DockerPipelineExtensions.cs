#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Infrastructure;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        // Register infrastructure services (shared across all environments)
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<IProcessExecutor, ProcessExecutor>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<IFileSystem, FileSystemAdapter>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<DockerCommandExecutor>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<EnvironmentFileReader>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<SSHConfigurationDiscovery>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<SSHConnectionFactory>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<DockerRegistryService>();

        // Register DockerSSHPipeline as a keyed service (one per resource)
        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton(
            resourceBuilder.Resource,
            (sp, key) => new DockerSSHPipeline(
                (DockerComposeEnvironmentResource)key,
                sp.GetRequiredService<DockerCommandExecutor>(),
                sp.GetRequiredService<EnvironmentFileReader>(),
                sp.GetRequiredService<IPipelineOutputService>(),
                sp.GetRequiredService<SSHConnectionFactory>(),
                sp.GetRequiredService<DockerRegistryService>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return resourceBuilder.WithPipelineStepFactory(context =>
        {
            var pipeline = context.PipelineContext.Services.GetRequiredKeyedService<DockerSSHPipeline>(resourceBuilder.Resource);
            return pipeline.CreateSteps(context);
        })
        .WithPipelineConfiguration(context =>
        {
            var pipeline = context.Services.GetRequiredKeyedService<DockerSSHPipeline>(resourceBuilder.Resource);
            return pipeline.ConfigurePipelineAsync(context);
        });
    }
}