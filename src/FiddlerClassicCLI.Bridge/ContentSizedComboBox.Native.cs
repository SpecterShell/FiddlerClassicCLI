// Measures dropdown choices with the exact HFONT and device context used by the native control.
using System.Drawing;
using System.Runtime.InteropServices;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class ContentSizedComboBox
{
    /// <summary>Returns the widest GDI text extent plus scaled padding, restoring every borrowed GDI object.</summary>
    /// <param name="fallback">Managed text width used if the native context cannot be measured.</param>
    private int MeasureNativeChoices(int fallback)
    {
        var dc = GetDC(Handle);
        if (dc == IntPtr.Zero) return fallback;
        var font = SendMessage(Handle, 0x0031, IntPtr.Zero, IntPtr.Zero); // WM_GETFONT
        var previous = font == IntPtr.Zero ? IntPtr.Zero : SelectObject(dc, font);
        try
        {
            var width = 0;
            var height = 0;
            foreach (var item in Items.Cast<object>())
            {
                var text = item.ToString();
                if (!GetTextExtentPoint32(dc, text, text.Length, out var size)) return fallback;
                width = Math.Max(width, size.Width);
                height = Math.Max(height, size.Height);
            }
            return width + Math.Max(6, height / 3);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            ReleaseDC(Handle, dc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out Size size);
}
