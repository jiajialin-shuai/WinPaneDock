using System.IO;
using System.Text.Json;
using Microsoft.Terminal.Wpf;

namespace Cmux.Spike.Terminal;

public sealed record TerminalSettings(string FontFamily, short FontSize, string CursorStyle, string ColorScheme, int Padding)
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "terminal-settings.json");

    public static TerminalSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new TerminalSettings("Cascadia Code NF", 14, "BlinkingBar", "Campbell", 0), JsonOptions));
        }

        var settings = JsonSerializer.Deserialize<TerminalSettings>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("terminal-settings.json must contain an object.");
        if (string.IsNullOrWhiteSpace(settings.FontFamily) || settings.FontSize is < 4 or > 72 || settings.Padding is < 0 or > 64)
            throw new InvalidDataException("Font family, font size (4-72), or padding (0-64) is invalid.");
        if (!Enum.TryParse<CursorStyle>(settings.CursorStyle, true, out var cursor) || !Enum.IsDefined(cursor))
            throw new InvalidDataException("Unknown cursor style.");
        if (settings.ColorScheme is not ("Campbell" or "Solarized Dark"))
            throw new InvalidDataException("Color scheme must be Campbell or Solarized Dark.");
        return settings;
    }

    public TerminalTheme CreateTheme(string uiTheme = "Night")
    {
        var solarized = ColorScheme == "Solarized Dark";
        var day = uiTheme == "Day";
        var gray = uiTheme == "Gray";
        return new TerminalTheme
        {
            DefaultBackground = Color(day ? 0xF7F6F2u : gray ? 0x2B2D30u : solarized ? 0x002B36u : 0x0C0C0Cu),
            DefaultForeground = Color(day ? 0x25272Au : gray ? 0xE5E5E5u : solarized ? 0x839496u : 0xCCCCCCu),
            DefaultSelectionBackground = Color(day ? 0xD3D9E0u : solarized ? 0x586E75u : 0x5A5A5Au),
            CursorStyle = Enum.Parse<CursorStyle>(CursorStyle, true),
            ColorTable = day
                ? new uint[] { 0x25272A, 0xA6262E, 0x287A39, 0x8B6500, 0x225EB4, 0x873F91, 0x007A84, 0xB7B8B9,
                    0x6A6D71, 0xC63740, 0x31934A, 0xA77A00, 0x3478CF, 0xA64CB3, 0x008F9B, 0xFFFFFF }
                    .Select(Color).ToArray()
                : solarized
                ? new uint[] { 0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                    0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3 }
                    .Select(Color).ToArray()
                : new uint[] { 0x0C0C0C, 0xC50F1F, 0x13A10E, 0x039CBA, 0xC19C00, 0x881798, 0x767676, 0xC2C3C3,
                    0x767676, 0x3B78FF, 0x16C60C, 0x61D6D6, 0xE74856, 0xB4009E, 0xF9F1A5, 0xF2F2F2 },
        };
    }

    private static uint Color(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
