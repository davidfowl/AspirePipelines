# Aspire.Hosting.Docker.SshDeploy

Deploy .NET Aspire applications to remote Docker hosts via SSH.

## Installation

```bash
dotnet add package Aspire.Hosting.Docker.SshDeploy
```

## Usage

Call `WithSshDeploySupport()` on your Docker Compose environment:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

builder.Build().Run();
```

Then deploy:

```bash
aspire deploy
```

**That's it.** The pipeline will prompt you for SSH credentials, registry, and deploy path. No configuration required.

## File Transfer

Transfer additional files (certificates, configs, etc.) to the remote server.

### App Files (relative to deploy path)

Use `WithAppFileTransfer` to transfer files to a subdirectory within the deployment path:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithAppFileTransfer("./certs", "certs");  // ./certs → {RemoteDeployPath}/certs
```

### Absolute Paths

Use `WithFileTransfer` for full control over the remote destination:

```csharp
var certDir = builder.AddParameter("certDir");  // e.g., "$HOME/certs"

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithFileTransfer("./certs", certDir);  // ./certs → $HOME/certs
```

Or with a string literal:
```csharp
.WithFileTransfer("./certs", "$HOME/certs")
```

Multiple file transfers can be chained:
```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithAppFileTransfer("./config", "config")
    .WithFileTransfer("./certs", certDir);
```

## Configuration (Optional)

To skip prompts, pre-configure via `appsettings.json`:

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

### SSH Key Path

The `SshKeyPath` supports tilde and `$HOME` expansion:

```json
"SshKeyPath": "~/.ssh/id_ed25519"
"SshKeyPath": "$HOME/.ssh/id_rsa"
"SshKeyPath": "/Users/john/.ssh/id_rsa"
```

### SSH Authentication

**Key-based (recommended):**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshKeyPath": "~/.ssh/id_ed25519",
    "SshPassword": "key-passphrase-if-any"
  }
}
```

**Password-based:**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshPassword": "your-password"
  }
}
```

### Environment Variables

```bash
export DockerSSH__TargetHost=your-server.com
export DockerSSH__SshUsername=your-username
export DockerSSH__SshKeyPath=~/.ssh/id_ed25519
export DockerRegistry__RegistryUrl=ghcr.io
```

### Target Host Privacy

IP addresses are masked in output by default. Domain names are shown.

To show IP addresses:
```bash
export UNSAFE_SHOW_TARGET_HOST=true
```

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
