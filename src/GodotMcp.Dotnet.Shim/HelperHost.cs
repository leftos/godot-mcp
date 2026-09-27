using System.Runtime.InteropServices;

namespace GodotMcp.Dotnet.Shim;

/// <summary>
/// Starts the loader in the .NET runtime the game already runs, through the <c>hostfxr</c> the game loaded
/// (native-hosting.md: a secondary context on the running runtime), and has it load the helper.
/// </summary>
internal static unsafe class HelperHost
{
    private const int HdtLoadAssemblyAndGetFunctionPointer = 5;
    private const string LoaderType = "GodotMcp.Dotnet.Loader.Entry, GodotMcp.Dotnet.Loader";
    private const string LoaderMethod = "Run";
    private const int ErrorCapacity = 2048;

    // UNMANAGEDCALLERSONLY_METHOD, native-hosting.md: the delegate type argument for an [UnmanagedCallersOnly] method.
    private static readonly char* UnmanagedCallersOnlyMethod = (char*)-1;

    /// <summary>
    /// Loads <c>loader/GodotMcp.Dotnet.Loader.dll</c> from <paramref name="directory"/> and runs it on
    /// <c>helper/GodotMcp.Dotnet.dll</c>. Returns null on success, else a one-line reason.
    /// </summary>
    public static string? Load(string directory)
    {
        string config = Path.Combine(directory, "loader", "GodotMcp.Dotnet.Loader.runtimeconfig.json");
        string loader = Path.Combine(directory, "loader", "GodotMcp.Dotnet.Loader.dll");
        string helper = Path.Combine(directory, "helper", "GodotMcp.Dotnet.dll");
        if (!File.Exists(config) || !File.Exists(loader))
        {
            return $"the loader is missing beside the extension: {loader}";
        }

        nint hostfxr = NativeMethods.ModuleHandle("hostfxr.dll");
        if (hostfxr == 0)
        {
            return "hostfxr not found in the process";
        }

        string? failure = LoadLoader(hostfxr, config, loader, out nint run);
        return failure ?? RunLoader(run, helper);
    }

    private static string? LoadLoader(nint hostfxr, string config, string loader, out nint run)
    {
        run = 0;
        string? failure = GetRuntimeDelegate(hostfxr, config, out nint load);
        if (failure is not null)
        {
            return failure;
        }

        var loadAssembly = (delegate* unmanaged<char*, char*, char*, char*, void*, nint*, int>)load;
        int status;
        nint function;
        fixed (char* assembly = loader)
        fixed (char* type = LoaderType)
        fixed (char* method = LoaderMethod)
        {
            status = loadAssembly(assembly, type, method, UnmanagedCallersOnlyMethod, null, &function);
        }

        if (status < 0)
        {
            return $"load_assembly_and_get_function_pointer failed: 0x{status:X8}";
        }

        run = function;
        return null;
    }

    /// <summary><c>hdt_load_assembly_and_get_function_pointer</c> of a context made from the loader's runtimeconfig.</summary>
    private static string? GetRuntimeDelegate(nint hostfxr, string config, out nint load)
    {
        load = 0;
        var initialize = (delegate* unmanaged<char*, void*, nint*, int>)Export(hostfxr, "hostfxr_initialize_for_runtime_config");
        var getDelegate = (delegate* unmanaged<nint, int, nint*, int>)Export(hostfxr, "hostfxr_get_runtime_delegate");
        var close = (delegate* unmanaged<nint, int>)Export(hostfxr, "hostfxr_close");

        nint context;
        int status;
        fixed (char* path = config)
        {
            status = initialize(path, null, &context);
        }

        if (status < 0)
        {
            return $"hostfxr_initialize_for_runtime_config failed: 0x{status:X8}";
        }

        nint function;
        status = getDelegate(context, HdtLoadAssemblyAndGetFunctionPointer, &function);
        _ = close(context);
        if (status < 0)
        {
            return $"hostfxr_get_runtime_delegate failed: 0x{status:X8}";
        }

        load = function;
        return null;
    }

    /// <summary>Calls the loader's <c>Run</c>, which writes a reason into the buffer and returns its length, or 0.</summary>
    private static string? RunLoader(nint run, string helper)
    {
        char[] buffer = new char[ErrorCapacity];
        int length;
        fixed (char* path = helper)
        fixed (char* error = buffer)
        {
            length = ((delegate* unmanaged<char*, char*, int, int>)run)(path, error, ErrorCapacity);
        }

        return length == 0 ? null : new string(buffer, 0, Math.Min(length, ErrorCapacity));
    }

    private static nint Export(nint hostfxr, string name) =>
        NativeLibrary.TryGetExport(hostfxr, name, out nint address) ? address : throw new InvalidOperationException($"hostfxr has no export {name}");
}
