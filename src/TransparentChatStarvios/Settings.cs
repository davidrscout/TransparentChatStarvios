using System.IO;
using System.Text.Json;

namespace TransparentChatStarvios;

public sealed class Settings
{
    public string Channel { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public bool BoldNames { get; set; } = true;
    public double FontSize { get; set; } = 15;
    /// <summary>Opacidad del fondo del chat (0 = totalmente transparente, 1 = negro sólido).</summary>
    public double BackgroundOpacity { get; set; } = 0.35;
    /// <summary>Segundos tras los que se oculta un mensaje. 0 = nunca.</summary>
    public int HideAfterSeconds { get; set; } = 0;
    public int MaxMessages { get; set; } = 80;
    public bool ShowBadges { get; set; } = true;
    public bool ShowEmotes { get; set; } = true;
    public bool TextShadow { get; set; } = true;
    public bool Locked { get; set; } = false;
    public bool LockTipShown { get; set; } = false;

    public double Left { get; set; } = 60;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 520;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransparentChatStarvios");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    /// <summary>Acepta "natu", "@natu" o cualquier URL de Starvios (…/popout/chat/natu, …/natu).</summary>
    public static string NormalizeChannel(string input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        int q = s.IndexOfAny(['?', '#']);
        if (q >= 0) s = s[..q];
        if (s.Contains('/')) s = s[(s.LastIndexOf('/') + 1)..];
        return s.TrimStart('@').ToLowerInvariant();
    }
}
