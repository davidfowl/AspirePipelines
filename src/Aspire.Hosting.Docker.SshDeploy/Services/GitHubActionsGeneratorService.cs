#pragma warning disable ASPIREPIPELINES001

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Service for generating GitHub Actions workflow files and setting secrets/variables.
/// </summary>
internal class GitHubActionsGeneratorService
{
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<GitHubActionsGeneratorService> _logger;

    public GitHubActionsGeneratorService(
        IProcessExecutor processExecutor,
        ILogger<GitHubActionsGeneratorService> logger)
    {
        _processExecutor = processExecutor;
        _logger = logger;
    }

    /// <summary>
    /// Checks if the GitHub CLI is installed, authenticated, and the current directory is a GitHub repository.
    /// Returns the git repository root path.
    /// </summary>
    public async Task<string> CheckGitHubCliAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Checking GitHub CLI availability");

        // Check if gh is installed
        var versionResult = await _processExecutor.ExecuteAsync(
            "gh",
            "--version",
            cancellationToken: cancellationToken);

        if (versionResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "GitHub CLI (gh) is required but not installed. Install it from https://cli.github.com/");
        }

        // Check if gh is authenticated
        var authResult = await _processExecutor.ExecuteAsync(
            "gh",
            "auth status",
            cancellationToken: cancellationToken);

        if (authResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "GitHub CLI is not authenticated. Run 'gh auth login' to authenticate.");
        }

        // Check if current directory is a GitHub repository
        var repoResult = await _processExecutor.ExecuteAsync(
            "gh",
            "repo view --json name",
            cancellationToken: cancellationToken);

        if (repoResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Current directory is not a GitHub repository. Run this command from a git repository with a GitHub remote.");
        }

        // Get the git repository root
        var gitRootResult = await _processExecutor.ExecuteAsync(
            "git",
            "rev-parse --show-toplevel",
            cancellationToken: cancellationToken);

        if (gitRootResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Failed to determine git repository root.");
        }

        var gitRoot = gitRootResult.Output.Trim();
        _logger.LogDebug("GitHub CLI is available and authenticated. Git root: {GitRoot}", gitRoot);

        return gitRoot;
    }

    /// <summary>
    /// Creates a GitHub environment if it doesn't exist.
    /// </summary>
    public async Task CreateEnvironmentAsync(string environmentName, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating GitHub environment: {EnvironmentName}", environmentName);

        // gh api creates the environment if it doesn't exist
        var result = await _processExecutor.ExecuteAsync(
            "gh",
            $"api repos/{{owner}}/{{repo}}/environments/{environmentName} -X PUT",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create GitHub environment {environmentName}: {result.Error}");
        }

        _logger.LogInformation("Created GitHub environment: {EnvironmentName}", environmentName);
    }

    /// <summary>
    /// Sets a value in a GitHub environment as either a secret or variable.
    /// </summary>
    public async Task SetEnvironmentValueAsync(string environmentName, string name, string value, bool isSecret, CancellationToken cancellationToken)
    {
        var command = isSecret ? "secret" : "variable";
        var arguments = $"{command} set {name} --env {environmentName}";

        _logger.LogDebug("Setting GitHub {Type} {Name} in environment {Environment}", command, name, environmentName);

        var result = await _processExecutor.ExecuteAsync(
            "gh",
            arguments,
            stdinInput: value,
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to set GitHub {command} {name} in environment {environmentName}: {result.Error}");
        }

        _logger.LogInformation("Set GitHub {Type}: {Name} in environment {Environment}", command, name, environmentName);
    }

    /// <summary>
    /// Gets existing secrets in a GitHub environment.
    /// </summary>
    public async Task<HashSet<string>> GetEnvironmentSecretsAsync(string environmentName, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Listing secrets in GitHub environment: {EnvironmentName}", environmentName);

        var result = await _processExecutor.ExecuteAsync(
            "gh",
            $"secret list --env {environmentName} --json name",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("Failed to list secrets (environment may not exist yet): {Error}", result.Error);
            return [];
        }

        var secrets = JsonSerializer.Deserialize<List<GitHubNameEntry>>(result.Output, JsonOptions) ?? [];
        return secrets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets existing variables in a GitHub environment.
    /// </summary>
    public async Task<HashSet<string>> GetEnvironmentVariablesAsync(string environmentName, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Listing variables in GitHub environment: {EnvironmentName}", environmentName);

        var result = await _processExecutor.ExecuteAsync(
            "gh",
            $"variable list --env {environmentName} --json name",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("Failed to list variables (environment may not exist yet): {Error}", result.Error);
            return [];
        }

        var variables = JsonSerializer.Deserialize<List<GitHubNameEntry>>(result.Output, JsonOptions) ?? [];
        return variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes a secret from a GitHub environment.
    /// </summary>
    public async Task DeleteEnvironmentSecretAsync(string environmentName, string name, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Deleting secret {Name} from environment {Environment}", name, environmentName);

        var result = await _processExecutor.ExecuteAsync(
            "gh",
            $"secret delete {name} --env {environmentName}",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to delete GitHub secret {name} from environment {environmentName}: {result.Error}");
        }

        _logger.LogInformation("Deleted GitHub secret: {Name} from environment {Environment}", name, environmentName);
    }

    /// <summary>
    /// Deletes a variable from a GitHub environment.
    /// </summary>
    public async Task DeleteEnvironmentVariableAsync(string environmentName, string name, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Deleting variable {Name} from environment {Environment}", name, environmentName);

        var result = await _processExecutor.ExecuteAsync(
            "gh",
            $"variable delete {name} --env {environmentName}",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to delete GitHub variable {name} from environment {environmentName}: {result.Error}");
        }

        _logger.LogInformation("Deleted GitHub variable: {Name} from environment {Environment}", name, environmentName);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private record GitHubNameEntry(
        [property: JsonPropertyName("name")] string Name);

    /// <summary>
    /// Generates a standalone GitHub Actions workflow YAML.
    /// </summary>
    public string GenerateStandaloneWorkflow(WorkflowGenerationOptions options)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"name: Deploy ({options.EnvironmentName})");
        sb.AppendLine();
        sb.AppendLine("on:");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main]");
        sb.AppendLine();
        sb.AppendLine("jobs:");
        sb.AppendLine("  deploy:");
        sb.AppendLine("    runs-on: ubuntu-latest");
        sb.AppendLine($"    environment: {options.EnvironmentName}");
        sb.AppendLine("    permissions:");
        sb.AppendLine("      contents: read");
        sb.AppendLine("      packages: write");
        sb.AppendLine("    steps:");
        sb.AppendLine("      - uses: actions/checkout@v4");
        sb.AppendLine();
        sb.AppendLine("      - name: Setup .NET");
        sb.AppendLine("        uses: actions/setup-dotnet@v4");
        sb.AppendLine("        with:");
        sb.AppendLine($"          dotnet-version: '{options.DotNetVersion}'");
        sb.AppendLine();
        sb.AppendLine("      - name: Install Aspire CLI");
        sb.AppendLine("        run: curl -sSL https://aspire.dev/install.sh | bash");
        sb.AppendLine();

        // SSH setup depends on auth type
        if (options.SshAuthType is SshAuthType.Key or SshAuthType.KeyWithPassphrase)
        {
            // Use ssh-agent action for key-based authentication
            sb.AppendLine("      - name: Setup SSH agent");
            sb.AppendLine("        uses: webfactory/ssh-agent@v0.9.1");
            sb.AppendLine("        with:");
            sb.AppendLine("          ssh-private-key: ${{ secrets.SSH_PRIVATE_KEY }}");
            sb.AppendLine();
            sb.AppendLine("      - name: Add known hosts");
            sb.AppendLine("        run: ssh-keyscan -H ${{ vars.TARGET_HOST }} >> ~/.ssh/known_hosts");
            sb.AppendLine();
        }
        else
        {
            // Password auth - just need known_hosts
            sb.AppendLine("      - name: Setup SSH known hosts");
            sb.AppendLine("        run: |");
            sb.AppendLine("          mkdir -p ~/.ssh");
            sb.AppendLine("          ssh-keyscan -H ${{ vars.TARGET_HOST }} >> ~/.ssh/known_hosts");
            sb.AppendLine();
        }

        sb.AppendLine("      - name: Set short SHA");
        sb.AppendLine("        run: echo \"SHORT_SHA=${GITHUB_SHA::7}\" >> $GITHUB_ENV");
        sb.AppendLine();
        sb.AppendLine("      - name: Deploy");
        sb.AppendLine($"        run: aspire deploy -e {options.EnvironmentName}");

        // Build environment variables section
        sb.AppendLine("        env:");
        sb.AppendLine("          DockerSSH__TargetHost: ${{ vars.TARGET_HOST }}");
        sb.AppendLine("          DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}");

        // Auth-specific env vars
        // For key-based auth: ssh-agent handles keys, no need for SshKeyPath
        // For password auth: fall back to SSH.NET
        if (options.SshAuthType == SshAuthType.Password)
        {
            sb.AppendLine("          DockerSSH__UseLegacySshNet: 'true'");
            sb.AppendLine("          DockerSSH__SshPassword: ${{ secrets.SSH_PASSWORD }}");
        }

        sb.AppendLine("          DockerRegistry__RegistryUrl: ghcr.io");
        sb.AppendLine("          DockerRegistry__RepositoryPrefix: ${{ github.repository_owner }}");
        sb.AppendLine("          DockerRegistry__RegistryUsername: ${{ github.actor }}");
        sb.AppendLine("          DockerRegistry__RegistryPassword: ${{ secrets.GITHUB_TOKEN }}");
        sb.AppendLine("          IMAGE_TAG_SUFFIX: build.${{ github.run_number }}.${{ env.SHORT_SHA }}");

        // Add detected parameters
        if (options.Parameters.Count > 0)
        {
            foreach (var param in options.Parameters)
            {
                var ghRef = param.IsSecret
                    ? $"${{{{ secrets.{param.GitHubName} }}}}"
                    : $"${{{{ vars.{param.GitHubName} }}}}";
                sb.AppendLine($"          {param.EnvVarName}: {ghRef}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Options for generating a GitHub Actions workflow.
/// </summary>
internal record WorkflowGenerationOptions(
    string EnvironmentName,
    string DotNetVersion,
    string AppHostPath,
    List<ParameterInfo> Parameters,
    SshAuthType SshAuthType);

/// <summary>
/// SSH authentication type for the workflow.
/// </summary>
internal enum SshAuthType
{
    /// <summary>SSH key without passphrase.</summary>
    Key,
    /// <summary>SSH key with passphrase.</summary>
    KeyWithPassphrase,
    /// <summary>Password authentication.</summary>
    Password
}

/// <summary>
/// Information about a parameter resource.
/// </summary>
/// <param name="Parameter">The parameter resource.</param>
/// <param name="Value">The resolved value of the parameter.</param>
/// <param name="ExistsInGitHub">Whether this parameter already exists in GitHub.</param>
internal record ParameterInfo(ParameterResource Parameter, string? Value, bool ExistsInGitHub)
{
    /// <summary>
    /// Gets the configuration key (e.g., "Parameters:cache_password" or "ConnectionStrings:mydb").
    /// </summary>
    public string ConfigurationKey => Parameter.IsConnectionString
        ? $"ConnectionStrings:{Parameter.Name}"
        : $"Parameters:{Parameter.Name}";

    /// <summary>
    /// Gets whether this parameter is a secret.
    /// </summary>
    public bool IsSecret => Parameter.Secret;

    /// <summary>
    /// Gets the GitHub-compatible name by replacing : and - with _.
    /// </summary>
    public string GitHubName => ConfigurationKey.Replace(":", "_").Replace("-", "_").ToUpperInvariant();

    /// <summary>
    /// Gets the environment variable name for the workflow (e.g., "Parameters__cache_password").
    /// </summary>
    public string EnvVarName => ConfigurationKey.Replace(":", "__");
}
