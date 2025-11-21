using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteEnvironmentService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeRemoteEnvironmentService : IRemoteEnvironmentService
{
    private readonly List<EnvironmentDeployment> _deployments = new();
    private bool _shouldDeploymentFail;
    private string? _deploymentFailureMessage;

    /// <summary>
    /// Gets all the environment deployments that were performed.
    /// </summary>
    public IReadOnlyList<EnvironmentDeployment> Deployments => _deployments.AsReadOnly();

    /// <summary>
    /// Configures deployment to fail with a specific message.
    /// </summary>
    public void ConfigureDeploymentFailure(string? message = null)
    {
        _shouldDeploymentFail = true;
        _deploymentFailureMessage = message ?? "Environment deployment failed (configured to fail)";
    }

    public Task<EnvironmentDeploymentResult> DeployEnvironmentAsync(
        string localEnvPath,
        string remoteDeployPath,
        Dictionary<string, string> imageTags,
        CancellationToken cancellationToken)
    {
        _deployments.Add(new EnvironmentDeployment(localEnvPath, remoteDeployPath, imageTags));

        if (_shouldDeploymentFail)
        {
            throw new InvalidOperationException(_deploymentFailureMessage!);
        }

        // Create a merged result simulating what the real service would do
        var mergedVars = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production"
        };

        // Add image tags
        foreach (var (serviceName, imageTag) in imageTags)
        {
            var imageEnvKey = $"{serviceName.ToUpperInvariant()}_IMAGE";
            mergedVars[imageEnvKey] = imageTag;
        }

        var result = new EnvironmentDeploymentResult(
            VariableCount: mergedVars.Count,
            RemoteEnvPath: $"{remoteDeployPath}/.env",
            MergedVariables: mergedVars);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Checks if a deployment was performed to a specific path.
    /// </summary>
    public bool WasDeployedTo(string remoteDeployPath)
    {
        return _deployments.Any(d => d.RemoteDeployPath == remoteDeployPath);
    }

    /// <summary>
    /// Gets the number of deployments performed.
    /// </summary>
    public int GetDeploymentCount()
    {
        return _deployments.Count;
    }

    /// <summary>
    /// Clears all recorded deployments.
    /// </summary>
    public void ClearDeployments()
    {
        _deployments.Clear();
    }
}

/// <summary>
/// Represents a recorded environment deployment.
/// </summary>
public record EnvironmentDeployment(
    string LocalEnvPath,
    string RemoteDeployPath,
    Dictionary<string, string> ImageTags);
