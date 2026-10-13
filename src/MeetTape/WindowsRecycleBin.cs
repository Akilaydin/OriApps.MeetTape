using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace MeetTape;

internal static class WindowsRecycleBin
{
    internal static string Recycle(string directory, Guid? expectedId = null)
    {
        string? destination = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { destination = RecycleCore(directory, expectedId); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "MeetTape recycle recording" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return destination!;
    }

    private static string RecycleCore(string directory, Guid? expectedId)
    {
        IShellItem? source = null, parent = null, bin = null, recycled = null;
        object? handler = null;
        try
        {
            var itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, 0, ref itemId, out source));
            source.GetParent(out parent);
            var transferId = typeof(ITransferSource).GUID;
            var transferHandler = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
            parent.BindToHandler(0, ref transferHandler, ref transferId, out handler);
            var binId = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(ref binId, 0, 0, ref itemId, out bin));
            if (expectedId is { } id) SessionRemoval.ValidateRecording(directory, id);
            // RecycleItem has a recycle destination; unlike DeleteItem it has no permanent-delete fallback.
            Marshal.ThrowExceptionForHR(((ITransferSource)handler).RecycleItem(source, bin, 0, out recycled));
            if (recycled is null) throw new IOException("Windows did not return a recycled item.");
            recycled.GetDisplayName(0x80058000, out nint name); // SIGDN_FILESYSPATH
            try { return Marshal.PtrToStringUni(name) ?? throw new IOException("No recycled location returned."); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally
        {
            foreach (object? value in new object?[] { recycled, handler, bin, parent, source })
                if (value is not null) Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id, out IShellItem item);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(ref Guid folder, uint flags, nint token, ref Guid id, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hints, out int order);
    }

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetProperties(nint properties);
        void OpenItem(IShellItem item, uint flags, ref Guid id, out nint result);
        void MoveItem(IShellItem item, IShellItem parent, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
        [PreserveSig] int RecycleItem(IShellItem source, IShellItem parent, uint flags, out IShellItem result);
    }
}
