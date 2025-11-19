# Aspire Docker SSH Deployment Pipeline

Deploy .NET Aspire applications to remote hosts via SSH with a single command. This sample demonstrates future deployment pipeline patterns for Aspire.

https://github.com/user-attachments/assets/d7bab501-f043-4058-a188-ccd1e0096a05

## Usage

### Development Machine Requirements

- **.NET 9 SDK** - https://dotnet.microsoft.com/en-us/download
- **Docker Desktop** or Docker CLI - https://www.docker.com/get-started/
- **SSH client** (typically included with Windows 10/11, macOS, and Linux)
- **Aspire CLI**: [Install Guide](https://learn.microsoft.com/en-us/dotnet/aspire/cli/install#install-as-a-native-executable)

#### Set the feature flag

```bash
aspire config set -g features.deployCommandEnabled true 
```

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
- Container registry integration (Docker Hub, GitHub Container Registry, etc.)
- Automatic image tagging with timestamps
- Interactive configuration prompts
- Integration with appsettings.json and environment variables
- Built-in Aspire dashboard deployment with automatic token extraction
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

1. **Docker**: Must be installed on both local development machine and target deployment host
2. **SSH Access**: SSH connectivity to the target deployment host

### Target Host Requirements

The remote deployment host must have:

- **Docker installed and running**
- **SSH server configured and accessible**
- **User account with Docker permissions** (user should be in the `docker` group)
- **Network connectivity** for pulling Docker images from the container registry
- **Access to the configured container registry** (Docker Hub, GitHub Container Registry, etc.)

## How It Works

### Deployment Process

The deployment pipeline executes the following steps:

1. **Configuration Resolution**: Reads deployment settings from configuration sources and prompts for missing values
2. **Build & Prepare**: Builds Docker images for all containerized projects and generates deployment files (docker-compose.yaml, .env files)
3. **Registry Push**: Tags images with timestamps and pushes them to the configured container registry
4. **SSH Connection**: Establishes secure SSH connection to the target deployment host
5. **Environment Preparation**: Verifies Docker installation and prepares deployment directory on remote host
6. **File Transfer**: Transfers docker-compose.yaml and merged environment file to remote host
7. **Container Deployment**: Pulls images from registry and starts containers using docker compose
8. **Dashboard Access**: Extracts and displays Aspire dashboard login token from deployment logs

### Pipeline Architecture

The SSH deployment pipeline consists of multiple coordinated steps:

**Setup Steps:**
- `ssh-prereq-env`: Verifies Docker prerequisites
- `prepare-ssh-context-env`: Prepares SSH connection context
- `configure-registry-env`: Configures container registry credentials

**Build & Push Steps:**
- `prepare-env`: Builds images and generates docker-compose files (built-in Aspire step)
- `push-images-env`: Tags and pushes images to container registry

**Remote Deployment Steps:**
- `establish-ssh-env`: Establishes SSH connection to target host
- `prepare-remote-env`: Verifies Docker installation on remote host
- `merge-environment-env`: Merges environment files with registry image tags
- `transfer-files-env`: Transfers docker-compose.yaml and .env to remote host
- `docker-via-ssh-env`: Deploys containers on remote host
- `extract-dashboard-token-env`: Extracts Aspire dashboard login token
- `cleanup-ssh-env`: Closes SSH connections

**Key Components:**
- **`DockerSSHPipeline`**: Core pipeline implementation
- **`WithSshDeploySupport`**: Extension method that registers the pipeline
- **`IInteractionService`**: Handles interactive configuration prompts
- **SSH.NET**: Secure SSH/SCP communication library

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
