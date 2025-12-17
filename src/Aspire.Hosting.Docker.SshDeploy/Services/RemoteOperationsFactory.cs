using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Factory for creating remote operation services tied to a specific SSH connection.
/// Services are lazily created and cached for the lifetime of the factory.
/// </summary>
internal class RemoteOperationsFactory
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly IProcessExecutor _processExecutor;
    private readonly ILoggerFactory _loggerFactory;

    // Cached service instances
    private IRemoteFileService? _fileService;
    private IRemoteDockerEnvironmentService? _dockerEnvironmentService;
    private IRemoteDockerComposeService? _dockerComposeService;
    private IRemoteServiceInspectionService? _serviceInspectionService;

    public RemoteOperationsFactory(
        ISSHConnectionManager sshConnectionManager,
        IProcessExecutor processExecutor,
        ILoggerFactory loggerFactory)
    {
        _sshConnectionManager = sshConnectionManager;
        _processExecutor = processExecutor;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets the SSH connection manager used by all services created by this factory.
    /// </summary>
    public ISSHConnectionManager SshConnectionManager => _sshConnectionManager;

    public IRemoteFileService FileService =>
        _fileService ??= new RemoteFileService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteFileService>());

    public IRemoteDockerEnvironmentService DockerEnvironmentService =>
        _dockerEnvironmentService ??= new RemoteDockerEnvironmentService(_sshConnectionManager, _processExecutor, _loggerFactory.CreateLogger<RemoteDockerEnvironmentService>());

    public IRemoteDockerComposeService DockerComposeService =>
        _dockerComposeService ??= new RemoteDockerComposeService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteDockerComposeService>());

    public IRemoteServiceInspectionService ServiceInspectionService =>
        _serviceInspectionService ??= new RemoteServiceInspectionService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteServiceInspectionService>());
}
