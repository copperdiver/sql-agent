using Microsoft.Extensions.Configuration;
using SqlAgent.Core;

namespace SqlAgent.Host.Web;

public static class FileStorageConfiguration
{
    public const string SectionName = "SqlAgent:Files";

    public static FileStorageOptions Resolve(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).Get<ConfigurableFileStorageOptions>();
        return configured is null
            ? new FileStorageOptions()
            : new FileStorageOptions(configured.Provider, configured.MaxBytes);
    }

    private sealed class ConfigurableFileStorageOptions
    {
        public string Provider { get; set; } = "local-disk";
        public long MaxBytes { get; set; } = 25 * 1024 * 1024;
    }
}
