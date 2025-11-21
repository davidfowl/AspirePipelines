# Aspire Docker SSH Deployment Pipeline

Deploy Aspire applications to remote hosts via SSH with a single command.

## Overview

This sample showcases how to build custom deployment pipelines for Aspire using the pipeline framework. The pipeline extends Docker Compose's built-in `prepare` step to push images to a registry and deploy them to remote servers via SSH.

**Key concepts demonstrated:**
- Step-based pipeline design with dependencies
- Integration with built-in Aspire pipeline steps
- Interactive configuration prompts
- Progress reporting via `IReportingStep`

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
    "RemoteDeployPath": "/opt/your-app"
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

## Sample Project

See `samples/DockerPipelinesSample` for a complete example:

```bash
aspire run     # Run locally
aspire deploy  # Deploy to remote host
```
