using System.Text.Json;
using System.Text.Json.Serialization;
using Slate.Models;

namespace Slate.Services.Storage;

/// <summary>
/// Shape of an exported configuration file. Deliberately not the settings object itself:
/// secrets are left out, and a version lets the format move later without breaking old files.
/// </summary>
public sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public string ExportedBy { get; set; } = "Slate";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    public AdoSettings? Ado { get; set; }
    public EntraSettings? Entra { get; set; }
    public CalendarSettings? Calendar { get; set; }
    public PlanningSettings? Planning { get; set; }
    public UiSettings? Ui { get; set; }
}

public sealed record ImportResult(bool Success, string Message, IReadOnlyList<string> Applied);

/// <summary>
/// Reads and writes the app's configuration as a portable JSON file, so a setup can be
/// shared with a colleague or carried to another machine.
/// </summary>
public sealed class ConfigTransfer(SettingsStore store)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The personal access token is never exported. It is encrypted to this Windows account,
    /// so it would be useless elsewhere, and a config file is something people email around.
    /// </summary>
    public string Serialize()
    {
        var current = store.Current;
        var copy = store.CreateDraft();
        copy.Ado.PersonalAccessToken = "";

        return JsonSerializer.Serialize(new ConfigFile
        {
            Ado = copy.Ado,
            Entra = current.Entra,
            Calendar = current.Calendar,
            Planning = current.Planning,
            Ui = current.Ui,
        }, Json);
    }

    public void ExportTo(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, Serialize());
    }

    /// <summary>
    /// Applies whichever sections the file contains, leaving the rest untouched. The existing
    /// personal access token always survives an import.
    /// </summary>
    public ImportResult Import(string json)
    {
        ConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConfigFile>(json, Json);
        }
        catch (JsonException ex)
        {
            return new ImportResult(false, $"That file is not valid JSON. {ex.Message}", []);
        }

        if (file is null)
            return new ImportResult(false, "That file did not contain any configuration.", []);

        if (file.Version > 1)
            return new ImportResult(false,
                $"That file was written by a newer version of the app (format {file.Version}).", []);

        var settings = store.CreateDraft();
        var applied = new List<string>();

        if (file.Ado is { } ado)
        {
            var token = settings.Ado.PersonalAccessToken;
            settings.Ado = ado;
            settings.Ado.PersonalAccessToken = token;
            applied.Add("Azure DevOps connection and query");
        }

        if (file.Entra is { } entra)
        {
            settings.Entra = entra;
            applied.Add("Entra ID application");
        }

        if (file.Calendar is { } calendar)
        {
            settings.Calendar = calendar;
            applied.Add("Calendar event defaults");
        }

        if (file.Planning is { } planning)
        {
            settings.Planning = planning;
            applied.Add("Working week and planning");
        }

        if (file.Ui is { } ui)
        {
            settings.Ui = ui;
            applied.Add("Appearance");
        }

        if (applied.Count == 0)
            return new ImportResult(false, "That file did not contain any recognised settings.", []);

        store.Save(settings);
        return new ImportResult(true, "Settings imported.", applied);
    }

    public ImportResult ImportFrom(string path)
    {
        try
        {
            return Import(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            return new ImportResult(false, $"Could not read that file. {ex.Message}", []);
        }
    }

    /// <summary>A sensible default filename for an export.</summary>
    public static string SuggestedFileName =>
        $"slate-config-{DateTime.Now:yyyy-MM-dd}.json";
}
