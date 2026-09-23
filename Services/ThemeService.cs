using System.Windows;
using System.Windows.Media;

namespace AIMaster.Services;

public static class ThemeModes
{
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? value) =>
        string.Equals(value, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
}

public static class ThemeService
{
    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackground"] = "#0B1220",
            ["Panel"] = "#111C2D",
            ["PanelRaised"] = "#17243A",
            ["InputSurface"] = "#0D1726",
            ["ButtonSurface"] = "#1A2940",
            ["ButtonHover"] = "#233550",
            ["ButtonPressed"] = "#101C2D",
            ["Line"] = "#263750",
            ["LineStrong"] = "#36506F",
            ["Ink"] = "#F4F7FB",
            ["Muted"] = "#9EADC2",
            ["Subtle"] = "#71839A",
            ["Signal"] = "#47D7AC",
            ["SignalInk"] = "#071A14",
            ["SignalSoft"] = "#153A34",
            ["SignalText"] = "#71E7C3",
            ["Warning"] = "#F2B95F",
            ["WarningSoft"] = "#382D1D",
            ["WarningBorder"] = "#6B542D",
            ["WarningText"] = "#F6D18E",
            ["Danger"] = "#F06B78",
            ["DangerSoft"] = "#3B222B",
            ["DangerBorder"] = "#75404B",
            ["DangerText"] = "#FFB7C0",
            ["MeterTrack"] = "#223249",
            ["ChartGrid"] = "#26364C",
            ["ChartBar"] = "#3A5364",
            ["FloatingShell"] = "#F5111C2D",
            ["FloatingPeek"] = "#70384A61",
            ["FloatingSecondaryInk"] = "#C5D0DE",
            ["FloatingHoveredTask"] = "#344B68",
            ["Interrupted"] = "#BEA5F4",
            ["InterruptedSoft"] = "#302746",
            ["Idle"] = "#8294A8",
            ["IdleSoft"] = "#223147",
            ["FloatInk"] = "#F4F7FB",
            ["FloatMuted"] = "#9EADC2",
            ["FloatSignal"] = "#47D7AC"
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackground"] = "#F3F6FA",
            ["Panel"] = "#FFFFFF",
            ["PanelRaised"] = "#EDF2F8",
            ["InputSurface"] = "#F8FAFD",
            ["ButtonSurface"] = "#E9EFF7",
            ["ButtonHover"] = "#DDE7F2",
            ["ButtonPressed"] = "#CFDCEB",
            ["Line"] = "#D7E0EB",
            ["LineStrong"] = "#B9C8D9",
            ["Ink"] = "#142238",
            ["Muted"] = "#5F7088",
            ["Subtle"] = "#7C8CA1",
            ["Signal"] = "#0B8F74",
            ["SignalInk"] = "#FFFFFF",
            ["SignalSoft"] = "#D9F3EA",
            ["SignalText"] = "#08725E",
            ["Warning"] = "#B46A08",
            ["WarningSoft"] = "#FFF2D7",
            ["WarningBorder"] = "#E8C478",
            ["WarningText"] = "#8A5208",
            ["Danger"] = "#C9475A",
            ["DangerSoft"] = "#FCE5E8",
            ["DangerBorder"] = "#E9A7B0",
            ["DangerText"] = "#A52F42",
            ["MeterTrack"] = "#DFE7F0",
            ["ChartGrid"] = "#DCE5EF",
            ["ChartBar"] = "#9FB2C5",
            ["FloatingShell"] = "#FAFFFFFF",
            ["FloatingPeek"] = "#704F657D",
            ["FloatingSecondaryInk"] = "#35465C",
            ["FloatingHoveredTask"] = "#C9DFF5",
            ["Interrupted"] = "#7957B2",
            ["InterruptedSoft"] = "#EEE7F8",
            ["Idle"] = "#667A91",
            ["IdleSoft"] = "#E7EDF4",
            ["FloatInk"] = "#142238",
            ["FloatMuted"] = "#5F7088",
            ["FloatSignal"] = "#0B8F74"
        };

    public static void Apply(ResourceDictionary resources, string? theme)
    {
        var palette = ThemeModes.Normalize(theme) == ThemeModes.Light ? LightPalette : DarkPalette;
        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
                existing.Color = color;
            else
                resources[key] = new SolidColorBrush(color);
        }
    }
}
