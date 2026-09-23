using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Slate.Services;

namespace Slate;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BlazorView.Services = App.Services;
        SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar();
            WatchForAbandonedSessionEnd();
        };

        // Closing between the two renames of an update would leave no Slate.exe behind, and
        // closing before the new copy is up would leave nothing to put the old one back if
        // the new one fails. Both are over within the update's own time limit, and the
        // update bar says Slate is restarting by itself, so the close is simply refused.
        // Signing out or shutting down ignores a refused close, so App settles the update
        // itself before those go ahead (SelfUpdater.SettleBeforeExit).
        Closing += (_, e) => e.Cancel |= SelfUpdater.IsSwapping || SelfUpdater.IsHandingOver;
    }

    /// <summary>Windows saying whether the session end it asked about is actually going ahead.</summary>
    private const int WmEndSession = 0x0016;

    /// <summary>
    /// Watches for WM_ENDSESSION with wParam false - the sign-out or shutdown Windows asked
    /// about was abandoned, because another app refused it or the user pressed Cancel.
    ///
    /// WPF raises SessionEnding for the question but has nothing to say about the answer, and
    /// the answer is what tells this copy it is not ending after all; see
    /// <see cref="SelfUpdater.SessionEndAbandoned"/> for what turns on knowing that. Hooked
    /// here because this is the window with a handle of its own to hook, and because the
    /// handler takes no lock and waits on nothing: WM_ENDSESSION is sent rather than posted,
    /// so whatever it does is done with the message loop stopped.
    /// </summary>
    private void WatchForAbandonedSessionEnd()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        HwndSource.FromHwnd(handle)?.AddHook(
            (IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
            {
                if (message == WmEndSession && wParam == IntPtr.Zero) SelfUpdater.SessionEndAbandoned();
                return IntPtr.Zero;
            });
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

