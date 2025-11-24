using Aspire.Hosting.Docker.SshDeploy.Utilities;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class PathExpansionUtilityTests
{
    [Fact]
    public void ExpandTildeToHome_ExpandsTildeSlash_ToHomeSlash()
    {
        // Arrange
        var path = "~/aspire/apps/myapp";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("$HOME/aspire/apps/myapp", result);
    }

    [Fact]
    public void ExpandTildeToHome_ExpandsTildeSlash_WithMultipleSegments()
    {
        // Arrange
        var path = "~/some/very/long/path/to/deployment";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("$HOME/some/very/long/path/to/deployment", result);
    }

    [Fact]
    public void ExpandTildeToHome_ExpandsJustTildeSlash()
    {
        // Arrange
        var path = "~/";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("$HOME/", result);
    }

    [Fact]
    public void ExpandTildeToHome_ExpandsJustTilde()
    {
        // Arrange
        var path = "~";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("$HOME", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_AbsolutePath()
    {
        // Arrange
        var path = "/opt/aspire/apps";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("/opt/aspire/apps", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_AlreadyExpandedHome()
    {
        // Arrange
        var path = "$HOME/aspire/apps";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("$HOME/aspire/apps", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_RelativePath()
    {
        // Arrange
        var path = "relative/path/to/app";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("relative/path/to/app", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_DotPath()
    {
        // Arrange
        var path = "./aspire/apps";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("./aspire/apps", result);
    }

    [Fact]
    public void ExpandTildeToHome_HandlesNull_ReturnsNull()
    {
        // Arrange
        string? path = null;

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExpandTildeToHome_HandlesEmptyString_ReturnsEmptyString()
    {
        // Arrange
        var path = "";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_TildeInMiddle()
    {
        // Arrange - tilde not at start should not be expanded
        var path = "/path/to/~something/app";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("/path/to/~something/app", result);
    }

    [Fact]
    public void ExpandTildeToHome_DoesNotExpand_TildeWithoutSlash()
    {
        // Arrange - ~username style paths are intentionally not supported
        // This implementation only expands ~ and ~/ patterns to $HOME
        var path = "~user/app";

        // Act
        var result = PathExpansionUtility.ExpandTildeToHome(path);

        // Assert
        Assert.Equal("~user/app", result);
    }
}
