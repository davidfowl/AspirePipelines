using Aspire.Hosting.Docker.SshDeploy.Utilities;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class ServiceUrlFormatterTests
{
    // Tests for IsIpAddress

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    [InlineData("fe80::1")]
    public void IsIpAddress_ReturnsTrue_ForIpAddresses(string host)
    {
        Assert.True(ServiceUrlFormatter.IsIpAddress(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("myapp.example.com")]
    [InlineData("localhost")]
    [InlineData("my-server.local")]
    [InlineData("server-01")]
    public void IsIpAddress_ReturnsFalse_ForDomainNames(string host)
    {
        Assert.False(ServiceUrlFormatter.IsIpAddress(host));
    }

    // Tests for CanShowTargetHost

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
        // IP addresses are masked by default
        var config = new ConfigurationBuilder().Build();
        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsTrue_WhenHostIsDomainName()
    {
        // Domain names are shown
        var config = new ConfigurationBuilder().Build();
        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "myapp.example.com"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    public void CanShowTargetHost_ReturnsTrue_WhenUnsafeShowHostSet_EvenForIp(string value)
    {
        // UNSAFE_SHOW_TARGET_HOST=true/1 forces showing even IPs
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UNSAFE_SHOW_TARGET_HOST"] = value
            })
            .Build();

        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void CanShowTargetHost_StillMasksIp_WhenUnsafeShowHostNotTrueOr1(string value)
    {
        // Other values don't override the IP detection
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UNSAFE_SHOW_TARGET_HOST"] = value
            })
            .Build();

        Assert.False(ServiceUrlFormatter.CanShowTargetHost(config, "192.168.1.100"));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsTrue_ForDomain_WithoutEnvVar()
    {
        // Domain names are shown without any env var
        var config = new ConfigurationBuilder().Build();
        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "deploy.mycompany.com"));
    }

    [Fact]
    public void CanShowTargetHost_ReturnsTrue_ForLocalhost()
    {
        // localhost is not an IP, so it's shown
        var config = new ConfigurationBuilder().Build();
        Assert.True(ServiceUrlFormatter.CanShowTargetHost(config, "localhost"));
    }
}
