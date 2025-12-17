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

# Or with the .NET CLI:
dotnet add package Aspire.Hosting.Docker.SshDeploy --prerelease
```

3. Add SSH deployment support to your AppHost:

```csharp
// Container registry parameters (prompted during deployment or set via config)
var registryEndpoint = builder.AddParameter("registry-endpoint");
var registryRepository = builder.AddParameter("registry-repository");
var registryUsername = builder.AddParameter("registry-username");
var registryPassword = builder.AddParameter("registry-password", secret: true);

// Create container registry with automatic login
var registry = builder.AddContainerRegistry("registry", registryEndpoint, registryRepository)
    .WithCredentialsLogin(registryUsername, registryPassword);

// Configure Docker Compose environment with SSH deployment
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithContainerRegistry(registry);
```

4. Deploy:

```bash
aspire deploy
```

The pipeline will prompt for SSH credentials, registry parameters, and deploy path.

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
  "Parameters": {
    "registry-endpoint": "docker.io",
    "registry-repository": "your-docker-username",
    "registry-username": "your-username",
    "registry-password": "your-password-or-token"
  },
  "Deployment": {
    "RemoteDeployPath": "$HOME/aspire/apps/your-app",
    "PruneImagesAfterDeploy": "true"
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
export Parameters__registry-endpoint=ghcr.io
export Parameters__registry-repository=your-org/your-repo
export Parameters__registry-username=your-username
export Parameters__registry-password=your-token
```

### Target Host Privacy

The target host is treated as sensitive and masked in output by default.

To show the target host:
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

### Multiple Environments

Deploy to different environments (staging, production, etc.) by using the `-e` flag:

```bash
aspire do gh-action-env -e staging
aspire do gh-action-env -e production
```

This creates separate GitHub environments and workflow files for each:
- `.github/workflows/deploy-env-staging.yml`
- `.github/workflows/deploy-env-production.yml`

Each workflow runs `aspire deploy -e {environment}`, allowing your AppHost to customize behavior per environment.

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
  Parameters__registry-endpoint: ghcr.io
  Parameters__registry-repository: ${{ github.repository }}
  Parameters__registry-username: ${{ github.actor }}
  Parameters__registry-password: ${{ secrets.GITHUB_TOKEN }}
```

## Teardown

Tear down a deployed environment by stopping and removing all containers:

```bash
aspire do teardown-{resource-name}
```

For example, if your Docker Compose environment is named `env`:

```bash
aspire do teardown-env
```

This command:
1. Connects to the remote server via SSH
2. Shows all running containers and their status
3. Prompts for confirmation before proceeding
4. Runs `docker compose down` to stop and remove containers

This is great for ephemeral environments like feature branch deployments, PR previews, or temporary staging environments that need to be cleaned up after use.

### Configuration

The teardown command uses the same SSH and deployment configuration as `aspire deploy`. It will prompt for credentials if not already configured.

To skip prompts, ensure your `appsettings.json` or environment variables are set (see [Configuration](#configuration-optional)).

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
