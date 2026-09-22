using Microsoft.Extensions.Configuration;
using SqlAgent.Core;

namespace SqlAgent.Host.Web;

public static class FileStorageConfiguration
{
    public const string SectionName = "SqlAgent:Files";

    public static FileStorageOptions Resolve(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<FileStorageOptions>() ?? new FileStorageOptions();
}
