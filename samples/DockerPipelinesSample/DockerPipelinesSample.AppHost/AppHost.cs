#pragma warning disable ASPIRECOMPUTE001 // WithDeploymentImageTag is experimental

// Sample demonstrating SSH deployment to a remote Docker host with:
// - Custom image tagging from CI/CD
// - Let's Encrypt certificate generation via certbot
// - YARP reverse proxy with HTTPS

var builder = DistributedApplication.CreateBuilder(args);

// Custom image tag from CI/CD (e.g., IMAGE_TAG_SUFFIX=build.42.abc1234)
var imageTag = builder.Configuration["IMAGE_TAG_SUFFIX"];

// Configure Docker Compose environment with SSH deployment support
builder.AddDockerComposeEnvironment("env")
    .WithDashboard(db => db.WithHostPort(8085))
    .WithSshDeploySupport();

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
    var enableHttps = bool.TryParse(builder.Configuration["EnableHttps"], out var v) && v;

    if (enableHttps)
    {
        // Let's Encrypt parameters
        var domain = builder.AddParameter("domain");
        var letsEncryptEmail = builder.AddParameter("letsencrypt-email");

        // Certbot container for Let's Encrypt certificates
        var certbot = builder.AddContainer("certbot", "certbot/certbot")
            .WithVolume("letsencrypt", "/etc/letsencrypt")
            .WithHttpEndpoint(port: 80, targetPort: 80) // Required for standalone HTTP-01 challenge
            .WithExternalHttpEndpoints() // Publish port 80 to host for ACME challenge
            .WithArgs(context =>
            {
                context.Args.Add("certonly");
                context.Args.Add("--standalone");
                context.Args.Add("--non-interactive");
                context.Args.Add("--agree-tos");
                context.Args.Add("-v");
                context.Args.Add("--keep-until-expiring"); // Skip if valid cert exists and not near expiry
                context.Args.Add("--deploy-hook");
                context.Args.Add("chmod -R 755 /etc/letsencrypt/live && chmod -R 755 /etc/letsencrypt/archive");
                context.Args.Add("--email");
                context.Args.Add(letsEncryptEmail.Resource);
                context.Args.Add("-d");
                context.Args.Add(domain.Resource);
            });

        // YARP needs port 80 after certbot completes, so certbot binds 80 during challenge
        // This requires certbot to run first and exit before YARP starts
        yarp.WaitForCompletion(certbot);

        yarp.WithHostPort(80);
        yarp.WithHttpsEndpoint(443);

        // Mount the shared letsencrypt volume (read-only for YARP)
        yarp.WithVolume("letsencrypt", "/etc/letsencrypt", isReadOnly: true);

        // Configure Kestrel to use Let's Encrypt certificates
        yarp.WithEnvironment(context =>
        {
            // Certificate paths - domain is interpolated
            context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] =
                ReferenceExpression.Create($"/etc/letsencrypt/live/{domain}/fullchain.pem");
            context.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] =
                ReferenceExpression.Create($"/etc/letsencrypt/live/{domain}/privkey.pem");

            // Configure URLs for both HTTP and HTTPS
            var httpEndpoint = yarp.GetEndpoint("http");
            var httpsEndpoint = yarp.GetEndpoint("https");

            var reb = new ReferenceExpressionBuilder();
            reb.Append($"http://+:{httpEndpoint.Property(EndpointProperty.TargetPort)}");
            reb.AppendLiteral(";");
            reb.Append($"https://+:{httpsEndpoint.Property(EndpointProperty.TargetPort)}");

            context.EnvironmentVariables["ASPNETCORE_URLS"] = reb.Build();
        });
    }
    else
    {
        // HTTP only
        yarp.WithHostPort(80);
    }

    yarp.WithExternalHttpEndpoints();
}

builder.Build().Run();
