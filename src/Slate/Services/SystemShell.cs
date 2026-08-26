using System.Diagnostics;

namespace Slate.Services;

/// <summary>Hands links to the OS rather than opening them inside the embedded WebView.</summary>
public static class SystemShell
{
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No default browser registered - nothing useful to do.
        }
    }

    /// <summary>
    /// Native save dialog. Blazor runs on the WPF dispatcher here, but the dialog still has
    /// to be raised on the UI thread explicitly.
    /// </summary>
    public static string? AskWhereToSave(string suggestedName)
    {
        return OnUiThread(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export configuration",
                FileName = suggestedName,
                DefaultExt = ".json",
                Filter = "Configuration file (*.json)|*.json|All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true,
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
    }

    public static string? AskWhatToOpen()
    {
        return OnUiThread(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import configuration",
                DefaultExt = ".json",
                Filter = "Configuration file (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
    }

    private static string? OnUiThread(Func<string?> show)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return show();

        return app.Dispatcher.CheckAccess()
            ? show()
            : app.Dispatcher.Invoke(show);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }
}
