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
    "TargetHost": "your-server.com",
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

### SSH Key Path Formats

The `SshKeyPath` supports multiple formats:

```json
// Tilde expansion (recommended)
"SshKeyPath": "~/.ssh/id_ed25519"

// $HOME expansion
"SshKeyPath": "$HOME/.ssh/id_rsa"

// Full absolute path
"SshKeyPath": "/Users/john/.ssh/id_rsa"

// Just the key name (assumes ~/.ssh/ directory)
"SshKeyPath": "id_ed25519"
```

### SSH Authentication

**Key-based authentication (recommended):**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshKeyPath": "~/.ssh/id_ed25519",
    "SshPassword": "***"
  }
}
```
> Note: When using key-based auth, `SshPassword` is the **passphrase** for your private key. Leave empty or omit if your key has no passphrase.

**Password authentication:**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshPassword": "***"
  }
}
```
> Note: When not using a key, `SshPassword` is your SSH login password.

### Environment Variables

Or use environment variables:

```bash
export DockerSSH__TargetHost=your-server.com
export DockerSSH__SshUsername=your-username
export DockerSSH__SshKeyPath=~/.ssh/id_ed25519
export DockerRegistry__RegistryUrl=ghcr.io
```

### Target Host Privacy

By default, IP addresses are treated as sensitive and masked in output:
- **Domain names** (e.g., `deploy.example.com`) → Shown in URLs
- **IP addresses** (e.g., `192.168.1.100`) → Masked as `***` in URLs

To force showing IP addresses in output:
```bash
export UNSAFE_SHOW_TARGET_HOST=true
```

This behavior protects against accidentally exposing server IP addresses in logs or CI output.

## How It Works

1. **Build** - Creates container images locally
2. **Push** - Tags and pushes images to your registry
3. **Deploy** - Transfers compose files via SCP, runs `docker compose up` on the remote host

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
