# Aspire Docker SSH Deployment Pipeline

Deploy .NET Aspire applications to remote hosts via SSH with a single command. This sample demonstrates future deployment pipeline patterns for Aspire.

## Usage

### Basic Usage

Add SSH deployment support to your Aspire app host:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.DockerPipelinesSample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.DockerPipelinesSample_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
```

### Running Deployment

Deploy your application using the Aspire CLI:

```bash
# Deploy with interactive prompts
aspire deploy
```

## Overview

Sample implementation showcasing configurable deployment pipelines that can be shared as NuGet packages, prompt for values, and integrate with IConfiguration.

## Features

- SSH-based Docker deployment to remote hosts
- Interactive configuration prompts
- Integration with appsettings.json and environment variables
- Configurable deployment paths and Docker registries

## Configuration

### Interactive Prompts

When deploying, the pipeline will prompt for required values if they're not already configured:

- **SSH Host**: The target deployment server hostname/IP
- **SSH Username**: Username for SSH authentication
- **SSH Port**: SSH port (defaults to 22)
- **SSH Key Path**: Path to SSH private key file
- **Remote Deploy Path**: Target directory on remote host (optional, defaults to `/home/{username}/aspire-app`)
- **Registry URL**: Docker registry URL (e.g., docker.io)
- **Repository Prefix**: Docker repository prefix/username

### Configuration File

You can pre-configure deployment settings in your `appsettings.json`:

```json
{
  "DockerSSH": {
    "SshHost": "your-server.com",
    "SshUsername": "your-username",
    "SshPort": "22",
    "SshKeyPath": "path/to/your/private-key",
    "RemoteDeployPath": "/opt/your-app",
    "RegistryUrl": "docker.io",
    "RepositoryPrefix": "your-docker-username"
  }
}
```

### Environment Variables

Alternatively, use environment variables:

```bash
DOCKERSSH__SSHHOST=your-server.com
DOCKERSSH__SSHUSERNAME=your-username
DOCKERSSH__SSHPORT=22
DOCKERSSH__SSHKEYPATH=path/to/your/private-key
DOCKERSSH__REMOTEDEPLOYPATH=/opt/your-app
DOCKERSSH__REGISTRYURL=docker.io
DOCKERSSH__REPOSITORYPREFIX=your-docker-username
```

## Requirements

### Prerequisites

1. **.NET Aspire 9.4 CLI (Nightly Build)**: Required for deployment functionality
   - Follow the installation guide: https://github.com/dotnet/aspire/blob/main/docs/using-latest-daily.md#install-the-daily-cli
2. **.NET 8 SDK**: Required for building and running Aspire applications
3. **Docker**: Must be installed on both local development machine and target deployment host
4. **SSH Access**: SSH connectivity to the target deployment host

### Target Host Requirements

The remote deployment host must have:

- **Docker installed and running**
- **SSH server configured and accessible**
- **User account with Docker permissions** (user should be in the `docker` group)
- **Network connectivity** for downloading Docker images (if not transferred locally)

### Development Machine Requirements

- **.NET 8 SDK**
- **Docker Desktop** or Docker CLI
- **SSH client** (typically included with Windows 10/11, macOS, and Linux)
- **Aspire 9.4 CLI (Nightly)**: Follow the installation guide at https://github.com/dotnet/aspire/blob/main/docs/using-latest-daily.md#install-the-daily-cli

## How It Works

### Deployment Process

1. **Configuration Resolution**: Reads deployment settings from configuration sources and prompts for missing values
2. **Docker Image Building**: Builds Docker images for all containerized projects in the Aspire application
3. **Image Transfer**: Transfers Docker images to the remote host via SSH
4. **Container Deployment**: Deploys and starts containers on the remote host
5. **Service Coordination**: Ensures proper startup order and inter-service communication

### Pipeline Architecture

The SSH deployment pipeline consists of:

- **`DockerSSHPipeline`**: Core resource that manages the SSH deployment process
- **`WithSshDeploySupport`**: Extension method that registers the pipeline with the Aspire application
- **Configuration providers**: Handle interactive prompts and settings resolution
- **SSH client**: Manages secure communication with the remote host

## Sample Project

The `samples/DockerPipelinesSample` directory contains a complete example showing:

- **AppHost**: Aspire application host with SSH deployment enabled
- **ApiService**: Sample API service for deployment
- **Web**: Sample web application
- **ServiceDefaults**: Shared service configuration

To run the sample:

```bash
aspire run
```

To deploy the sample:

```bash
aspire deploy
```

## Customization

The `WithSshDeploySupport` extension method currently doesn't accept parameters. All configuration is handled through the configuration system (appsettings.json, environment variables, or interactive prompts).

## Security Considerations

- **SSH Keys**: Consider using SSH key-based authentication instead of passwords
- **Secrets Management**: Use secure configuration providers for sensitive data
- **Network Security**: Ensure SSH connections are properly secured
- **Container Security**: Follow Docker security best practices for deployed containers

## Future Enhancements

This sample implementation demonstrates the foundation for more advanced deployment pipeline features:

- **Multiple deployment targets**: Azure, AWS, Kubernetes, etc.
- **CI/CD integration**: GitHub Actions, Azure DevOps, etc.
- **Advanced orchestration**: Service mesh integration, load balancing
- **Monitoring integration**: Health checks, logging, metrics collection
- **Advanced SSH deployment scenarios**: multi-host deployments, blue-green deployment strategies, health check integration, rollback capabilities
