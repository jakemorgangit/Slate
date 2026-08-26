using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<PlanStore>();
        services.AddSingleton<ConfigTransfer>();

        // Auth
        services.AddSingleton<TokenCacheStore>();
        services.AddSingleton<MsalAuthService>();

        // Back-end clients
        services.AddSingleton<AzureDevOpsClient>();
        services.AddSingleton<GraphCalendarClient>();

        // App state / orchestration
        services.AddSingleton<AppState>();
        services.AddSingleton<PlannerService>();
        services.AddSingleton<ToastService>();

        Services = services.BuildServiceProvider();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLog.Path}",
            "Slate",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
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
