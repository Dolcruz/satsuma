using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Satsuma.Core;

public enum WheelMode { Convert, Tools, Actions }

public enum TriggerKey { Shift, Ctrl, Alt, None }

public sealed class Settings
{
    public TriggerKey ConvertKey { get; set; } = TriggerKey.Shift;
    public TriggerKey ToolsKey { get; set; } = TriggerKey.Ctrl;
    public TriggerKey ActionsKey { get; set; } = TriggerKey.Alt;

    /// <summary>Global shortcut that opens the wheel for the files selected in Explorer.</summary>
    public bool KeyboardShortcutEnabled { get; set; } = true;

    public bool LaunchAtStartup { get; set; } = true;
    public bool ExplorerContextMenu { get; set; } = true;
    public bool RevealResultInExplorer { get; set; } = false;
    public bool PlaySounds { get; set; } = false;

    public int JpegQuality { get; set; } = 90;
    public int CompressQuality { get; set; } = 70;
    public int PdfRenderDpi { get; set; } = 200;
    public int VideoCrf { get; set; } = 23;

    public bool FirstRunDone { get; set; }

    /// <summary>Gestures and shortcut are off. Remembered across restarts.</summary>
    public bool Paused { get; set; }

    public static void SetPaused(bool paused)
    {
        if (Current.Paused == paused) return;
        Current.Paused = paused;
        Save();
    }

    [JsonIgnore]
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Satsuma");

    [JsonIgnore]
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { Current = new Settings(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch { /* settings are best effort */ }
        Changed?.Invoke();
    }

    public WheelMode? ModeFor(TriggerKey key)
    {
        if (key == TriggerKey.None) return null;
        if (ConvertKey == key) return WheelMode.Convert;
        if (ToolsKey == key) return WheelMode.Tools;
        if (ActionsKey == key) return WheelMode.Actions;
        return null;
    }

    public TriggerKey KeyFor(WheelMode mode) => mode switch
    {
        WheelMode.Convert => ConvertKey,
        WheelMode.Tools => ToolsKey,
        _ => ActionsKey,
    };
}
