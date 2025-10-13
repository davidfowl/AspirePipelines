# Kamal-Inspired Enhancements for AspirePipelines

## 1. Automatic Docker Installation

**Current State**: Only verifies Docker is installed
**Enhancement**: Auto-install Docker if missing

```csharp
private async Task EnsureDockerInstalled(IPublishingStep step, CancellationToken cancellationToken)
{
    var dockerCheck = await ExecuteSSHCommandWithOutput("docker --version", cancellationToken);
    if (dockerCheck.ExitCode != 0)
    {
        await using var installTask = await step.CreateTaskAsync("Installing Docker", cancellationToken);
        
        // Install Docker using get.docker.com script
        var installResult = await ExecuteSSHCommandWithOutput(
            "curl -fsSL https://get.docker.com | sh", cancellationToken);
        
        if (installResult.ExitCode != 0)
        {
            await installTask.FailAsync($"Failed to install Docker: {installResult.Error}");
            throw new InvalidOperationException("Docker installation failed");
        }

        // Add user to docker group
        await ExecuteSSHCommand($"usermod -aG docker {_sshUsername}", cancellationToken);
        await installTask.SucceedAsync("Docker installed successfully");
    }
}
```

## 2. Reverse Proxy Management (Traefik/Caddy Integration)

**Current State**: Direct container exposure
**Enhancement**: Automatic reverse proxy setup with SSL

```csharp
private async Task EnsureReverseProxy(string deployPath, IPublishingStep step, CancellationToken cancellationToken)
{
    await using var proxyTask = await step.CreateTaskAsync("Setting up reverse proxy", cancellationToken);

    // Check if Traefik is running
    var traefikCheck = await ExecuteSSHCommandWithOutput(
        "docker ps --filter name=traefik --format '{{.Names}}'", cancellationToken);

    if (string.IsNullOrEmpty(traefikCheck.Output.Trim()))
    {
        // Deploy Traefik with Let's Encrypt
        var traefikCompose = GenerateTraefikCompose();
        await TransferStringAsFile(traefikCompose, $"{deployPath}/traefik.yml", cancellationToken);
        
        await ExecuteSSHCommand($"cd {deployPath} && docker compose -f traefik.yml up -d", cancellationToken);
        await proxyTask.SucceedAsync("Traefik proxy deployed with SSL");
    }
    else
    {
        await proxyTask.SucceedAsync("Reverse proxy already running");
    }
}

private string GenerateTraefikCompose()
{
    return """
    version: '3.8'
    services:
      traefik:
        image: traefik:v3.0
        command:
          - --api.dashboard=true
          - --entrypoints.web.address=:80
          - --entrypoints.websecure.address=:443
          - --providers.docker=true
          - --providers.docker.exposedbydefault=false
          - --certificatesresolvers.letsencrypt.acme.httpchallenge=true
          - --certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web
          - --certificatesresolvers.letsencrypt.acme.email=admin@example.com
          - --certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json
        ports:
          - "80:80"
          - "443:443"
        volumes:
          - /var/run/docker.sock:/var/run/docker.sock:ro
          - letsencrypt:/letsencrypt
    volumes:
      letsencrypt:
    """;
}
```

## 3. Zero-Downtime Blue-Green Deployment

**Current State**: Stop old, start new (downtime)
**Enhancement**: Start new, health check, route traffic, stop old

```csharp
private async Task<string> DeployWithZeroDowntime(string deployPath, Dictionary<string, string> imageTags, IPublishingStep step, CancellationToken cancellationToken)
{
    var deploymentId = GenerateDeploymentId();
    
    await using var blueGreenTask = await step.CreateTaskAsync("Blue-green deployment", cancellationToken);

    // 1. Start new containers with unique names
    await StartNewDeployment(deployPath, imageTags, deploymentId, cancellationToken);

    // 2. Wait for health checks to pass
    await WaitForHealthChecks(deployPath, deploymentId, cancellationToken);

    // 3. Update proxy routing to new containers
    await UpdateProxyRouting(deployPath, deploymentId, cancellationToken);

    // 4. Stop old containers
    await StopOldDeployment(deployPath, deploymentId, cancellationToken);

    // 5. Clean up old images and containers
    await CleanupOldResources(cancellationToken);

    await blueGreenTask.SucceedAsync($"Zero-downtime deployment completed: {deploymentId}");
    return deploymentId;
}

private async Task WaitForHealthChecks(string deployPath, string deploymentId, CancellationToken cancellationToken)
{
    var maxWait = TimeSpan.FromMinutes(5);
    var startTime = DateTime.UtcNow;

    while (DateTime.UtcNow - startTime < maxWait)
    {
        var healthCheck = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && docker compose ps --filter label=deployment={deploymentId} --format json", 
            cancellationToken);

        // Parse health status and check if all services are healthy
        var services = ParseDockerComposeStatus(healthCheck.Output);
        if (services.All(s => s.IsHealthy))
        {
            return; // All healthy
        }

        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
    }

    throw new InvalidOperationException("Health checks failed within timeout period");
}
```

## 4. Git-Based Versioning

**Current State**: Timestamp-based tags
**Enhancement**: Use Git commit hash for reproducible deployments

