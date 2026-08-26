using System.Text.Json;
using System.Text.Json.Serialization;
using Slate.Models;

namespace Slate.Services.Storage;

/// <summary>Loads and saves <see cref="AppSettings"/>, keeping the PAT encrypted on disk.</summary>
public sealed class SettingsStore(SecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private AppSettings? _cached;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= Load();
            }
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json)
                           ?? new AppSettings();

            settings.Normalize();
            settings.Ado.PersonalAccessToken = protector.Unprotect(settings.Ado.PersonalAccessToken);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file should not stop the app from opening on the Settings page.
            return new AppSettings();
        }
    }

    /// <summary>
    /// A deep copy for the Settings page to edit, so nothing takes effect until Save is pressed.
    /// </summary>
    public AppSettings CreateDraft()
    {
        var current = Current;
        var draft = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, Json), Json)!;
        draft.Ado.PersonalAccessToken = current.Ado.PersonalAccessToken;
        return draft;
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureCreated();

        // Every write goes through here, including an imported configuration, so this is
        // the one place that can promise the rest of the app a usable set of values.
        settings.Normalize();

        // Serialize a copy so the in-memory PAT stays plaintext for the running session.
        var onDisk = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        onDisk.Ado.PersonalAccessToken = protector.Protect(settings.Ado.PersonalAccessToken);

        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(onDisk, Json));
        File.Move(temp, AppPaths.SettingsFile, overwrite: true);

        lock (_gate)
        {
            _cached = settings;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Changes one preference in place and writes it out. For small choices made in passing,
    /// such as which format the comment box is in, where taking a full draft and asking the
    /// user to press Save would be heavier than the change deserves.
    /// </summary>
    public void Update(Action<AppSettings> change)
    {
        var settings = Current;
        change(settings);
        Save(settings);
    }

    /// <summary>Raised after a successful save so open pages can re-read configuration.</summary>
    public event Action? Changed;
}
