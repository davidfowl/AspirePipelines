# Aspire.Hosting.Docker.SshDeploy

Deploy .NET Aspire applications to remote Docker hosts via SSH.

## Installation

1. Add the package feed:

```bash
dotnet nuget add source https://f.feedz.io/davidfowl/aspire/nuget/index.json --name davidfowl-aspire
```

2. Install the package:

```bash
aspire add docker-sshdeploy
```

Or with the .NET CLI:

```bash
dotnet add package Aspire.Hosting.Docker.SshDeploy --prerelease
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

## CI/CD with GitHub Actions

Generate a GitHub Actions workflow that deploys on every push to `main`:

```bash
aspire do gh-action-{environment-name}
```

For example, if your Docker Compose environment is named `env`:

```bash
aspire do gh-action-env
```

This command:
1. Creates a GitHub environment with your deployment settings
2. Stores SSH credentials and parameters as GitHub secrets/variables
3. Generates a workflow file (`.github/workflows/deploy-{name}.yml`)

### Prerequisites

- GitHub CLI (`gh`) installed and authenticated (`gh auth login`)
- Current directory is a GitHub repository with a remote

### What Gets Created

**GitHub Environment** with:
- `TARGET_HOST` (variable) - Your server hostname/IP
- `SSH_USERNAME` (secret) - SSH username
- `SSH_PRIVATE_KEY` (secret) - SSH private key contents (for key auth)
- `SSH_PASSWORD` (secret) - SSH password (for password auth)
- `SSH_KEY_PASSPHRASE` (secret) - Key passphrase (if applicable)
- Any application parameters defined in your AppHost

**Workflow File** that:
- Triggers on push to `main` and manual dispatch
- Uses GitHub Container Registry (`ghcr.io`)
- Handles SSH authentication automatically
- Runs `aspire deploy` with environment variables

### Re-running the Command

Running `aspire do gh-action-{name}` again will:
1. Detect existing secrets/variables
2. Prompt whether to overwrite existing values
3. Update only what you choose to change

### Generated Workflow

The workflow uses environment variables to configure the deployment:

```yaml
env:
  DockerSSH__TargetHost: ${{ vars.TARGET_HOST }}
  DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}
  DockerSSH__SshKeyPath: ${{ github.workspace }}/.ssh/id_rsa
  DockerRegistry__RegistryUrl: ghcr.io
  DockerRegistry__RepositoryPrefix: ${{ github.repository_owner }}
```

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
