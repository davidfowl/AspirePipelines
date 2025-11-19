# Aspire Docker SSH Deployment Pipeline

Deploy Aspire applications to remote hosts via SSH with a single command. This project demonstrates a step-based deployment pipeline that integrates with Aspire's built-in pipeline system.

## Overview

This sample showcases how to build custom deployment pipelines for Aspire using the pipeline framework. The pipeline builds on top of Docker Compose's built-in `prepare` step (which generates docker-compose.yaml and .env files) and adds additional steps to push images to a registry and deploy them to remote servers via SSH.

**Key concepts demonstrated:**
- **Step-based pipeline design**: Each deployment task is a discrete pipeline step
- **Step dependencies**: Steps declare dependencies on other steps for proper ordering
- **Built-in step integration**: Custom steps depend on Docker Compose's built-in `prepare-env` step to generate deployment files
- **Interactive configuration**: Pipelines can prompt for missing configuration values
- **Progress reporting**: Steps report progress using `IReportingStep`

## Usage

### Development Machine Requirements

- **.NET 10 SDK** - https://dotnet.microsoft.com/en-us/download
- **Docker Desktop** or Docker CLI - https://www.docker.com/get-started/
- **Aspire CLI**: [Install Guide](https://aspire.dev/docs/cli/install)

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

### Deployment State

Configuration values (SSH connection info, registry credentials) are stored in deployment state after the first deployment. This means you won't be prompted for these values on subsequent deployments.

To clear stored configuration and be prompted again:

```bash
aspire deploy --clear-cache
```

This is useful when you need to:
- Deploy to a different server
- Use different registry credentials
- Change deployment configuration settings

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

### Step-Based Pipeline Architecture

This deployment pipeline is built using Aspire's step-based pipeline framework. Each step represents a discrete unit of work with clear dependencies on other steps. The pipeline execution engine automatically orders steps based on their dependencies and runs independent steps in parallel for optimal performance.

**Building on Docker Compose's `prepare` step:**
Docker Compose environments include a built-in `prepare-env` step that handles image building and generates docker-compose.yaml and .env files. This SSH deployment pipeline extends that foundation by adding steps to:
1. Push the built images to a container registry
2. Update the .env file with registry image references
3. Transfer deployment files to a remote server via SSH
4. Run `docker compose up` on the remote server to pull and deploy the images

#### Pipeline Step Execution Flow

When you run `aspire deploy`, the pipeline executes steps in the following order:

**Level 0** - Parallel execution of independent setup steps:
- `ssh-prereq-env`: Verifies Docker prerequisites on local machine
- `configure-registry-env`: Prompts for and configures container registry credentials
- `deploy-prereq`: Initializes deployment state
- `prepare-ssh-context-env`: Gathers SSH connection information
- `publish-env`: Generates docker-compose.yaml structure

**Level 1** - Parallel build and SSH connection:
- `build-apiservice`, `build-webfrontend`: Build container images for each project
- `establish-ssh-env`: Establishes SSH/SCP connection to target host
- `publish`: Completes publish process

**Level 2** - Parallel preparation:
- `build`: Aggregates individual build steps
- `prepare-remote-env`: Verifies Docker on remote host and prepares deployment directory

**Level 3** - Prepare deployment files:
- `prepare-env`: Built-in Aspire step that builds images and generates `.env.Production` and `docker-compose.yaml` files

**Level 4** - Push to registry:
- `push-images-env`: Tags images with timestamps and pushes to container registry

**Level 5** - Merge configuration:
- `merge-environment-env`: Updates environment file with registry image tags

**Level 6** - Transfer files:
- `transfer-files-env`: Transfers docker-compose.yaml and .env to remote host via SCP

**Level 7** - Deploy:
- `docker-via-ssh-env`: Runs `docker compose` on remote host to deploy containers

**Level 8** - Extract token:
- `extract-dashboard-token-env`: Retrieves Aspire dashboard login token from container logs

**Level 9** - Cleanup:
- `cleanup-ssh-env`: Closes SSH/SCP connections

**Level 10-11** - Complete:
- `deploy-docker-ssh-env`: Final coordination step
- `deploy`: Overall deployment completion

### Step Dependencies

Steps declare dependencies using:
- **`DependsOn(step)`**: Depends on a specific step instance
- **`DependsOnSteps`**: Depends on steps by name (for built-in steps)
- **`RequiredBy(step)`**: Marks this step as required by another step

Example from the code:
```csharp
var pushImages = new PipelineStep {
    Name = $"push-images-{DockerComposeEnvironment.Name}",
    Action = PushImagesStep,
    DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"] // Depends on built-in step
};
pushImages.DependsOn(configureRegistry); // Depends on our custom step

var mergeEnv = new PipelineStep {
    Name = $"merge-environment-{DockerComposeEnvironment.Name}",
    Action = MergeEnvironmentFileStep
};
mergeEnv.DependsOn(prepareRemote);
mergeEnv.DependsOn(pushImages); // Waits for both remote prep AND image push
```

### Key Components

- **`DockerSSHPipeline`**: Implements `IPipelineStepProvider` to register custom steps
- **`PipelineStep`**: Represents a unit of work with name, action, and dependencies
- **`IReportingStep`**: Provides progress reporting with tasks and status updates
- **`IInteractionService`**: Handles interactive prompts for missing configuration
- **`PipelineStepContext`**: Provides logger, cancellation token, and reporting step to each step action

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

## Extending the Pipeline

This sample demonstrates the foundation for building custom deployment pipelines. You can extend it by creating new `PipelineStep` instances with custom actions and declaring dependencies on other steps (including built-in steps like `prepare-env`). See the source code in `DockerSSHPipeline.cs` for examples of step creation and dependency composition.
