using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace GodotMcp.Dotnet.Loader;

/// <summary>
/// Loads the helper into the load context that holds the game's <c>GodotSharp</c> (GodotPlugins' own), so the helper binds
/// the game's <c>GodotSharp</c> rather than a copy, and runs its <c>Install</c>.
/// </summary>
public static class Entry
{
    private const int SupportedMajor = 4;
    private const int SupportedMinor = 7;
    private const string HelperType = "GodotMcp.Dotnet.Helper";
    private const string HelperInstall = "Install";

    /// <summary>
    /// Called by the shim with the helper's path. Returns 0 on success, else the length of the one-line reason written
    /// into <paramref name="error"/> (at most <paramref name="capacity"/> characters).
    /// </summary>
    [UnmanagedCallersOnly]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An exception crossing into native code takes the game down."
    )]
    public static unsafe int Run(char* helperPath, char* error, int capacity)
    {
        string? failure;
        try
        {
            failure = Load(new string(helperPath));
        }
        catch (Exception e)
        {
            Exception cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            failure = $"the loader threw {cause.GetType().Name}: {cause.Message}";
        }

        if (failure is null)
        {
            return 0;
        }

        int length = Math.Min(failure.Length, capacity);
        failure.AsSpan(0, length).CopyTo(new Span<char>(error, capacity));
        return length;
    }

    private static string? Load(string helperPath)
    {
        (AssemblyLoadContext context, Assembly godotSharp)? found = FindGodotSharp();
        if (found is not { } godot)
        {
            return "no load context holds GodotSharp";
        }

        Version? version = godot.godotSharp.GetName().Version;
        if (version is null || version.Major != SupportedMajor || version.Minor != SupportedMinor)
        {
            return $"GodotSharp {version} is loaded; this helper is built for {SupportedMajor}.{SupportedMinor}";
        }

        Assembly helper = godot.context.LoadFromAssemblyPath(helperPath);
        MethodInfo install =
            helper.GetType(HelperType, throwOnError: true)!.GetMethod(HelperInstall, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(HelperType, HelperInstall);
        _ = install.Invoke(null, null);
        return null;
    }

    private static (AssemblyLoadContext, Assembly)? FindGodotSharp()
    {
        foreach (AssemblyLoadContext context in AssemblyLoadContext.All)
        {
            Assembly? godotSharp = context.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == "GodotSharp");
            if (godotSharp is not null)
            {
                return (context, godotSharp);
            }
        }

        return null;
    }
}
