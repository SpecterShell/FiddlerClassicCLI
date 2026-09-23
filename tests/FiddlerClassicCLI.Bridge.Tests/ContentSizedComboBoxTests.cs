// Measures the real GDI font and edit rectangle under native font changes and DPI-aware window creation.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BindFitsTheFontActuallyUsedByTheNativeControl(bool enabled) => RunOnSta(() =>
    {
        using var bind = new ContentSizedComboBox { Enabled = enabled };
        bind.Items.AddRange(new object[] { "loopback", "all" });
        bind.SelectedIndex = 0;
        bind.CreateControl();
        var originalFont = SendMessage(bind.Handle, 0x0031, IntPtr.Zero, IntPtr.Zero); // WM_GETFONT
        using var largeFont = new Font(bind.Font.FontFamily, bind.Font.SizeInPoints * 2);
        var nativeFont = largeFont.ToHfont();
        try
        {
            // A host can assign a native font without changing the managed Font property.
            SendMessage(bind.Handle, 0x0030, nativeFont, new IntPtr(1)); // WM_SETFONT
            AssertNativeChoicesFit(bind);
            bind.Width = 50;
            bind.PerformLayout();
            AssertNativeChoicesFit(bind);
        }
        finally
        {
            SendMessage(bind.Handle, 0x0030, originalFont, new IntPtr(1));
            DeleteObject(nativeFont);
        }
    });

    [Theory]
    [InlineData(-1)] // DPI unaware
    [InlineData(-2)] // System aware, matching the Application compatibility override
    [InlineData(-4)] // Per-monitor v2
    public void BindFitsNativeTextAtTheMonitorDpi(int awareness) => RunOnSta(() =>
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(awareness));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            using var panel = new BridgeControlPanel(new FakeHostControlClient()) { Size = new Size(640, 1000) };
            panel.CreateControl();
            PumpUntilIdle(panel);
            CompleteRefresh(panel);
            var bind = Named<ComboBox>(panel, "Listener bind mode");
            AssertNativeChoicesFit(bind);
            bind.Enabled = false;
            AssertNativeChoicesFit(bind);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    });

    private static void AssertNativeChoicesFit(ComboBox bind)
    {
        var info = new ComboBoxInfo { Size = Marshal.SizeOf(typeof(ComboBoxInfo)) };
        Assert.True(GetComboBoxInfo(bind.Handle, ref info));
        var dc = GetDC(bind.Handle);
        Assert.NotEqual(IntPtr.Zero, dc);
        var font = SendMessage(bind.Handle, 0x0031, IntPtr.Zero, IntPtr.Zero);
        var previous = SelectObject(dc, font);
        try
        {
            foreach (var choice in bind.Items.Cast<object>())
            {
                var item = choice.ToString()!;
                Assert.True(GetTextExtentPoint32(dc, item, item.Length, out var size));
                Assert.True(size.Width + 4 <= info.Item.Right - info.Item.Left,
                    $"'{item}' uses {size.Width}px of native GDI text plus padding, but only {info.Item.Right - info.Item.Left}px is available.");
            }
            Assert.True(bind.DropDownWidth >= bind.Width);
        }
        finally
        {
            SelectObject(dc, previous);
            ReleaseDC(bind.Handle, dc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out Size size);
}
