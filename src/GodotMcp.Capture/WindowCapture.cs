using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GodotMcp.Capture;

/// <summary>
/// A Windows.Graphics.Capture session on one window: the frame pool at the window's size, a staging texture at the crop's
/// size that the crop of the newest frame is copied into on the GPU, and a read-back of that texture as top-down BGRA rows
/// packed at the output size (<see cref="Crop.OutputWidth"/> by <see cref="Crop.OutputHeight"/>).
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private const string SessionTypeName = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const int BytesPerPixel = 4;

    private readonly GraphicsCaptureItem _item;
    private readonly Gpu _gpu;
    private readonly Box _region;
    private readonly nint _hwnd;
    private volatile bool _closed;

    private WindowCapture(nint hwnd, GraphicsCaptureItem item, PixelRect crop, Gpu gpu)
    {
        _item = item;
        _gpu = gpu;
        _hwnd = hwnd;
        ItemSize = item.Size;
        CropRect = crop;
        _region = new Box(crop.X, crop.Y, 0, crop.X + crop.Width, crop.Y + crop.Height, 1);
        // The event is kept for a process that pumps messages, where it fires as soon as the window is gone; this console
        // process never pumps, so IsClosed relies on the IsWindow poll beside it instead.
        _item.Closed += (_, _) => _closed = true;
    }

    /// <summary>The capture item's size when the capture was created: the window with its frame; every frame is this size.</summary>
    public SizeInt32 ItemSize { get; }

    /// <summary>The part of each frame the capture copies.</summary>
    public PixelRect CropRect { get; }

    /// <summary>True once the window has closed: no frame arrives after it. Read on every tick of a run.</summary>
    public bool IsClosed => _closed || !WindowFinder.IsWindow(_hwnd);

    public int FrameBytes => Crop.OutputWidth(CropRect) * Crop.OutputHeight(CropRect) * BytesPerPixel;

    /// <summary>
    /// The capture of <paramref name="hwnd"/>, copying <paramref name="requestedCrop"/> of it, or all of it when that is
    /// null. Returns null, with the reason in <paramref name="refusal"/>, when the crop reaches outside the capture item.
    /// </summary>
    /// <exception cref="CaptureStartException">The capture item, the D3D11 device or the frame pool could not be created.</exception>
    public static WindowCapture? Create(nint hwnd, PixelRect? requestedCrop, out string? refusal)
    {
        GraphicsCaptureItem item = CreateItem(hwnd);
        PixelRect? crop = Crop.Resolve(requestedCrop, item.Size.Width, item.Size.Height, out refusal);
        return crop is { } resolved ? new WindowCapture(hwnd, item, resolved, Gpu.Open(item, resolved)) : null;
    }

    /// <summary>Starts the session; throws <see cref="CaptureStartException"/> when the engine refuses to start it.</summary>
    public void Start()
    {
        try
        {
            _gpu.Session!.StartCapture();
        }
        catch (Exception failure) when (failure is SharpGenException or COMException)
        {
            throw new CaptureStartException("starting the capture session", failure);
        }
    }

    /// <summary>
    /// Drains the pool, copies the crop of the newest frame into <paramref name="buffer"/> and returns true; returns false
    /// when no frame arrived since the last call, so the caller writes the previous one again. Throws
    /// <see cref="WindowResizedException"/> when the window's content size no longer matches the pool, and
    /// <see cref="DeviceLostException"/> when the device is lost while the frame is read back.
    /// </summary>
    public bool TryCopyLatestFrame(byte[] buffer)
    {
        try
        {
            Direct3D11CaptureFrame? newest = null;
            while (_gpu.FramePool!.TryGetNextFrame() is { } frame)
            {
                newest?.Dispose();
                newest = frame;
            }
            if (newest is null)
            {
                return false;
            }
            using (newest)
            {
                SizeInt32 contentSize = newest.ContentSize;
                if (contentSize.Width != ItemSize.Width || contentSize.Height != ItemSize.Height)
                {
                    throw new WindowResizedException(ItemSize, contentSize);
                }
                using ID3D11Texture2D texture = CaptureInterop.GetTexture(newest.Surface);
                ReadBack(texture, buffer);
            }
            return true;
        }
        catch (Exception failure) when (failure is SharpGenException or COMException)
        {
            throw new DeviceLostException("reading the newest frame back", failure);
        }
    }

    public void Dispose() => _gpu.Dispose();

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        try
        {
            return CaptureInterop.CreateItemForWindow(hwnd);
        }
        catch (Exception failure) when (failure is COMException or ArgumentException or UnauthorizedAccessException)
        {
            throw new CaptureStartException($"creating the capture item for window {hwnd}", failure);
        }
    }

    private void ReadBack(ID3D11Texture2D texture, byte[] buffer)
    {
        ID3D11DeviceContext context = _gpu.Context!;
        ID3D11Texture2D staging = _gpu.Staging!;
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, _region);
        MappedSubresource mapped;
        try
        {
            mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        }
        catch (SharpGenException failure)
        {
            throw new DeviceLostException("mapping the staging texture", failure);
        }
        try
        {
            CopyRows(mapped, buffer);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// The mapped texture pads each row to its pitch; the output wants rows at the output width, edge to edge. Only the
    /// crop's own pixels are written, so an odd size's pad column and row keep the zeros the buffer was allocated with.
    /// </summary>
    private void CopyRows(MappedSubresource mapped, byte[] buffer)
    {
        int contentBytes = CropRect.Width * BytesPerPixel;
        int outputBytes = Crop.OutputWidth(CropRect) * BytesPerPixel;
        int pitch = (int)mapped.RowPitch;
        for (int row = 0; row < CropRect.Height; row++)
        {
            Marshal.Copy(mapped.DataPointer + row * pitch, buffer, row * outputBytes, contentBytes);
        }
    }

    /// <summary>
    /// The GPU side of a capture, created in order and disposed in reverse; a creation that fails part-way disposes what it
    /// had already made.
    /// </summary>
    private sealed class Gpu : IDisposable
    {
        public ID3D11Device? Device { get; private set; }

        public ID3D11DeviceContext? Context { get; private set; }

        public IDirect3DDevice? WinRtDevice { get; private set; }

        public ID3D11Texture2D? Staging { get; private set; }

        public Direct3D11CaptureFramePool? FramePool { get; private set; }

        public GraphicsCaptureSession? Session { get; private set; }

        public static Gpu Open(GraphicsCaptureItem item, PixelRect crop)
        {
            Gpu gpu = new();
            try
            {
                gpu.Create(item, crop);
                return gpu;
            }
            catch (Exception failure) when (failure is SharpGenException or COMException or ArgumentException or UnauthorizedAccessException)
            {
                gpu.Dispose();
                throw new CaptureStartException("creating the D3D11 device and the capture's frame pool", failure);
            }
        }

        public void Dispose()
        {
            Session?.Dispose();
            FramePool?.Dispose();
            Staging?.Dispose();
            WinRtDevice?.Dispose();
            Context?.Dispose();
            Device?.Dispose();
        }

        private void Create(GraphicsCaptureItem item, PixelRect crop)
        {
            Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            Context = Device.ImmediateContext;
            WinRtDevice = CaptureInterop.CreateWinRtDevice(Device);
            Staging = Device.CreateTexture2D(
                new Texture2DDescription(
                    Format.B8G8R8A8_UNorm,
                    (uint)crop.Width,
                    (uint)crop.Height,
                    arraySize: 1,
                    mipLevels: 1,
                    bindFlags: BindFlags.None,
                    usage: ResourceUsage.Staging,
                    cpuAccessFlags: CpuAccessFlags.Read
                )
            );
            FramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            Session = FramePool.CreateCaptureSession(item);
            ConfigureSession(Session);
        }

        private static void ConfigureSession(GraphicsCaptureSession session)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) && ApiInformation.IsPropertyPresent(SessionTypeName, "IsBorderRequired"))
            {
                session.IsBorderRequired = false;
            }
            if (ApiInformation.IsPropertyPresent(SessionTypeName, "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = false;
            }
        }
    }
}
