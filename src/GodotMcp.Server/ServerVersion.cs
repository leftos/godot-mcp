using System.Reflection;

namespace GodotMcp.Server;

/// <summary>
/// The server's version, <c>&lt;semver&gt;+&lt;7-char commit sha&gt;</c> (<c>0.1.0+cae43a3</c>), or the bare semver when it was
/// built outside a git checkout: the assembly's informational version, which Directory.Build.props composes.
/// </summary>
internal static class ServerVersion
{
    public static string Value { get; } =
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("godot-mcp was built without an AssemblyInformationalVersionAttribute.");
}
