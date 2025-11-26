# Aspire.Hosting.Docker.SshDeploy

Deploy .NET Aspire applications to remote Docker hosts via SSH.

## Installation

```bash
dotnet add package Aspire.Hosting.Docker.SshDeploy
```

## Usage

Add SSH deployment support to your Aspire AppHost:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

// Add your services
var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.MyApi>("api")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.MyWeb>("web")
    .WithExternalHttpEndpoints()
    .WithReference(cache)
    .WithReference(apiService);

builder.Build().Run();
```

Then deploy with:

```bash
aspire deploy
```

## Configuration

The pipeline will prompt for missing configuration. Pre-configure via `appsettings.json`:

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

Or use environment variables:

```bash
export DockerSSH__SshHost=your-server.com
export DockerSSH__SshUsername=your-username
export DockerRegistry__RegistryUrl=ghcr.io
```

## How It Works

1. **Build** - Creates container images locally
2. **Push** - Tags and pushes images to your registry
3. **Deploy** - Transfers compose files via SCP, runs `docker compose up` on the remote host

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
