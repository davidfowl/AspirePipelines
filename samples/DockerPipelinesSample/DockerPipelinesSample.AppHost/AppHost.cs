#pragma warning disable ASPIREPIPELINES003 // WithDeploymentImageTag is experimental

// Sample: SSH Deployment to Remote Docker Host
// Demonstrates: custom image tagging, YARP reverse proxy, optional HTTPS with Let's Encrypt
//
// Key settings:
// - IMAGE_TAG_SUFFIX: Custom image tag (e.g., "build.42.abc1234")
// - EnableHttps: Enable HTTPS with Let's Encrypt (requires domain + email parameters)
// - DockerRegistry__*: Registry URL, prefix, and optional credentials

var builder = DistributedApplication.CreateBuilder(args);

var imageTag = builder.Configuration["IMAGE_TAG_SUFFIX"];

// Configure Docker Compose environment with SSH deployment
// Registry is automatically configured from DockerRegistry:* settings
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

// Apply custom image tag from CI/CD to all services
if (!string.IsNullOrEmpty(imageTag))
{
    apiService.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
    frontend.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
    yarp.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
}

if (builder.ExecutionContext.IsPublishMode)
{
    var enableHttps = bool.TryParse(builder.Configuration["EnableHttps"], out var v) && v;

    if (enableHttps)
    {
        var domain = builder.AddParameter("domain");
        var letsEncryptEmail = builder.AddParameter("letsencrypt-email");

        // Certbot container for automatic Let's Encrypt certificate generation.
        // How it works:
        // 1. Certbot starts and binds to port 80 for the ACME HTTP-01 challenge
        // 2. Let's Encrypt verifies domain ownership by requesting /.well-known/acme-challenge/
        // 3. Certificates are stored in a shared Docker volume (/etc/letsencrypt)
        // 4. Certbot fixes permissions so non-root containers can read the certs, then exits
        // 5. On subsequent deploys, --keep-until-expiring skips renewal if certs are still valid
        var certbot = builder.AddContainer("certbot", "certbot/certbot")
            .WithVolume("letsencrypt", "/etc/letsencrypt")
            .WithHttpEndpoint(port: 80, targetPort: 80)
            .WithExternalHttpEndpoints()
            .WithArgs(context =>
            {
                context.Args.Add("certonly");
                context.Args.Add("--standalone");
                context.Args.Add("--non-interactive");
                context.Args.Add("--agree-tos");
                context.Args.Add("-v");
                context.Args.Add("--keep-until-expiring");
                context.Args.Add("--deploy-hook");
                context.Args.Add("chmod -R 755 /etc/letsencrypt/live && chmod -R 755 /etc/letsencrypt/archive");
                context.Args.Add("--email");
                context.Args.Add(letsEncryptEmail.Resource);
                context.Args.Add("-d");
                context.Args.Add(domain.Resource);
            });

        // YARP waits for certbot to complete since both need port 80.
        // Certbot runs the ACME challenge first, then YARP takes over for traffic.
        yarp.WaitForCompletion(certbot);
        yarp.WithHostPort(80);
        yarp.WithHttpsEndpoint(443);
        yarp.WithVolume("letsencrypt", "/etc/letsencrypt", isReadOnly: true);

        // Configure Kestrel to use the Let's Encrypt certificates
        yarp.WithEnvironment(context =>
        {
            context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] =
                ReferenceExpression.Create($"/etc/letsencrypt/live/{domain}/fullchain.pem");
            context.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] =
                ReferenceExpression.Create($"/etc/letsencrypt/live/{domain}/privkey.pem");

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
        // HTTP only mode - expose YARP on port 80
        yarp.WithHostPort(80);
    }

    yarp.WithExternalHttpEndpoints();
}

builder.Build().Run();
