using System.Security.Cryptography;
using System.Text;

namespace Cmux.Core;

/// <summary>
/// Optional process scope for development and isolated tests. The default scope
/// deliberately preserves the production pipe names used by existing installs.
/// </summary>
public static class InstanceScope
{
    public const string EnvironmentVariable = "CMUX_INSTANCE_ID";
    private static string? _configuredId;

    public static string Id => _configuredId ?? Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static void Configure(string? instanceId)
    {
        _configuredId = Normalize(instanceId);
        Environment.SetEnvironmentVariable(EnvironmentVariable, _configuredId);
    }

    public static string Qualify(string baseName)
    {
        var id = Id;
        return id.Length == 0 ? baseName : $"{baseName}-{Token(id)}";
    }

    public static string Token(string? instanceId = null)
    {
        var id = Normalize(instanceId ?? Id);
        if (id.Length == 0) return "default";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > 128)
            throw new ArgumentException("Instance id must be 128 characters or fewer.", nameof(value));
        return normalized;
    }
}
