# Aspire Docker SSH Deployment Pipeline

[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Fdavidfowl%2Faspire%2Fshield%2FAspire.Hosting.Docker.SshDeploy%2Flatest&label=Aspire.Hosting.Docker.SshDeploy)](https://f.feedz.io/davidfowl/aspire/packages/Aspire.Hosting.Docker.SshDeploy/latest/download)

Deploy Aspire applications to remote Docker hosts via SSH.

## Overview

This package extends Aspire's Docker Compose support with a deployment pipeline that builds container images locally, pushes them to a registry, and deploys to a remote server via SSH. The pipeline handles SSH connection management, file transfers, and container orchestration with `docker compose`.

```mermaid
flowchart LR
    A[Dev Machine / CI<br/>aspire deploy] -->|SSH| B[Target Server<br/>docker compose up]
```

## Quick Start

1. Install the package:

```bash
dotnet nuget add source https://f.feedz.io/davidfowl/aspire/nuget/index.json --name davidfowl-aspire
dotnet add package Aspire.Hosting.Docker.SshDeploy --prerelease
```

2. Add SSH deployment support to your AppHost:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

3. Deploy:

```bash
aspire deploy
```

The pipeline will prompt for SSH credentials, registry configuration, and deploy path.

## Documentation

See the [package README](src/Aspire.Hosting.Docker.SshDeploy/README.md) for:
- Configuration options (`appsettings.json`, environment variables)
- SSH authentication (key-based vs password)
- Target host privacy settings

## Sample Project

See `samples/DockerPipelinesSample` for a complete example:

```bash
aspire run     # Run locally
aspire deploy  # Deploy to remote host
```

## CI/CD with GitHub Actions

See `.github/workflows/deploy.yml` for a complete example using GitHub Container Registry with secrets for SSH credentials.
