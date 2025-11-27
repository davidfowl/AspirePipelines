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
    private readonly EnvironmentFileReader _environmentFileReader;
    private readonly ILoggerFactory _loggerFactory;

    // Cached service instances
    private IRemoteFileService? _fileService;
    private IRemoteDockerEnvironmentService? _dockerEnvironmentService;
    private IRemoteDockerComposeService? _dockerComposeService;
    private IRemoteEnvironmentService? _environmentService;
    private IRemoteServiceInspectionService? _serviceInspectionService;

    public RemoteOperationsFactory(
        ISSHConnectionManager sshConnectionManager,
        EnvironmentFileReader environmentFileReader,
        ILoggerFactory loggerFactory)
    {
        _sshConnectionManager = sshConnectionManager;
        _environmentFileReader = environmentFileReader;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets the SSH connection manager used by all services created by this factory.
    /// </summary>
    public ISSHConnectionManager SshConnectionManager => _sshConnectionManager;

    public IRemoteFileService FileService =>
        _fileService ??= new RemoteFileService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteFileService>());

    public IRemoteDockerEnvironmentService DockerEnvironmentService =>
        _dockerEnvironmentService ??= new RemoteDockerEnvironmentService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteDockerEnvironmentService>());

    public IRemoteDockerComposeService DockerComposeService =>
        _dockerComposeService ??= new RemoteDockerComposeService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteDockerComposeService>());

    public IRemoteEnvironmentService EnvironmentService =>
        _environmentService ??= new RemoteEnvironmentService(_sshConnectionManager, _environmentFileReader, _loggerFactory.CreateLogger<RemoteEnvironmentService>());

    public IRemoteServiceInspectionService ServiceInspectionService =>
        _serviceInspectionService ??= new RemoteServiceInspectionService(_sshConnectionManager, _loggerFactory.CreateLogger<RemoteServiceInspectionService>());
}
