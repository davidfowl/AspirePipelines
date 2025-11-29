#pragma warning disable ASPIRECOMPUTE001 // WithDeploymentImageTag is experimental

// Sample demonstrating SSH deployment to a remote Docker host with:
// - Custom image tagging from CI/CD
// - Optional TLS certificate transfer
// - YARP reverse proxy with HTTPS

var builder = DistributedApplication.CreateBuilder(args);

// Custom image tag from CI/CD (e.g., IMAGE_TAG_SUFFIX=build.42.abc1234)
var imageTag = builder.Configuration["IMAGE_TAG_SUFFIX"];

// Configure Docker Compose environment with SSH deployment support
builder.AddDockerComposeEnvironment("env")
    .WithDashboard(db => db.WithHostPort(8085))
    .WithSshDeploySupport()
    .WithAppFileTransfer("certs", "certs"); // Skipped if certs folder doesn't exist

var p = builder.AddParameter("p");

var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.DockerPipelinesSample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("P_ENV", p);

var frontend = builder.AddProject<Projects.DockerPipelinesSample_Web>("webfrontend")
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithEnvironment("IMAGE_TAG_SUFFIX", imageTag ?? "local");

var yarp = builder.AddYarp("gateway")
      .WithConfiguration(c =>
      {
          c.AddRoute(frontend);
      });

// Apply the image tag to all services if specified
if (!string.IsNullOrEmpty(imageTag))
{
    apiService.WithDeploymentImageTag(_ => imageTag);
    frontend.WithDeploymentImageTag(_ => imageTag);
    yarp.WithDeploymentImageTag(_ => imageTag);
}

if (builder.ExecutionContext.IsPublishMode)
{
    yarp.WithHostPort(80);

    // Configure HTTPS if TLS certificates exist
    var certPath = Path.Combine(builder.AppHostDirectory, "certs");

    if (Directory.Exists(certPath))
    {
        yarp.WithHttpsEndpoint(443);

        yarp.WithEnvironment(context =>
        {
            context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = "/app/certs/app.pem";
            context.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] = "/app/certs/app.key";

            var httpEndpoint = yarp.GetEndpoint("http");
            var httpsEndpoint = yarp.GetEndpoint("https");

            var reb = new ReferenceExpressionBuilder();
            reb.Append($"http://+:{httpEndpoint.Property(EndpointProperty.TargetPort)}");
            reb.AppendLiteral(";");
            reb.Append($"https://+:{httpsEndpoint.Property(EndpointProperty.TargetPort)}");

            context.EnvironmentVariables["ASPNETCORE_URLS"] = reb.Build();
        });

        yarp.WithBindMount("./certs", "/app/certs");

        // Fix relative path for Docker Compose volume
        yarp.PublishAsDockerComposeService((svc, infra) => infra.Volumes[0].Source = "./certs");
    }

    yarp.WithExternalHttpEndpoints();
}

builder.Build().Run();
