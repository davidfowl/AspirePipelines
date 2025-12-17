#pragma warning disable ASPIREPIPELINES003 // WithDeploymentImageTag is experimental
#pragma warning disable ASPIRECOMPUTE003 // ContainerRegistryResource is experimental

// =============================================================================
// Sample: SSH Deployment to Remote Docker Host
// =============================================================================
//
// This sample demonstrates deploying an Aspire application to a remote Docker
// host via SSH with the following features:
//
// - Custom image tagging from CI/CD pipelines
// - Container registry integration with automatic login
// - YARP reverse proxy as the public entry point
// - Optional HTTPS with automatic Let's Encrypt certificate generation
//
// Configuration (appsettings.json or environment variables):
// ----------------------------------------------------------
// - EnableHttps: Set to "true" to enable HTTPS with Let's Encrypt certificates.
//                When false (default), only HTTP on port 80 is exposed.
//
// Required parameters for deployment:
// - Parameters__registry-endpoint: Registry URL (e.g., "registry.digitalocean.com")
// - Parameters__registry-repository: Repository prefix (e.g., "my-project")
// - Parameters__registry-username: Registry username
// - Parameters__registry-password: Registry password (secret)
//
// Required parameters when EnableHttps=true:
// - Parameters__domain: The domain name for the certificate (e.g., "example.com")
// - Parameters__letsencrypt_email: Email for Let's Encrypt registration/notifications
//
// How container registry integration works:
// -----------------------------------------
// 1. A ContainerRegistryResource is created with endpoint and repository
// 2. WithCredentialsLogin adds a login step that runs before push-prereq
// 3. Built-in push steps (push-apiservice, push-webfrontend) push to the registry
// 4. Remote deployment authenticates with the same credentials to pull images
//
// How HTTPS works:
// ----------------
// When EnableHttps is true, a certbot container is added that:
// 1. Runs the Let's Encrypt ACME HTTP-01 challenge on port 80
// 2. Obtains/renews certificates and stores them in a shared Docker volume
// 3. Sets permissions so non-root containers can read the certificates
// 4. Exits after certificate generation
//
// YARP then starts (after certbot completes) and:
// - Mounts the shared certificate volume (read-only)
// - Serves HTTPS on port 443 using the Let's Encrypt certificates
// - Serves HTTP on port 80
//
// On subsequent deployments, certbot skips certificate generation if valid
// certificates already exist (--keep-until-expiring flag).
//
// =============================================================================

var builder = DistributedApplication.CreateBuilder(args);

// Custom image tag from CI/CD (e.g., IMAGE_TAG_SUFFIX=build.42.abc1234)
var imageTag = builder.Configuration["IMAGE_TAG_SUFFIX"];

// Container registry configuration for push/pull operations
// These parameters are prompted during deployment or can be set via config
var registryEndpoint = builder.AddParameter("registry-endpoint");
var registryRepository = builder.AddParameter("registry-repository");

var registryUsername = builder.AddParameter("registry-username");
var registryPassword = builder.AddParameter("registry-password", secret: true);

// Create container registry with automatic login
// This creates a login-to-registry step that runs before built-in push steps
var registry = builder.AddContainerRegistry("registry", registryEndpoint, registryRepository)
    .WithCredentialsLogin(registryUsername, registryPassword);

// Configure Docker Compose environment with SSH deployment support
builder.AddDockerComposeEnvironment("env")
    .WithDashboard(db => db.WithHostPort(8085))
    .WithSshDeploySupport()
    .WithContainerRegistry(registry);

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
    apiService.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
    frontend.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
    yarp.WithImagePushOptions(c => c.Options.RemoteImageTag = imageTag);
}

if (builder.ExecutionContext.IsPublishMode)
{
    var enableHttps = bool.TryParse(builder.Configuration["EnableHttps"], out var v) && v;

    if (enableHttps)
    {
        // Let's Encrypt parameters (required when EnableHttps=true)
        // Set via: Parameters__domain and Parameters__letsencrypt_email
        var domain = builder.AddParameter("domain");
        var letsEncryptEmail = builder.AddParameter("letsencrypt-email");

        // Certbot container for automatic Let's Encrypt certificate generation
        // Uses the official certbot/certbot image to obtain and renew certificates.
        //
        // Certbot arguments explained:
        // - certonly: Only obtain the certificate, don't install it
        // - --standalone: Run a temporary webserver for the ACME HTTP-01 challenge
        // - --non-interactive: Don't prompt for user input
        // - --agree-tos: Agree to Let's Encrypt Terms of Service
        // - --keep-until-expiring: Skip if cert exists and is not within 30 days of expiry
        // - --deploy-hook: Command to run after successful cert issuance (fix permissions)
        // - --email: Email for urgent renewal/security notices
        // - -d: Domain name for the certificate
        //
        var certbot = builder.AddContainer("certbot", "certbot/certbot")
            // Shared volume for certificates - both certbot and YARP mount this
            .WithVolume("letsencrypt", "/etc/letsencrypt")
            // Port 80 must be published to host for Let's Encrypt to reach the ACME challenge
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
                // Fix permissions so non-root containers (like YARP) can read the certs
                context.Args.Add("--deploy-hook");
                context.Args.Add("chmod -R 755 /etc/letsencrypt/live && chmod -R 755 /etc/letsencrypt/archive");
                context.Args.Add("--email");
                context.Args.Add(letsEncryptEmail.Resource);
                context.Args.Add("-d");
                context.Args.Add(domain.Resource);
            });

        // Certbot and YARP both need port 80, but at different times:
        // - Certbot needs port 80 during the ACME challenge (runs first, then exits)
        // - YARP needs port 80 for HTTP traffic (starts after certbot completes)
        // WaitForCompletion ensures YARP doesn't start until certbot exits successfully
        yarp.WaitForCompletion(certbot);

        yarp.WithHostPort(80);
        yarp.WithHttpsEndpoint(443);

        // Mount the shared certificate volume (read-only since YARP only reads certs)
        yarp.WithVolume("letsencrypt", "/etc/letsencrypt", isReadOnly: true);

        // Configure Kestrel to use the Let's Encrypt certificates
        // Certbot stores certs at: /etc/letsencrypt/live/{domain}/
        yarp.WithEnvironment(context =>
        {
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
