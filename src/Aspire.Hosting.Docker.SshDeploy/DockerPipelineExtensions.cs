#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Infrastructure;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Represents a file transfer mapping from a local path to a remote path on the deployment target.
/// </summary>
/// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
/// <param name="remotePath">The remote path provider that resolves to the destination directory.</param>
/// <param name="isRelativeToDeployPath">If true, the remote path is relative to RemoteDeployPath; otherwise it's an absolute path.</param>
public class FileTransferAnnotation(string localPath, IValueProvider remotePath, bool isRelativeToDeployPath) : IResourceAnnotation
{
    /// <summary>
    /// Gets the local path containing files to transfer.
    /// </summary>
    public string LocalPath { get; } = localPath;

    /// <summary>
    /// Gets the value provider for the remote destination path.
    /// </summary>
    public IValueProvider RemotePath { get; } = remotePath;

    /// <summary>
    /// Gets whether the remote path is relative to the deployment path.
    /// </summary>
    public bool IsRelativeToDeployPath { get; } = isRelativeToDeployPath;
}

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
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<SSHConfigurationDiscovery>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<GitHubActionsGeneratorService>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<ISshKeyDiscoveryService, SshKeyDiscoveryService>();

        // Register both SSH connection factory implementations
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<NativeSSHConnectionFactory>();
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<SSHNetConnectionFactory>();

        // Register the ISSHConnectionFactory interface - selects native ssh by default, SSH.NET as fallback
        resourceBuilder.ApplicationBuilder.Services.TryAddSingleton<ISSHConnectionFactory>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var useLegacy = config.GetValue<bool>("DockerSSH:UseLegacySshNet", false);

            if (useLegacy)
            {
                return sp.GetRequiredService<SSHNetConnectionFactory>();
            }

            return sp.GetRequiredService<NativeSSHConnectionFactory>();
        });

        // Register DockerSSHPipeline as a keyed service (one per resource)
        resourceBuilder.ApplicationBuilder.Services.AddKeyedSingleton(
            resourceBuilder.Resource,
            (sp, key) => new DockerSSHPipeline(
                (DockerComposeEnvironmentResource)key,
                sp.GetRequiredService<DockerCommandExecutor>(),
                sp.GetRequiredService<IPipelineOutputService>(),
                sp.GetRequiredService<ISSHConnectionFactory>(),
                sp.GetRequiredService<GitHubActionsGeneratorService>(),
                sp.GetRequiredService<ISshKeyDiscoveryService>(),
                sp.GetRequiredService<IProcessExecutor>(),
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

    /// <summary>
    /// Configures files to be transferred to the remote deployment directory via SCP.
    /// The remote path is relative to the configured RemoteDeployPath.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remoteSubPath">The subdirectory within RemoteDeployPath where files will be transferred.</param>
    /// <returns>The resource builder for method chaining.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithAppFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        string remoteSubPath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, ReferenceExpression.Create($"{remoteSubPath}"), isRelativeToDeployPath: true));
        return builder;
    }

    /// <summary>
    /// Configures files to be transferred to an absolute path on the remote deployment target via SCP.
    /// The remote directory will be created if it doesn't exist.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remotePath">A parameter resource that resolves to the absolute remote destination directory.</param>
    /// <returns>The resource builder for method chaining.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        IResourceBuilder<ParameterResource> remotePath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, remotePath.Resource, isRelativeToDeployPath: false));
        return builder;
    }

    /// <summary>
    /// Configures files to be transferred to an absolute path on the remote deployment target via SCP.
    /// The remote directory will be created if it doesn't exist.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remotePath">The absolute remote destination directory path (supports $HOME and ~ expansion).</param>
    /// <returns>The resource builder for method chaining.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        string remotePath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, ReferenceExpression.Create($"{remotePath}"), isRelativeToDeployPath: false));
        return builder;
    }
}