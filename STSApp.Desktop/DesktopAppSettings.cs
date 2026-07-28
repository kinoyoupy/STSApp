using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STSApp.Desktop;

/// <summary>
/// Avaloniaアプリ側の設定です。
/// BackendのURLなど、環境によって変わる値をコードへ直接書かないために使います。
/// </summary>
public sealed class DesktopAppSettings
{
    public string BackendBaseUrl { get; init; } = "http://127.0.0.1:5133";

    public static DesktopAppSettings Load()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            return new DesktopAppSettings();
        }

        var json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<DesktopAppSettings>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            });

        return settings ?? new DesktopAppSettings();
    }
}