```csharp
private async Task<string> GetGitVersionTag()
{
    // Get current Git commit hash
    var gitResult = await DockerCommandUtility.ExecuteProcessAsync(
        "git", "rev-parse --short HEAD", CancellationToken.None);

    if (gitResult.ExitCode == 0 && !string.IsNullOrEmpty(gitResult.Output.Trim()))
    {
        return gitResult.Output.Trim();
    }

    // Fallback to timestamp if not in Git repo
    return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
}

private async Task<Dictionary<string, string>> PushContainerImagesToRegistryWithGitTag(...)
{
    var gitTag = await GetGitVersionTag();
    var imageTags = new Dictionary<string, string>();

    foreach (var cr in context.Model.GetComputeResources())
    {
        if (cr.IsContainer() && cr.HasAnnotationOfType<DockerfileBuildAnnotation>())
        {
            var serviceName = cr.Name;
            var targetImageName = !string.IsNullOrEmpty(repositoryPrefix)
                ? $"{registryUrl}/{repositoryPrefix}/{serviceName}:{gitTag}"
                : $"{registryUrl}/{serviceName}:{gitTag}";

            // Tag and push with Git version
            imageTags[serviceName] = targetImageName;
        }
    }

    return imageTags;
}
```

## 5. Advanced Health Check Integration

**Current State**: Basic container status
**Enhancement**: Custom health endpoint validation

```csharp
private async Task WaitForApplicationHealth(string deployPath, Dictionary<string, string> servicePorts, CancellationToken cancellationToken)
{
    await using var healthStep = await step.CreateTaskAsync("Waiting for application health", cancellationToken);

    var healthTasks = servicePorts.Select(async servicePort =>
    {
        var (serviceName, port) = servicePort;
        var healthUrl = $"http://localhost:{port}/health"; // or /up for Kamal compatibility

        var maxRetries = 30;
        for (int i = 0; i < maxRetries; i++)
        {
            var healthCheck = await ExecuteSSHCommandWithOutput(
                $"curl -f -s {healthUrl} || echo 'UNHEALTHY'", cancellationToken);

            if (!healthCheck.Output.Contains("UNHEALTHY"))
            {
                await healthStep.UpdateAsync($"✓ {serviceName} healthy on port {port}", cancellationToken);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        throw new InvalidOperationException($"Service {serviceName} failed health checks on {healthUrl}");
    });

    await Task.WhenAll(healthTasks);
    await healthStep.SucceedAsync("All services passed health checks");
}
```

## 6. Resource Cleanup and Pruning

**Current State**: No cleanup
**Enhancement**: Automatic resource pruning

```csharp
private async Task CleanupOldResources(CancellationToken cancellationToken)
{
    await using var cleanupStep = await step.CreateTaskAsync("Cleaning up old resources", cancellationToken);

    // Remove stopped containers
    await ExecuteSSHCommand("docker container prune -f", cancellationToken);

    // Remove unused images (keep last 3 deployments)
    await ExecuteSSHCommand("docker image prune -f", cancellationToken);

    // Remove unused volumes
    await ExecuteSSHCommand("docker volume prune -f", cancellationToken);

    // Remove unused networks
    await ExecuteSSHCommand("docker network prune -f", cancellationToken);

    await cleanupStep.SucceedAsync("Resource cleanup completed");
}

private async Task PruneOldDeployments(string deployPath, string currentDeploymentId, CancellationToken cancellationToken)
{
    // Keep only the last 3 deployments
    var deploymentsResult = await ExecuteSSHCommandWithOutput(
        $"cd {deployPath} && ls -1t | grep -E '^[a-f0-9]{{7}}$' | tail -n +4", cancellationToken);

    if (!string.IsNullOrEmpty(deploymentsResult.Output))
    {
        var oldDeployments = deploymentsResult.Output.Trim().Split('\n');
        foreach (var deployment in oldDeployments)
        {
            await ExecuteSSHCommand($"cd {deployPath} && rm -rf {deployment.Trim()}", cancellationToken);
        }
    }
}
```

## 7. Rollback Capabilities

**Enhancement**: Quick rollback to previous deployment

```csharp
public async Task RollbackToPreviousDeployment(string deployPath, CancellationToken cancellationToken)
{
    await using var rollbackStep = await step.CreateStepAsync("Rolling back deployment", cancellationToken);

    // Find previous deployment
    var previousDeployment = await GetPreviousDeploymentId(deployPath, cancellationToken);
    if (string.IsNullOrEmpty(previousDeployment))
    {
        throw new InvalidOperationException("No previous deployment found for rollback");
    }

    // Update proxy to route to previous deployment
    await UpdateProxyRouting(deployPath, previousDeployment, cancellationToken);

    // Stop current deployment
    var currentDeployment = await GetCurrentDeploymentId(deployPath, cancellationToken);
    if (!string.IsNullOrEmpty(currentDeployment))
    {
        await StopDeployment(deployPath, currentDeployment, cancellationToken);
    }

    await rollbackStep.SucceedAsync($"Rolled back to deployment: {previousDeployment}");
}
```

## 8. Configuration Enhancements

**Enhancement**: Kamal-style configuration file support

```csharp
// Support for kamal-style deploy.yml configuration
public class KamalStyleConfiguration
{
    public string? Service { get; set; }
    public string? Image { get; set; }
    public List<string> Servers { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public ProxyConfiguration? Proxy { get; set; }
    public HealthCheckConfiguration? HealthCheck { get; set; }
}

public class ProxyConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "traefik"; // or "caddy"
    public bool Ssl { get; set; } = true;
    public string? Domain { get; set; }
}

public class HealthCheckConfiguration
{
    public string Path { get; set; } = "/health";
    public int Port { get; set; } = 80;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}
```

## Implementation Priority

1. **High Priority** (Core Kamal features):
   - Zero-downtime blue-green deployment
   - Resource cleanup and pruning
   - Git-based versioning

2. **Medium Priority** (Operational improvements):
   - Advanced health check integration
   - Rollback capabilities
   - Automatic Docker installation

3. **Low Priority** (Nice-to-have):
   - Reverse proxy management (can be done manually)
   - Kamal-style configuration format

These enhancements would make AspirePipelines significantly more production-ready and align it closer to Kamal's capabilities while maintaining the Aspire-native experience.
