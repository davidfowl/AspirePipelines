using Aspire.Hosting.Docker.SshDeploy.Utilities;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class ServiceUrlFormatterTests
{
    // Tests for CanShowTargetHost - host is always masked unless UNSAFE_SHOW_TARGET_HOST is set

    [Fact]
    public void CanShowTargetHost_ReturnsFalse_WhenHostIsNull()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, null));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsFalse_WhenHostIsEmpty()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, ""));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsFalse_WhenHostIsIpAddress()
    {
        // All hosts are masked by default
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsFalse_WhenHostIsDomainName()
    {
        // All hosts are masked by default (including domain names)
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "myapp.example.com"));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsFalse_ForLocalhost()
    {
        // All hosts are masked by default (including localhost)
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "localhost"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    public void CanShowTargetHost_ReturnsTrue_WhenUnsafeShowHostSet(string value)
    {
        // UNSAFE_SHOW_TARGET_HOST=true/1 allows showing the host
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UNSAFE_SHOW_TARGET_HOST"] = value
            })
            .Build();

        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "myapp.example.com"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void CanShowTargetHost_ReturnsFalse_WhenUnsafeShowHostNotTrueOr1(string value)
    {
        // Other values don't enable showing the host
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UNSAFE_SHOW_TARGET_HOST"] = value
            })
            .Build();

        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "myapp.example.com"));
    }
}
