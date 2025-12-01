using System.Text;
using Aspire.Hosting.Docker.SshDeploy.Services;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class SSHOutputParserTests
{
    [Fact]
    public void WrapCommand_ProducesCorrectFormat()
    {
        // Act
        var wrapped = SSHOutputParser.WrapCommand("ls -la");

        // Assert - command is wrapped in a group with stderr redirected to stdout
        Assert.Equal(
            "echo ___ASPIRE_CMD_START___; { ls -la; __ec=$?; } 2>&1; echo ___ASPIRE_EXIT_CODE___$__ec; echo ___ASPIRE_CMD_END___",
            wrapped);
    }

    [Fact]
    public void ParseOutput_ExtractsSimpleOutput()
    {
        // Arrange
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "hello world",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("hello world", output);
    }

    [Fact]
    public void ParseOutput_ExtractsMultiLineOutput()
    {
        // Arrange
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "line 1",
            "line 2",
            "line 3",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("line 1\nline 2\nline 3", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ParseOutput_ExtractsNonZeroExitCode()
    {
        // Arrange
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "error: file not found",
            "___ASPIRE_EXIT_CODE___1",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(1, exitCode);
        Assert.Equal("error: file not found", output);
    }

    [Fact]
    public void ParseOutput_HandlesLargeExitCode()
    {
        // Arrange
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "___ASPIRE_EXIT_CODE___255",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(255, exitCode);
        Assert.Empty(output);
    }

    [Fact]
    public void ParseOutput_IgnoresLinesBeforeStartMarker()
    {
        // Arrange - simulates bash prompt echo before marker
        var lines = new[]
        {
            "echo ___ASPIRE_CMD_START___; ls; __ec=$?; echo ___ASPIRE_EXIT_CODE___$__ec; echo ___ASPIRE_CMD_END___",
            "___ASPIRE_CMD_START___",
            "file.txt",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("file.txt", output);
    }

    [Fact]
    public void ParseOutput_HandlesEmptyOutput()
    {
        // Arrange
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Empty(output);
    }

    [Fact]
    public void ParseOutput_HandlesOutputContainingMarkerLikeText()
    {
        // Arrange - output that looks similar to markers but isn't exact
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "some text with ___ASPIRE in it",
            "and EXIT_CODE text too",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("some text with ___ASPIRE in it", output);
        Assert.Contains("and EXIT_CODE text too", output);
    }

    [Fact]
    public void ProcessLine_ReturnsTrueOnEndMarker()
    {
        // Arrange
        bool foundStart = true;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        var result = SSHOutputParser.ProcessLine(
            "___ASPIRE_CMD_END___",
            ref foundStart,
            ref exitCode,
            outputBuilder);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ProcessLine_ReturnsFalseOnStartMarker()
    {
        // Arrange
        bool foundStart = false;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        var result = SSHOutputParser.ProcessLine(
            "___ASPIRE_CMD_START___",
            ref foundStart,
            ref exitCode,
            outputBuilder);

        // Assert
        Assert.False(result);
        Assert.True(foundStart);
    }

    [Fact]
    public void ProcessLine_ExtractsExitCode()
    {
        // Arrange
        bool foundStart = true;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        var result = SSHOutputParser.ProcessLine(
            "___ASPIRE_EXIT_CODE___42",
            ref foundStart,
            ref exitCode,
            outputBuilder);

        // Assert
        Assert.False(result);
        Assert.Equal(42, exitCode);
    }

    [Fact]
    public void ProcessLine_AppendsOutputAfterStartMarker()
    {
        // Arrange
        bool foundStart = true;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        SSHOutputParser.ProcessLine("hello", ref foundStart, ref exitCode, outputBuilder);
        SSHOutputParser.ProcessLine("world", ref foundStart, ref exitCode, outputBuilder);

        // Assert
        var output = outputBuilder.ToString().TrimEnd('\r', '\n').Replace("\r\n", "\n");
        Assert.Equal("hello\nworld", output);
    }

    [Fact]
    public void ProcessLine_IgnoresOutputBeforeStartMarker()
    {
        // Arrange
        bool foundStart = false;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        SSHOutputParser.ProcessLine("ignored line", ref foundStart, ref exitCode, outputBuilder);

        // Assert
        Assert.Empty(outputBuilder.ToString());
    }

    [Fact]
    public void ProcessLine_ReturnsFalseOnNull()
    {
        // Arrange
        bool foundStart = true;
        int exitCode = 0;
        var outputBuilder = new StringBuilder();

        // Act
        var result = SSHOutputParser.ProcessLine(
            null,
            ref foundStart,
            ref exitCode,
            outputBuilder);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ProcessLine_HandlesInvalidExitCode()
    {
        // Arrange
        bool foundStart = true;
        int exitCode = 99;
        var outputBuilder = new StringBuilder();

        // Act
        SSHOutputParser.ProcessLine(
            "___ASPIRE_EXIT_CODE___notanumber",
            ref foundStart,
            ref exitCode,
            outputBuilder);

        // Assert - exit code should remain unchanged
        Assert.Equal(99, exitCode);
    }

    [Theory]
    [InlineData("whoami", "root", 0)]
    [InlineData("cat /nonexistent", "cat: /nonexistent: No such file or directory", 1)]
    [InlineData("pwd", "/home/user", 0)]
    public void ParseOutput_HandlesRealWorldCommands(string command, string expectedOutput, int expectedExitCode)
    {
        // Arrange - simulate what real bash output would look like
        var lines = new[]
        {
            $"echo ___ASPIRE_CMD_START___; {command}; __ec=$?; echo ___ASPIRE_EXIT_CODE___$__ec; echo ___ASPIRE_CMD_END___",
            "___ASPIRE_CMD_START___",
            expectedOutput,
            $"___ASPIRE_EXIT_CODE___{expectedExitCode}",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(expectedOutput, output);
    }

    [Fact]
    public void ParseOutput_HandlesDockerComposeOutput()
    {
        // Arrange - simulates docker compose ls output
        var lines = new[]
        {
            "___ASPIRE_CMD_START___",
            "NAME                STATUS              CONFIG FILES",
            "aspire-app          running(2)          /home/user/aspire/docker-compose.yaml",
            "___ASPIRE_EXIT_CODE___0",
            "___ASPIRE_CMD_END___"
        };

        // Act
        var (exitCode, output) = SSHOutputParser.ParseOutput(lines);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("NAME", output);
        Assert.Contains("aspire-app", output);
    }
}
