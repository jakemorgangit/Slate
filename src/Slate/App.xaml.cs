using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Slate.Services;
using Slate.Services.Auth;
using Slate.Services.AzureDevOps;
using Slate.Services.Graph;
using Slate.Services.Planning;
using Slate.Services.Storage;

namespace Slate;

public partial class App : Application
{
    /// <summary>Service provider shared by the WPF shell and every BlazorWebView in it.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _restartedAfterRenderFailure = e.Args.Contains(RestartedFlag);
        SelfUpdater.OnStartup(e.Args);

        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Without a provider here, an unhandled exception inside a Blazor component goes
        // nowhere: the framework logs it, tears the root component down, and the WebView is
        // left blank with the WPF shell still running happily. That reads as "the app
        // crashed" while none of the handlers below ever fire, so route it to the crash log.
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Warning);
            b.AddProvider(new CrashLogLoggerProvider());
        });

        // Storage + settings
        services.AddSingleton<WriteGate>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<PlanStore>();
        services.AddSingleton<WorkItemsCacheStore>();
        services.AddSingleton<ConfigTransfer>();

        // Auth
        services.AddSingleton<TokenCacheStore>();
        services.AddSingleton<MsalAuthService>();

        // Back-end clients
        services.AddSingleton<AzureDevOpsClient>();
        services.AddSingleton<GraphCalendarClient>();

        // App state / orchestration
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<SelfUpdater>();
        services.AddSingleton<AppState>();
        services.AddSingleton<PlannerService>();
        services.AddSingleton<ToastService>();

        Services = services.BuildServiceProvider();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLog.Write(args.ExceptionObject as Exception);
            SelfUpdater.SettleBeforeExit("Slate crashed");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SelfUpdater.SettleBeforeExit("Slate was exiting");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };
    }

    // ---------------------------------------------------------------- ending mid-update

    /// <summary>
    /// Signing out or shutting down ends this copy whatever the window says: WPF shuts down
    /// straight after this returns, ignoring a refused close, and Windows may end the process
    /// the moment it has answered. An update still waiting on its new copy is settled here and
    /// now, before answering, rather than on the way out when there may be no time left.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SelfUpdater.SettleBeforeExit($"Windows began {(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "shutting down" : "signing out")}");
        base.OnSessionEnding(e);
    }

    /// <summary>
    /// Any other shutdown that reaches this copy while an update waits on its new one - the
    /// handover's own comes after it has settled, and so finds nothing to do.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        SelfUpdater.SettleBeforeExit("Slate was shut down");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);

        if (IsRenderThreadFailure(e.Exception) && TryRestartAfterRenderFailure())
        {
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLog.Path}",
            "Slate",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- render thread failure

    /// <summary>
    /// UCEERR_RENDERTHREADFAILURE. WPF's render thread has lost the graphics device - the
    /// display driver reset under it on sleep and resume, docking, a monitor coming or going,
    /// or Remote Desktop - and the window it was drawing does not come back from that.
    /// </summary>
    private const int RenderThreadFailure = unchecked((int)0x88980406);

    /// <summary>Passed to the copy started in our place, so it knows not to do the same.</summary>
    private const string RestartedFlag = "--restarted-after-render-failure";

    /// <summary>
    /// A copy that was itself started by a restart and fails again this soon is failing for
    /// some other reason than a one-off reset, and restarting it again would only loop.
    /// </summary>
    private static readonly TimeSpan RestartGrace = TimeSpan.FromMinutes(2);

    private readonly DateTime _startedAt = DateTime.UtcNow;
    private bool _restartedAfterRenderFailure;
    private bool _restarting;

    private static bool IsRenderThreadFailure(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is COMException { HResult: RenderThreadFailure }) return true;
        return false;
    }

    /// <summary>
    /// Replaces this copy with a fresh one rather than leaving a dead window behind an error
    /// box. Nothing is lost: settings and the plan are written to disk as they change, and
    /// unsent calendar edits are part of the plan. False when a restart is not safe to try.
    /// </summary>
    private bool TryRestartAfterRenderFailure()
    {
        // The failure is raised for each window message that touches the lost device, so
        // several can arrive before the shutdown below takes effect.
        if (_restarting) return true;

        // An update is swapping this copy for another, or this is the new copy and the one
        // that started it may still stop it and put itself back. Starting a copy now would
        // leave two running on the same plan. Treated as handled rather than reported: the
        // update settles within its own time limit, and if this copy is still the one
        // running afterwards, the next window message brings the failure round again.
        if (SelfUpdater.IsHandingOver || SelfUpdater.AwaitingReadySignal) return true;

        if (_restartedAfterRenderFailure && DateTime.UtcNow - _startedAt < RestartGrace) return false;
        if (Environment.ProcessPath is not { Length: > 0 } exe || !File.Exists(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe, RestartedFlag) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return false;
        }

        _restarting = true;
        CrashLog.WriteLine("Restarted after the display driver reset under the window.");
        Shutdown();
        return true;
    }
}

/// <summary>
/// Sends warnings and errors from the framework - Blazor's component exceptions above all -
/// to the same crash log the WPF handlers use.
/// </summary>
internal sealed class CrashLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CrashLogLogger(categoryName);

    public void Dispose() { }

    private sealed class CrashLogLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            CrashLog.WriteLine($"[{level}] {category}: {formatter(state, exception)}");
            if (exception is not null) CrashLog.Write(exception);
        }
    }
}

internal static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(AppPaths.DataDirectory, "crash.log");

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        WriteLine(ex.ToString());
    }

    public static void WriteLine(string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path, $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the thing that takes the app down.
        }
    }
}
