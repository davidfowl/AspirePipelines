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
- **Aspire CLI** - https://aspire.dev/get-started/install-cli/

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

The deployment pipeline uses three separate configuration sections for different concerns:

- **`DockerSSH`**: SSH connection settings (host, username, port, key path)
- **`DockerRegistry`**: Container registry settings (URL, repository prefix, credentials)
- **`Deployment`**: Deployment-specific settings (remote path)

This separation ensures clear boundaries between connection configuration, registry configuration, and deployment configuration.

### Interactive Prompts

When deploying, the pipeline will prompt for required values if they're not already configured. Prompts are organized by concern:

**SSH Connection Configuration:**
- **SSH Host**: The target deployment server hostname/IP
- **SSH Username**: Username for SSH authentication
- **SSH Port**: SSH port (defaults to 22)
- **SSH Authentication Method**: SSH private key path or password authentication

**Container Registry Configuration:**
- **Registry URL**: Docker registry URL (e.g., docker.io, ghcr.io)
- **Repository Prefix**: Docker repository prefix/namespace
- **Registry Username**: Optional username for registry authentication
- **Registry Password**: Optional password/token for registry authentication

**Deployment Configuration:**
- **Remote Deploy Path**: Target directory on remote host (defaults to `/opt/{appname}`)

### Configuration File

You can pre-configure deployment settings in your `appsettings.json`:

```json
{
  "DockerSSH": {
    "SshHost": "your-server.com",
    "SshUsername": "your-username",
    "SshPort": "22",
    "SshKeyPath": "path/to/your/private-key"
  },
  "DockerRegistry": {
    "RegistryUrl": "docker.io",
    "RepositoryPrefix": "your-docker-username",
    "RegistryUsername": "your-registry-username",
    "RegistryPassword": "your-registry-password"
  },
  "Deployment": {
    "RemoteDeployPath": "/opt/your-app"
  }
}
```

### Environment Variables

Alternatively, use environment variables:

```bash
# SSH Configuration
DOCKERSSH__SSHHOST=your-server.com
DOCKERSSH__SSHUSERNAME=your-username
DOCKERSSH__SSHPORT=22
DOCKERSSH__SSHKEYPATH=path/to/your/private-key

# Docker Registry Configuration
DOCKERREGISTRY__REGISTRYURL=docker.io
DOCKERREGISTRY__REPOSITORYPREFIX=your-docker-username
DOCKERREGISTRY__REGISTRYUSERNAME=your-registry-username
DOCKERREGISTRY__REGISTRYPASSWORD=your-registry-password

# Deployment Configuration
DEPLOYMENT__REMOTEDEPLOYPATH=/opt/your-app
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
Docker Compose environments include a built-in `prepare-env` step that handles image building and generates docker-compose.yaml and environment-specific .env files (e.g., `.env.Production`, `.env.Development`). This SSH deployment pipeline extends that foundation by adding steps to:
1. Push the built images to a container registry
2. Update the environment-specific .env file with registry image references
3. Transfer deployment files to a remote server via SSH
4. Run `docker compose up` on the remote server to pull and deploy the images

#### Pipeline Step Execution Flow

When you run `aspire deploy`, the pipeline executes steps in the following order.

**Key Design Principle: Fail-Fast Input Gathering**

All interactive prompts for user input (SSH credentials, registry settings, deployment path) are gathered at the beginning of the pipeline before any expensive operations like building images, pushing to registries, or deploying. This ensures users aren't interrupted mid-deployment and prevents wasted work if they cancel during input prompts.

**Implementation:** The pipeline uses an elegant dependency chain where configuration steps declare themselves as `RequiredBy("build-prereq")`. Since build steps already depend on `build-prereq` (from the framework), this creates an automatic chain: config steps → `build-prereq` → build steps. No direct modification of build steps needed!

**Pipeline Execution Flow:**

1. **Initialize state and gather inputs** (all run in parallel):
   - `deploy-prereq`: Initializes deployment state
   - `publish-env`: Generates docker-compose.yaml structure
   - `establish-ssh-env`: Establishes SSH/SCP connection, prompts for SSH credentials if needed
   - `configure-registry-env`: Prompts for and configures container registry credentials
   - `ssh-prereq-env`: Verifies Docker prerequisites on local machine

2. **Complete input gathering**:
   - `configure-deployment-env`: Prompts for deployment path configuration (depends on SSH)
   - `publish`: Completes publish process

3. **Prerequisites ready** (waits for ALL input via RequiredBy chain):
   - `build-prereq`: Build prerequisites check (automatically waits for config steps via RequiredBy)
   - `prepare-remote-env`: Verifies Docker on remote host and prepares deployment directory

4. **Build images** (depends on build-prereq, which waited for all config):
   - `build-apiservice`, `build-webfrontend`: Build container images for each project
   - `build`: Aggregates individual build steps

5. **Prepare environment**:
   - `prepare-env`: Built-in Aspire step that generates environment-specific `.env` files (e.g., `.env.Production`) and `docker-compose.yaml`

6. **Push to registry**:
   - `push-images-env`: Tags images with timestamps and pushes to container registry

7. **Merge configuration**:
   - `merge-environment-env`: Updates environment file with registry image tags

8. **Transfer files**:
   - `transfer-files-env`: Transfers docker-compose.yaml and .env to remote host via SCP

9. **Deploy**:
   - `remote-docker-deploy-env`: Runs `docker compose` on remote host to deploy containers

10. **Extract token**:
    - `extract-dashboard-token-env`: Retrieves Aspire dashboard login token from container logs

11. **Cleanup**:
    - `cleanup-ssh-env`: Closes SSH/SCP connections

12. **Complete**:
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

### Logging and Output

The pipeline uses structured logging with appropriate log levels for different types of information:

- **Task Success Messages**: High-level step completion status (e.g., "SSH connection established")
- **INF (Information)**: Important deployment information like service URLs, health check summaries, and configuration details
- **DBG (Debug)**: Detailed operational information like individual service health status, command execution details, and intermediate steps

This layered approach keeps the default output clean while providing detailed diagnostic information when needed (via `--verbosity debug` or log files).

**Example output structure:**
- SSH connection: Single success message (detailed connection tests at DEBUG level)
- Health checks: Summary at INFO level (individual service status at DEBUG level)
- Service URLs: Formatted table at INFO level with deployment summary
- Remote environment preparation: Single success message (Docker version, permissions at DEBUG level)

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
