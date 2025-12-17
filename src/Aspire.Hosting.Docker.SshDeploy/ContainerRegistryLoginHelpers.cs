#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Helper methods for container registry login operations.
/// </summary>
internal static class ContainerRegistryLoginHelpers
{
    /// <summary>
    /// Logs in to the container registry using stored credentials.
    /// </summary>
    /// <param name="registry">The container registry resource.</param>
    /// <param name="context">The pipeline step context.</param>
    public static async Task LoginToRegistryAsync(ContainerRegistryResource registry, PipelineStepContext context)
    {
        var logger = context.Logger;
        var step = context.ReportingStep;
        var cancellationToken = context.CancellationToken;

        // Get the container runtime for login
        var containerRuntime = context.Services.GetRequiredService<IContainerRuntime>();

        // Get the registry endpoint via IContainerRegistry interface
        IContainerRegistry containerRegistry = registry;
        var endpoint = await containerRegistry.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(endpoint))
        {
            logger.LogDebug("Registry endpoint is empty, skipping login (local registry)");
            await step.SucceedAsync("Local registry - no login required");
            return;
        }

        // Try to get credentials from annotations
        string? username = null;
        string? password = null;

        if (registry.TryGetLastAnnotation<ContainerRegistryCredentialsAnnotation>(out var credentials))
        {
            // Parameters can hold literal values or be prompted during deployment
            // If parameters aren't configured, GetValueAsync will throw - treat as no credentials
            try
            {
                username = await credentials.Username.GetValueAsync(context.CancellationToken);
                password = await credentials.Password.GetValueAsync(context.CancellationToken);
            }
            catch (DistributedApplicationException ex)
            {
                logger.LogDebug(ex, "Could not resolve registry credentials, skipping login");
            }
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            logger.LogDebug("No credentials configured for registry {Endpoint}, skipping login", endpoint);
            await step.SucceedAsync($"No credentials for {endpoint}");
            return;
        }

        await using var loginTask = await step.CreateTaskAsync($"Logging in to {endpoint}", cancellationToken);

        logger.LogDebug("Logging in to registry {Endpoint} as {Username}", endpoint, username);

        await containerRuntime.LoginToRegistryAsync(
            endpoint,
            username,
            password,
            cancellationToken).ConfigureAwait(false);

        await loginTask.SucceedAsync($"Authenticated with {endpoint}", cancellationToken);
    }
}
