#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class DeploymentFileUtility
{
    public static Task<bool> VerifyDeploymentFiles(string outputPath)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new InvalidOperationException("Deployment output path not available");
        }

        // Check for both .yml and .yaml extensions
        var dockerComposePathYml = Path.Combine(outputPath, "docker-compose.yml");
        var dockerComposePathYaml = Path.Combine(outputPath, "docker-compose.yaml");
        var envPath = Path.Combine(outputPath, ".env");

        if (!File.Exists(dockerComposePathYml) && !File.Exists(dockerComposePathYaml))
        {
            throw new InvalidOperationException($"docker-compose.yml or docker-compose.yaml not found in {outputPath}");
        }

        // .env file is optional - return whether it exists
        var envExists = File.Exists(envPath);
        return Task.FromResult(envExists);
    }
}
