using System.Runtime.InteropServices;

namespace GodotMcp.Dotnet.Shim;

/// <summary><c>GDExtensionInitialization</c>, gdextension_interface.json (4.7.2-stable): a level, userdata, two callbacks.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GDExtensionInitialization
{
    public int MinimumInitializationLevel;
    public void* Userdata;
    public delegate* unmanaged<void*, int, void> Initialize;
    public delegate* unmanaged<void*, int, void> Deinitialize;
}
