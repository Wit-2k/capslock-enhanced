using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Remake;

/// <summary>以纯 Win32 隐藏窗口承载消息循环和托盘退出菜单。</summary>
internal sealed unsafe class NativeWindow : IDisposable
{
    private const string WindowClassName = "CapsLockPlusRemakeWorkerWindow";
    private const string WindowTitle = "CapsLock+ mini";
    private const uint TrayCallbackMessage = 0x8001;
    private const uint ExitCommand = 1;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmNull = 0;

    private static NativeWindow? _activeWindow;
    // CsWin32 将 WNDPROC 生成为委托；静态字段保证回调不会被 GC 回收。
    private static readonly WNDPROC WindowProcedureCallback = WindowProcedure;
    private readonly HINSTANCE _instance;
    private HWND _handle;
    private HICON _icon;
    private bool _trayIconAdded;
    private bool _disposed;

    public NativeWindow()
    {
        HMODULE module = PInvoke.GetModuleHandle(default(PCWSTR));
        _instance = new HINSTANCE(module.Value);
        if (module.Value is null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandle 失败。");

        RegisterClassAndCreateWindow();
        AddTrayIcon();
    }

    public int Run()
    {
        MSG message = default;
        while (true)
        {
            BOOL result = PInvoke.GetMessage(&message, default, 0, 0);
            if (result.Value == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage 失败。");
            if (result.Value == 0)
                return unchecked((int)message.wParam.Value);

            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    private void RegisterClassAndCreateWindow()
    {
        _activeWindow = this;
        fixed (char* classNamePointer = WindowClassName)
        fixed (char* titlePointer = WindowTitle)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = WindowProcedureCallback,
                hInstance = _instance,
                lpszClassName = new PCWSTR(classNamePointer),
            };

            if (PInvoke.RegisterClassEx(in windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx 失败。");

            _handle = PInvoke.CreateWindowEx(
                (WINDOW_EX_STYLE)0,
                new PCWSTR(classNamePointer),
                new PCWSTR(titlePointer),
                (WINDOW_STYLE)0,
                0, 0, 0, 0,
                default,
                default,
                _instance,
                null);
        }

        if (_handle.Value is null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx 失败。");
    }

    private void AddTrayIcon()
    {
        // 使用 Windows 共享应用图标，避免子仓库依赖父目录里的资源文件。
        HANDLE loadedIcon = PInvoke.LoadImage(
            default,
            new PCWSTR((char*)32512),
            GDI_IMAGE_TYPE.IMAGE_ICON,
            0,
            0,
            IMAGE_FLAGS.LR_SHARED | IMAGE_FLAGS.LR_DEFAULTSIZE);
        if (loadedIcon.Value is null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "加载系统托盘图标失败。");

        _icon = new HICON(loadedIcon.Value);
        NOTIFYICONDATAW data = CreateNotifyIconData();
        if (PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data).Value == 0)
        {
            _icon = default;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Shell_NotifyIcon(NIM_ADD) 失败。");
        }

        _trayIconAdded = true;
        AppLog.Info("系统托盘图标已就绪。");
    }


    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        // 托盘提示只显示产品名，不包含用户输入或其他敏感内容。
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = 1,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon,
            szTip = "CapsLock+ mini（右键退出）",
        };
    }

    private void ShowExitMenu()
    {
        HMENU menu = PInvoke.CreatePopupMenu();
        if (menu.Value is null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePopupMenu 失败。");

        try
        {
            fixed (char* exitText = "退出")
            {
                if (PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)ExitCommand, new PCWSTR(exitText)).Value == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "AppendMenu 失败。");
            }

            PInvoke.SetForegroundWindow(_handle);
            if (PInvoke.GetCursorPos(out System.Drawing.Point cursor).Value == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos 失败。");

            uint selected = unchecked((uint)PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD,
                cursor.X,
                cursor.Y,
                0,
                _handle,
                null).Value);
            PInvoke.PostMessage(_handle, WmNull, default, default);
            if (selected == ExitCommand)
                PInvoke.DestroyWindow(_handle);
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private void HandleWindowMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == TrayCallbackMessage)
        {
            uint notification = unchecked((uint)lParam.Value) & 0xFFFF;
            if (notification == WmRightButtonUp || notification == WmContextMenu)
                ShowExitMenu();
            return;
        }

        if (message == WmCommand && unchecked((uint)wParam.Value) == ExitCommand)
        {
            PInvoke.DestroyWindow(_handle);
            return;
        }

        if (message == WmClose)
        {
            PInvoke.DestroyWindow(_handle);
            return;
        }

        if (message == WmDestroy)
        {
            RemoveTrayIcon();
            _handle = default;
            PInvoke.PostQuitMessage(0);
        }
    }

    private void RemoveTrayIcon()
    {
        if (_trayIconAdded)
        {
            NOTIFYICONDATAW data = CreateNotifyIconData();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _trayIconAdded = false;
            AppLog.Info("系统托盘图标已移除。");
        }

        _icon = default;
    }

    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (_activeWindow is not null)
            {
                _activeWindow.HandleWindowMessage(message, wParam, lParam);
                if (message == TrayCallbackMessage || message == WmCommand || message == WmClose || message == WmDestroy)
                    return default;
            }
        }
        catch (Exception ex)
        {
            // 异常不能穿越原生回调边界；记录后交给系统默认窗口过程。
            AppLog.Error("隐藏窗口消息处理失败", ex);
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_handle.Value is not null)
            PInvoke.DestroyWindow(_handle);
        RemoveTrayIcon();
        _activeWindow = null;
    }
}
