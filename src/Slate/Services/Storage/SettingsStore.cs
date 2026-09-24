using System.Text.Json;
using System.Text.Json.Serialization;
using Slate.Models;

namespace Slate.Services.Storage;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>, keeping the PAT encrypted on disk.
///
/// Every save is counted through <see cref="WriteGate"/> and refused once an update has begun
/// handing over, whichever of the many places that save settings it comes from: the new copy
/// reads the settings as it starts, so one saved here afterwards would never reach it.
/// </summary>
public sealed class SettingsStore(SecretProtector protector, WriteGate writes)
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

    /// <summary>
    /// Makes <paramref name="settings"/> the current settings and writes them out. False, with
    /// nothing changed, when an update is taking over.
    /// </summary>
    public bool Save(AppSettings settings)
    {
        if (!writes.TryEnter()) return false;

        try
        {
            Apply(settings);
        }
        finally
        {
            writes.Exit();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Changes one preference in place and writes it out. For small choices made in passing,
    /// such as which format the comment box is in, where taking a full draft and asking the
    /// user to press Save would be heavier than the change deserves.
    ///
    /// Counted before the change is made rather than only around the write: the change edits
    /// the live settings, so being turned away after it would leave them changed in memory and
    /// not on disk. False, with nothing changed, when an update is taking over.
    /// </summary>
    public bool Update(Action<AppSettings> change)
    {
        if (!writes.TryEnter()) return false;

        try
        {
            var settings = Current;
            change(settings);
            Apply(settings);
        }
        finally
        {
            writes.Exit();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>The save itself, for a caller that has already been counted in.</summary>
    private void Apply(AppSettings settings)
    {
        // Every write goes through here, including an imported configuration, so this is
        // the one place that can promise the rest of the app a usable set of values.
        settings.Normalize();

        DataFolder.Write(AppPaths.SettingsFile, () => WriteFile(settings));

        lock (_gate)
        {
            _cached = settings;
        }
    }

    /// <summary>
    /// Serializes when it runs rather than when the save was asked for, so a save held back
    /// while an update was starting still writes these settings as they are by then.
    /// </summary>
    private void WriteFile(AppSettings settings)
    {
        // Serialize a copy so the in-memory PAT stays plaintext for the running session.
        var onDisk = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        onDisk.Ado.PersonalAccessToken = protector.Protect(settings.Ado.PersonalAccessToken);

        DataFolder.Replace(AppPaths.SettingsFile, JsonSerializer.Serialize(onDisk, Json));
    }

    /// <summary>Raised after a successful save so open pages can re-read configuration.</summary>
    public event Action? Changed;
}
