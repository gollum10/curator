using System.Windows;
using System.Windows.Media;

namespace NewsDesk.Desktop.Services;

/// <summary>Light and dark palettes exposed as dynamic brush resources, plus the WPF Fluent theme for controls.</summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Bg"] = ("#F6F6F3", "#1B1B1D"),
        ["Surface"] = ("#FFFFFF", "#242427"),
        ["SurfaceAlt"] = ("#EEEEEA", "#2D2D31"),
        ["Border"] = ("#DCDCD6", "#3A3A40"),
        ["Text"] = ("#1B1B1B", "#ECECEE"),
        ["Muted"] = ("#5F5F5A", "#A4A4AA"),
        ["Accent"] = ("#2F64C9", "#78A6FF"),
        ["AccentSoft"] = ("#E2EAF8", "#26334C"),
        ["OnAccent"] = ("#FFFFFF", "#101318"),
        ["Warning"] = ("#9A5B00", "#F2B866"),
        ["WarningSoft"] = ("#FFF2DC", "#3A2F1F"),
        ["Danger"] = ("#B3263E", "#FF7A8E"),
        ["Success"] = ("#2D7231", "#82C985"),
        ["Selection"] = ("#E7EDF8", "#2C3647"),
    };

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        foreach (var (key, (light, darkColor)) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }
}
