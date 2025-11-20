using Aspire.Hosting.Docker.Pipelines.Services;
using Aspire.Hosting.Docker.Pipelines.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Docker.Pipelines.Tests;

public class EnvironmentFileReaderTests
{
    [Fact]
    public async Task ReadEnvironmentFile_ReturnsEmptyDictionary_WhenFileDoesNotExist()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = await utility.ReadEnvironmentFile("/path/to/nonexistent.env");

        // Assert
        Assert.Empty(result);
        Assert.True(fakeFileSystem.WasCalled("FileExists", "/path/to/nonexistent.env"));
    }

    [Fact]
    public async Task ReadEnvironmentFile_ParsesSimpleKeyValuePairs()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        fakeFileSystem.AddFile("/path/to/test.env", "KEY1=value1\nKEY2=value2\nKEY3=value3");

        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = await utility.ReadEnvironmentFile("/path/to/test.env");

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("value1", result["KEY1"]);
        Assert.Equal("value2", result["KEY2"]);
        Assert.Equal("value3", result["KEY3"]);
    }

    [Fact]
    public async Task ReadEnvironmentFile_SkipsEmptyLinesAndComments()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        fakeFileSystem.AddFile("/path/to/test.env", @"
# This is a comment
KEY1=value1

# Another comment
KEY2=value2

");

        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = await utility.ReadEnvironmentFile("/path/to/test.env");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("value1", result["KEY1"]);
        Assert.Equal("value2", result["KEY2"]);
    }

    [Fact]
    public async Task ReadEnvironmentFile_HandlesQuotedValues()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        fakeFileSystem.AddFile("/path/to/test.env", @"
KEY1=""quoted value""
KEY2='single quoted'
KEY3=unquoted
");

        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = await utility.ReadEnvironmentFile("/path/to/test.env");

        // Assert
        Assert.Equal("quoted value", result["KEY1"]);
        Assert.Equal("single quoted", result["KEY2"]);
        Assert.Equal("unquoted", result["KEY3"]);
    }

    [Fact]
    public async Task ReadEnvironmentFile_HandlesValuesWithEquals()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        fakeFileSystem.AddFile("/path/to/test.env", "CONNECTION_STRING=Server=localhost;Database=test");

        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = await utility.ReadEnvironmentFile("/path/to/test.env");

        // Assert
        Assert.Equal("Server=localhost;Database=test", result["CONNECTION_STRING"]);
    }

    [Fact]
    public void ParseEnvironmentContent_ReturnsEmptyDictionary_ForEmptyString()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        var result = utility.ParseEnvironmentContent("");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void IsSensitiveEnvironmentVariable_ReturnsTrueForSensitiveKeys()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act & Assert
        Assert.True(utility.IsSensitiveEnvironmentVariable("PASSWORD"));
        Assert.True(utility.IsSensitiveEnvironmentVariable("API_KEY"));
        Assert.True(utility.IsSensitiveEnvironmentVariable("SECRET_TOKEN"));
        Assert.True(utility.IsSensitiveEnvironmentVariable("DB_PASSWORD"));
        Assert.True(utility.IsSensitiveEnvironmentVariable("PRIVATE_KEY"));
    }

    [Fact]
    public void IsSensitiveEnvironmentVariable_ReturnsFalseForNonSensitiveKeys()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act & Assert
        Assert.False(utility.IsSensitiveEnvironmentVariable("DATABASE_URL"));
        Assert.False(utility.IsSensitiveEnvironmentVariable("PORT"));
        Assert.False(utility.IsSensitiveEnvironmentVariable("HOST"));
        Assert.False(utility.IsSensitiveEnvironmentVariable("LOG_LEVEL"));
    }

    [Fact]
    public async Task ReadEnvironmentFile_RecordsFileSystemCalls()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        fakeFileSystem.AddFile("/path/to/test.env", "KEY=value");

        var utility = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);

        // Act
        await utility.ReadEnvironmentFile("/path/to/test.env");

        // Assert
        Assert.Equal(2, fakeFileSystem.Calls.Count);
        Assert.True(fakeFileSystem.WasCalled("FileExists", "/path/to/test.env"));
        Assert.True(fakeFileSystem.WasCalled("ReadAllTextAsync", "/path/to/test.env"));
    }
}
