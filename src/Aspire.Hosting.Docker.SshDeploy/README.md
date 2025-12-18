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

## Usage Scenarios

### 1. Interactive Mode (Simplest)

Just add SSH deploy support and run `aspire deploy`. You'll be prompted for everything:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

builder.Build().Run();
```

```bash
aspire deploy
```

The pipeline prompts for:
- Registry URL (e.g., `docker.io`, `ghcr.io`)
- Repository prefix (e.g., `myusername` or `myorg/myrepo`)
- SSH target host
- SSH credentials

No explicit registry login is performed - docker uses whatever credentials it already has.

---

### 2. Pre-configured Registry (No Prompts)

Configure the registry via environment variables or `appsettings.json` to skip prompts:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables:**
```bash
export DockerRegistry__RegistryUrl=docker.io
export DockerRegistry__RepositoryPrefix=myusername
```

**Or appsettings.json:**
```json
{
  "DockerRegistry": {
    "RegistryUrl": "docker.io",
    "RepositoryPrefix": "myusername"
  }
}
```

This assumes you're already logged in to the registry (e.g., via `docker login`).

---

### 3. With Registry Credentials

Add username and password to perform explicit `docker login` before pushing:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables:**
```bash
export DockerRegistry__RegistryUrl=ghcr.io
export DockerRegistry__RepositoryPrefix=myorg/myrepo
export DockerRegistry__RegistryUsername=myusername
export DockerRegistry__RegistryPassword=ghp_xxxxxxxxxxxx
```

**Or appsettings.json:**
```json
{
  "DockerRegistry": {
    "RegistryUrl": "ghcr.io",
    "RepositoryPrefix": "myorg/myrepo",
    "RegistryUsername": "myusername",
    "RegistryPassword": "ghp_xxxxxxxxxxxx"
  }
}
```

When both username and password are configured, the pipeline logs in to the registry before pushing images.

---

### 4. Custom Registry Resource (Full Control)

For complete control, define your own container registry resource:

```csharp
var registry = builder.AddContainerRegistry("my-registry", "ghcr.io", "myorg/myrepo");

// Option A: Already logged in (no credentials needed)
builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(registry)
    .WithSshDeploySupport();
```

```csharp
// Option B: With explicit credentials
var username = builder.AddParameter("registry-username");
var password = builder.AddParameter("registry-password", secret: true);

var registry = builder.AddContainerRegistry("my-registry", "ghcr.io", "myorg/myrepo")
    .WithCredentialsLogin(username, password);

builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(registry)
    .WithSshDeploySupport();
```

When you specify your own registry via `WithContainerRegistry()`, the built-in default registry is not used.

---

## SSH Configuration

### Environment Variables

```bash
export DockerSSH__TargetHost=your-server.com
export DockerSSH__SshUsername=deploy
export DockerSSH__SshKeyPath=~/.ssh/id_ed25519
```

### appsettings.json

**Key-based authentication (recommended):**
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

**Password-based authentication:**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshPassword": "your-password"
  }
}
```

The `SshKeyPath` supports tilde and `$HOME` expansion:
```
~/.ssh/id_ed25519
$HOME/.ssh/id_rsa
/Users/john/.ssh/id_rsa
```

### Target Host Privacy

The target host is masked in output by default. To show it:
```bash
export UNSAFE_SHOW_TARGET_HOST=true
```

---

## File Transfer

Transfer additional files (certificates, configs, etc.) to the remote server.

### Relative to Deploy Path

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithAppFileTransfer("./certs", "certs");  // ./certs → {RemoteDeployPath}/certs
```

### Absolute Path

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithFileTransfer("./certs", "$HOME/certs");
```

Or with a parameter:
```csharp
var certDir = builder.AddParameter("certDir");

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithFileTransfer("./certs", certDir);
```

---

### 5. GitHub Actions with GHCR

Use GitHub Container Registry with the built-in `GITHUB_TOKEN`:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables (in workflow):**
```yaml
env:
  DockerSSH__TargetHost: ${{ secrets.TARGET_HOST }}
  DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}
  DockerRegistry__RegistryUrl: ghcr.io
  DockerRegistry__RepositoryPrefix: ${{ github.repository }}
  DockerRegistry__RegistryUsername: ${{ github.actor }}
  DockerRegistry__RegistryPassword: ${{ secrets.GITHUB_TOKEN }}
```

**Required secrets:** `SSH_PRIVATE_KEY`, `SSH_USERNAME`, `TARGET_HOST`

The `GITHUB_TOKEN` is automatically available with `packages:write` permission.

---

## CI/CD with GitHub Actions

### Generate Workflow Automatically

```bash
aspire do gh-action-env
```

This creates a GitHub environment with secrets and generates `.github/workflows/deploy-env.yml`.

### Multiple Environments

```bash
aspire do gh-action-env -e staging
aspire do gh-action-env -e production
```

---

## Teardown

Stop and remove all containers:

```bash
aspire do teardown-env
```

This connects via SSH, shows running containers, and runs `docker compose down` after confirmation.

---

## Example Workflow

A complete GitHub Actions workflow for deploying to a remote server:

```yaml
name: Deploy

on:
  workflow_dispatch:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: Production
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install Aspire CLI
        run: curl -sSL https://aspire.dev/install.sh | bash

      - name: Setup SSH agent
        uses: webfactory/ssh-agent@v0.9.1
        with:
          ssh-private-key: ${{ secrets.SSH_PRIVATE_KEY }}

      - name: Add known hosts
        run: ssh-keyscan -H ${{ secrets.TARGET_HOST }} >> ~/.ssh/known_hosts

      - name: Deploy
        run: aspire deploy -e Production
        env:
          DockerSSH__TargetHost: ${{ secrets.TARGET_HOST }}
          DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}
          DockerRegistry__RegistryUrl: ghcr.io
          DockerRegistry__RepositoryPrefix: ${{ github.repository }}
          DockerRegistry__RegistryUsername: ${{ github.actor }}
          DockerRegistry__RegistryPassword: ${{ secrets.GITHUB_TOKEN }}
```

---

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
