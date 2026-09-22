using Microsoft.Extensions.Configuration;
using SqlAgent.Core;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public sealed class FileStorageConfigurationTests
{
    [Fact]
    public void Resolve_binds_provider_and_max_bytes_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlAgent:Files:Provider"] = "local-disk",
                ["SqlAgent:Files:MaxBytes"] = "1234",
            })
            .Build();

        var options = FileStorageConfiguration.Resolve(configuration);

        Assert.Equal("local-disk", options.Provider);
        Assert.Equal(1234, options.MaxBytes);
        Assert.Equal(10, options.MaxAttachmentsPerMessage);
    }

    [Fact]
    public void Resolve_uses_documented_defaults_when_files_configuration_is_missing()
    {
        var options = FileStorageConfiguration.Resolve(new ConfigurationBuilder().Build());

        Assert.Equal(new FileStorageOptions(), options);
    }
}
