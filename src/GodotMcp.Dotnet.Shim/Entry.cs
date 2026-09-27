using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

[assembly: SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "The assembly name is the native library's file name, godot_mcp_dotnet.dll, which the .gdextension names."
)]

namespace GodotMcp.Dotnet.Shim;

/// <summary>
/// The extension's entry symbol and its initialization callbacks. At the first initialization level at or above SCENE it
/// loads the helper once; a failure is reported through <see cref="ErrorVariable"/>, never thrown into Godot.
/// </summary>
public static unsafe class Entry
{
    /// <summary>The process environment variable a failure's one-line reason is put in, for the bridge to read.</summary>
    public const string ErrorVariable = "GODOT_MCP_DOTNET_ERROR";

    // GDExtensionInitializationLevel, gdextension_interface.json (4.7.2-stable): CORE 0, SERVERS 1, SCENE 2, EDITOR 3.
    private const int SceneLevel = 2;

    private static bool _loaded;

    /// <summary>
    /// <c>GDExtensionInitializationFunction</c>: fills <paramref name="initialization"/> (a
    /// <c>GDExtensionInitialization*</c>) with the level and the callbacks. Returns 1, or 0 when it could not.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "godot_mcp_dotnet_init")]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An exception crossing into native code takes the game down."
    )]
    public static byte Init(nint getProcAddress, nint library, void* initialization)
    {
        try
        {
            var init = (GDExtensionInitialization*)initialization;
            init->MinimumInitializationLevel = SceneLevel;
            init->Userdata = null;
            init->Initialize = &Initialize;
            init->Deinitialize = &Deinitialize;
            return 1;
        }
        catch (Exception e)
        {
            Report($"the extension's init threw {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An exception crossing into native code takes the game down."
    )]
    private static void Initialize(void* userdata, int level)
    {
        try
        {
            if (level < SceneLevel || _loaded)
            {
                return;
            }

            _loaded = true;
            string? failure = HelperHost.Load(ModuleDirectory());
            if (failure is not null)
            {
                Report(failure);
            }
        }
        catch (Exception e)
        {
            Report($"loading the helper threw {e.GetType().Name}: {e.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static void Deinitialize(void* userdata, int level) { }

    /// <summary>
    /// Sets <see cref="ErrorVariable"/> in the process environment, which on Windows is the process's own block that
    /// Godot's <c>OS.get_environment</c> reads, and prints the reason to stderr.
    /// </summary>
    private static void Report(string reason)
    {
        Environment.SetEnvironmentVariable(ErrorVariable, reason);
        Console.Error.WriteLine($"[godot_mcp_dotnet] {reason}");
    }

    /// <summary>The folder this library was loaded from, found from the address of one of its own functions.</summary>
    private static string ModuleDirectory()
    {
        delegate* unmanaged<void*, int, void> self = &Initialize;
        return Path.GetDirectoryName(NativeMethods.ModuleFileName(NativeMethods.ModuleAt((nint)self)))
            ?? throw new InvalidOperationException("the shim's own module path has no folder");
    }
}
