#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Infrastructure;
using Aspire.Hosting.Docker.Pipelines.Services;
using Aspire.Hosting.Docker.Pipelines.Utilities;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Register SSHConnectionManager as a keyed service (one per pipeline/resource)
        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<ISSHConnectionManager>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<SSHConnectionManager>>();
                return new SSHConnectionManager(logger);
            });

        // Register remote operation services as keyed services (one set per pipeline/resource)
        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteFileService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteFileService>>();
                return new RemoteFileService(sshConnectionManager, logger);
            });

        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteDockerEnvironmentService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteDockerEnvironmentService>>();
                return new RemoteDockerEnvironmentService(sshConnectionManager, logger);
            });

        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteDockerComposeService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteDockerComposeService>>();
                return new RemoteDockerComposeService(sshConnectionManager, logger);
            });

        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteDeploymentMonitorService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteDeploymentMonitorService>>();
                return new RemoteDeploymentMonitorService(sshConnectionManager, logger);
            });

        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteEnvironmentService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var environmentFileReader = serviceProvider.GetRequiredService<EnvironmentFileReader>();
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteEnvironmentService>>();
                return new RemoteEnvironmentService(sshConnectionManager, environmentFileReader, logger);
            });

        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton<IRemoteServiceInspectionService>(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var logger = serviceProvider.GetRequiredService<ILogger<RemoteServiceInspectionService>>();
                return new RemoteServiceInspectionService(sshConnectionManager, logger);
            });

        // Register DockerSSHPipeline as a keyed service, keyed on the Docker Compose environment resource
        // Injects all the high-level operation services
        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton(
            resourceBuilder.Resource,
            (serviceProvider, key) =>
            {
                var dockerCommandExecutor = serviceProvider.GetRequiredService<DockerCommandExecutor>();
                var environmentFileReader = serviceProvider.GetRequiredService<EnvironmentFileReader>();
                var sshConfigurationDiscovery = serviceProvider.GetRequiredService<SSHConfigurationDiscovery>();
                var pipelineOutputService = serviceProvider.GetRequiredService<IPipelineOutputService>();
                var sshConnectionManager = serviceProvider.GetRequiredKeyedService<ISSHConnectionManager>(key);
                var remoteFileService = serviceProvider.GetRequiredKeyedService<IRemoteFileService>(key);
                var remoteDockerEnvironmentService = serviceProvider.GetRequiredKeyedService<IRemoteDockerEnvironmentService>(key);
                var remoteDockerComposeService = serviceProvider.GetRequiredKeyedService<IRemoteDockerComposeService>(key);
                var remoteDeploymentMonitorService = serviceProvider.GetRequiredKeyedService<IRemoteDeploymentMonitorService>(key);
                var remoteEnvironmentService = serviceProvider.GetRequiredKeyedService<IRemoteEnvironmentService>(key);
                var remoteServiceInspectionService = serviceProvider.GetRequiredKeyedService<IRemoteServiceInspectionService>(key);

                return new DockerSSHPipeline(
                    (DockerComposeEnvironmentResource)key,
                    dockerCommandExecutor,
                    environmentFileReader,
                    pipelineOutputService,
                    sshConfigurationDiscovery,
                    sshConnectionManager,
                    remoteFileService,
                    remoteDockerEnvironmentService,
                    remoteDockerComposeService,
                    remoteDeploymentMonitorService,
                    remoteEnvironmentService,
                    remoteServiceInspectionService);
            });

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