var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithDashboard(db => db.WithHostPort(8085))
    .WithSshDeploySupport()
    .WithAppFileTransfer("data", "data");

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
      .WithExternalHttpEndpoints()
      .WithConfiguration(c =>
      {
          c.AddRoute(frontend);
      });

if (builder.ExecutionContext.IsPublishMode)
{
    // In publish mode, expose YARP on port 80
    yarp.WithHostPort(80);
}

builder.Build().Run();
