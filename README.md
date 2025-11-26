# Aspire Docker SSH Deployment Pipeline

Deploy Aspire applications to remote hosts via SSH with a single command.

## Overview

This sample showcases how to build custom deployment pipelines for Aspire using the pipeline framework. The pipeline extends Docker Compose's built-in `prepare` step to push images to a registry and deploy them to remote servers via SSH.

**Key concepts demonstrated:**
- Step-based pipeline design with dependencies
- Integration with built-in Aspire pipeline steps
- Interactive configuration prompts
- Progress reporting via `IReportingStep`

## Installation

Add the CI build feed and install the package:

```bash
# Add the feedz.io source
dotnet nuget add source https://f.feedz.io/davidfowl/aspire/nuget/index.json --name davidfowl-aspire

# Install the package
dotnet add package Aspire.Hosting.Docker.SshDeploy --prerelease
```

CI builds are automatically published on every push to main and use the version format `0.1.0-ci.<build-number>+<branch>.<sha>`.

> **Note:** Stable releases will be published to NuGet.org when available.

## Quick Start

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

```bash
aspire deploy
```

## Configuration

Configuration is optional - the pipeline will prompt for any missing values. Pre-configure via `appsettings.json` to skip prompts:

```json
{
  "DockerSSH": {
    "SshHost": "your-server.com",
    "SshUsername": "your-username",
    "SshPort": "22",
    "SshKeyPath": "~/.ssh/id_rsa"
  },
  "DockerRegistry": {
    "RegistryUrl": "docker.io",
    "RepositoryPrefix": "your-docker-username"
  },
  "Deployment": {
    "RemoteDeployPath": "$HOME/aspire/apps/your-app"
  }
}
```

Clear cached configuration with `aspire deploy --clear-cache`.

## How It Works

The pipeline gathers all configuration inputs before building, then:

1. **Build** - Creates container images locally
2. **Push** - Tags and pushes images to registry
3. **Deploy** - Transfers files via SCP, runs `docker compose up` on remote host

Configuration steps declare `RequiredBy(WellKnownPipelineSteps.BuildPrereq)` to block builds until input gathering completes.

### Step Dependencies

```csharp
var pushImages = new PipelineStep {
    Name = $"push-images-{DockerComposeEnvironment.Name}",
    Action = PushImagesStep,
    DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"]
};
pushImages.DependsOn(configureRegistry);
```

## CI/CD with GitHub Actions

See `.github/workflows/deploy.yml` for a complete example. Key points:

- Uses GitHub Container Registry (ghcr.io) with built-in `GITHUB_TOKEN`
- Configuration via environment variables (e.g., `DockerSSH__TargetHost`, `Parameters__p`)
- Secrets for sensitive values (`TARGET_HOST`, `SSH_PRIVATE_KEY`, `CACHE_PASSWORD`)

```yaml
- name: Deploy
  run: aspire deploy
  env:
    DockerSSH__TargetHost: ${{ secrets.TARGET_HOST }}
    DockerSSH__SshKeyPath: ${{ github.workspace }}/.ssh/id_rsa
    DockerRegistry__RegistryUrl: ghcr.io
    DockerRegistry__RepositoryPrefix: ${{ github.repository_owner }}
    Parameters__p: hello
```

## Sample Project

See `samples/DockerPipelinesSample` for a complete example:

```bash
aspire run     # Run locally
aspire deploy  # Deploy to remote host
```
