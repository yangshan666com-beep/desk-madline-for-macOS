using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskMadeline.Mac;

internal sealed class Settings
{
    public int Scale { get; set; } = 3;
    public bool Explore { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool ShowExpressions { get; set; } = true;
    public int BadelineCount { get; set; }
    public bool InfiniteStamina { get; set; }
    public int DashAssist { get; set; } = 1;
    public RumbleAmount Rumble { get; set; } = RumbleAmount.On;
    public WindowMaterial Terrain { get; set; }
    public string Celeste { get; set; }
    public List<DesktopItemPlacement> DesktopItems { get; set; } = new List<DesktopItemPlacement>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyRefillAnchor> DashRefills { get; set; }

    internal static string StateDirectory => Environment.GetEnvironmentVariable("DESKMADELINE_STATE_DIR") ??
        (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Desk Madeline")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Desk Madeline"));

    static string FilePath => Path.Combine(StateDirectory, "Settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (settings != null)
                {
                    settings.Scale = Math.Clamp(settings.Scale, 2, 6);
                    settings.BadelineCount = Math.Clamp(settings.BadelineCount, 0, DesktopBadeline.MaxCount);
                    if (settings.DashAssist is not (-1 or 1 or 2)) settings.DashAssist = 1;
                    if (!Enum.IsDefined(settings.Rumble)) settings.Rumble = RumbleAmount.On;
                    if (!Enum.IsDefined(settings.Terrain)) settings.Terrain = WindowMaterial.Solid;
                    settings.DesktopItems ??= new List<DesktopItemPlacement>();
                    if (settings.DashRefills != null)
                    {
                        foreach (LegacyRefillAnchor crystal in settings.DashRefills)
                            settings.DesktopItems.Add(new DesktopItemPlacement(DesktopItemKind.Refill, crystal.X, crystal.Y));
                        settings.DashRefills = null;
                    }
                    return settings;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            PetWindow.Log(e.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PetWindow.Log(e.Message);
        }
    }

    public readonly record struct LegacyRefillAnchor(float X, float Y);
}
