using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeskBox.Services;

namespace DeskBox.Tests;

public class UnitTest1
{
    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolderTest
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenuTest
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [Fact]
    public void NativeDesktopBackgroundMenu_QueryContextMenu_PopulatesItems()
    {
        int hrFolder = -1;
        int hrView = -1;
        int hrQuery = -1;
        int itemCount = -1;

        var thread = new Thread(() =>
        {
            using var host = new HwndSource(new HwndSourceParameters("DeskBoxTestHost") { Width = 0, Height = 0, WindowStyle = 0 });
            IntPtr desktopPtr = IntPtr.Zero;
            IntPtr ctxPtr = IntPtr.Zero;
            IntPtr hMenu = IntPtr.Zero;
            try
            {
                hrFolder = SHGetDesktopFolder(out desktopPtr);
                if (hrFolder == 0 && desktopPtr != IntPtr.Zero)
                {
                    var folder = (IShellFolderTest)Marshal.GetObjectForIUnknown(desktopPtr);
                    var iidCtx = new Guid("000214E4-0000-0000-C000-000000000046");
                    hrView = folder.CreateViewObject(host.Handle, ref iidCtx, out ctxPtr);
                    if (hrView == 0 && ctxPtr != IntPtr.Zero)
                    {
                        var ctx = (IContextMenuTest)Marshal.GetObjectForIUnknown(ctxPtr);
                        hMenu = CreatePopupMenu();
                        hrQuery = ctx.QueryContextMenu(hMenu, 0, 1000, 0x7FFF, 0);
                        itemCount = GetMenuItemCount(hMenu);
                        Marshal.ReleaseComObject(ctx);
                    }
                    Marshal.ReleaseComObject(folder);
                }
            }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
                if (ctxPtr != IntPtr.Zero) Marshal.Release(ctxPtr);
                if (desktopPtr != IntPtr.Zero) Marshal.Release(desktopPtr);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(hrFolder == 0, $"SHGetDesktopFolder failed: 0x{hrFolder:X8}");
        Assert.True(hrView == 0, $"CreateViewObject failed: 0x{hrView:X8}");
        Assert.True(hrQuery >= 0, $"QueryContextMenu failed: 0x{hrQuery:X8}");
        Assert.True(itemCount > 0, $"Expected > 0 menu items, got {itemCount}");
    }

    [Fact]
    public void ShellService_GetIcon_ReturnsHighResolutionBitmapAbove36Px()
    {
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(cmdPath));

        BitmapSource? icon64 = null;
        BitmapSource? icon96 = null;
        Exception? threadEx = null;

        var thread = new Thread(() =>
        {
            try
            {
                var shell = new ShellService();
                icon64 = shell.GetIcon(cmdPath, 64) as BitmapSource;
                icon96 = shell.GetIcon(cmdPath, 96) as BitmapSource;
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
        Assert.NotNull(icon64);
        Assert.NotNull(icon96);
        Assert.True(icon64!.PixelWidth > 36, $"Expected PixelWidth > 36, got {icon64.PixelWidth}");
        Assert.True(icon96!.PixelWidth >= 64, $"Expected PixelWidth >= 64, got {icon96.PixelWidth}");
    }
}
