using System.Runtime.InteropServices;

namespace MeetTape;

internal sealed class RecordingTaskbar : IDisposable
{
    private readonly Action<bool> set;
    private readonly ITaskbarList3? native;
    private readonly nint icon;
    private bool? state;
    private bool disposed;
    private bool ready;
    internal bool Applied { get; private set; }

    internal RecordingTaskbar(Action<bool> set, bool ready = true) { this.set = set; this.ready = ready; }

    internal RecordingTaskbar(nint window)
    {
        native = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), true)!)!;
        try
        {
            byte[] pixels = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x - 7.5, 2) + Math.Pow(y - 7.5, 2));
                byte alpha = (byte)(255 * Math.Clamp(7 - distance, 0, 1));
                int offset = (y * 16 + x) * 4;
                pixels[offset] = (byte)(48 * alpha / 255); pixels[offset + 1] = (byte)(48 * alpha / 255);
                pixels[offset + 2] = (byte)(230 * alpha / 255); pixels[offset + 3] = alpha;
            }
            icon = CreateIcon(0, 16, 16, 1, 32, new byte[32], pixels);
            if (icon == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            bool initialized = false;
            set = active =>
            {
                if (!initialized) { native.HrInit(); initialized = true; }
                native.SetOverlayIcon(window, active ? icon : 0, active ? "Recording" : "");
            };
        }
        catch { Marshal.FinalReleaseComObject(native); throw; }
    }

    internal void Update(bool active)
    {
        if (disposed || state == active) return;
        state = active;
        if (ready) Apply();
        else if (App.Smoke) Console.Error.WriteLine("Taskbar overlay waiting for button creation.");
    }

    internal void Restore(bool created = true)
    {
        if (created) ready = true;
        if (!disposed && ready && state.HasValue) Apply();
    }
    private void Apply()
    {
        try { set(state == true); Applied = true; }
        catch (Exception error)
        {
            Applied = false;
            if (App.Smoke) Console.Error.WriteLine($"Taskbar overlay failed: {error.GetType().Name}, HRESULT 0x{error.HResult:X8}.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Update(false); disposed = true;
        if (icon != 0) _ = DestroyIcon(icon);
        if (native is not null) Marshal.FinalReleaseComObject(native);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bits, byte[] mask, byte[] pixels);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, uint state);
        void RegisterTab(nint tab, nint parent);
        void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint before);
        void SetTabActive(nint tab, nint parent, uint reserved);
        void ThumbBarAddButtons(nint window, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint window, uint count, nint buttons);
        void ThumbBarSetImageList(nint window, nint images);
        void SetOverlayIcon(nint window, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }
}
