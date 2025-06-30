#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class DeploymentFileUtility
{
    public static Task<bool> VerifyDeploymentFiles(DeployingContext context)
    {
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            throw new InvalidOperationException("Deployment output path not available");
        }

        // Check for both .yml and .yaml extensions
        var dockerComposePathYml = Path.Combine(context.OutputPath, "docker-compose.yml");
        var dockerComposePathYaml = Path.Combine(context.OutputPath, "docker-compose.yaml");
        var envPath = Path.Combine(context.OutputPath, ".env");

        if (!File.Exists(dockerComposePathYml) && !File.Exists(dockerComposePathYaml))
        {
            throw new InvalidOperationException($"docker-compose.yml or docker-compose.yaml not found in {context.OutputPath}");
        }

        // .env file is optional - return whether it exists
        var envExists = File.Exists(envPath);
        return Task.FromResult(envExists);
    }
}
