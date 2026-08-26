using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Slate;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BlazorView.Services = App.Services;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    /// <summary>
    /// Paints the Win32 title bar dark so the chrome matches the app's dark theme.
    /// Silently no-ops on Windows builds that predate the DWM attribute.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            int useDark = 1;
            // 20 is the modern attribute id; 19 was used on Windows 10 1809-1903.
            if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Not fatal - the app just keeps a light title bar.
        }
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

