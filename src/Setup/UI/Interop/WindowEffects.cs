using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Setup.UI.Interop;

/// <summary>
/// Applies Windows 11 desktop-window-manager effects — immersive dark title bar,
/// rounded corners and the Mica system backdrop — to a WPF window. Every call is
/// wrapped so it degrades silently on OS builds that do not support an attribute.
/// </summary>
public static class WindowEffects
{
    // DwmSetWindowAttribute attribute identifiers.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_WINDOW_CORNER_PREFERENCE values.
    private const int DWMWCP_ROUND = 2;

    // DWM_SYSTEMBACKDROP_TYPE values.
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    /// <summary>
    /// Applies dark mode, rounded corners and the Mica backdrop to
    /// <paramref name="window"/>. Safe to call before or after the window handle
    /// exists; if the handle is not yet created it hooks
    /// <see cref="Window.SourceInitialized"/> and applies then. Never throws.
    /// </summary>
    public static void Apply(Window? window)
    {
        if (window is null) return;

        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                // Handle not created yet — defer until it is.
                window.SourceInitialized += OnSourceInitialized;
                return;
            }

            ApplyToHandle(helper.Handle);
        }
        catch
        {
            // DWM unavailable / unsupported build — leave the dark fallback in place.
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
                ApplyToHandle(handle);
        }
        catch
        {
            // Ignore — graceful degradation.
        }
    }

    private static void ApplyToHandle(IntPtr hwnd)
    {
        // Each attribute is independent; failure of one must not block the others.
        TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        TrySetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        TrySetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
    }

    private static void TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            int local = value;
            _ = DwmSetWindowAttribute(hwnd, attribute, ref local, sizeof(int));
        }
        catch
        {
            // Older builds return a non-zero HRESULT or the export is missing;
            // ignore so the app still runs with its solid dark background.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
