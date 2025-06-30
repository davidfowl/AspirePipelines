var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.DockerPipelinesSample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.DockerPipelinesSample_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
