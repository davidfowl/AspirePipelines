using Org.BouncyCastle.Crypto.Agreement.Srp;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithDashboard(db => db.WithHostPort(8085))
    .WithSshDeploySupport()
    // This will be skipped if certs does not exist
    .WithAppFileTransfer("certs", "certs");

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
    .WaitFor(apiService);

var yarp = builder.AddYarp("gateway")
      .WithConfiguration(c =>
      {
          c.AddRoute(frontend);
      });

if (builder.ExecutionContext.IsPublishMode)
{
    // In publish mode, expose YARP on port 80
    yarp.WithHostPort(80);

    // Now wire up TLS if the certs exist
    var certPath = Path.Combine(builder.AppHostDirectory, "certs");

    if (Directory.Exists(certPath))
    {
        yarp.WithHttpsEndpoint(443);

        yarp.WithEnvironment(context =>
        {
            context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = "/app/certs/app.pem";
            context.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] = "/app/certs/app.key";

            context.EnvironmentVariables["HTTP_PORTS"] = yarp.GetEndpoint("http").Property(EndpointProperty.TargetPort);
            context.EnvironmentVariables["HTTPS_PORTS"] = yarp.GetEndpoint("https").Property(EndpointProperty.TargetPort);
        });

        yarp.WithBindMount("./certs", "/app/certs");

        // Work around being able to use relative paths for the source path
        yarp.PublishAsDockerComposeService((svc, infra) =>
        {
            infra.Volumes[0].Source = "./certs";
        });
    }

    yarp.WithExternalHttpEndpoints();
}

builder.Build().Run();
