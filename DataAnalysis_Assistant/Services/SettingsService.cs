using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services
{
    public class SettingsService
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataAnalysis_Assistant",
            "settings.json"
        );

        private static readonly string LegacySettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Data_Analysis",
            "settings.json"
        );

        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return NormalizeSettings(settings);
                    }
                }
                else if (File.Exists(LegacySettingsFilePath))
                {
                    string json = File.ReadAllText(LegacySettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        NormalizeSettings(settings);
                        SaveSettings(settings);
                        return settings;
                    }
                }
            }
            catch
            {
            }

            return NormalizeSettings(new AppSettings());
        }

        private static AppSettings NormalizeSettings(AppSettings settings)
        {
            settings.DisabledSkills ??= new List<string>();
            settings.CustomChartLanguages ??= new List<ChartLanguageOption>();
            settings.CustomChartColorThemes ??= new List<ChartColorThemeOption>();

            settings.CustomChartLanguages = settings.CustomChartLanguages
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new ChartLanguageOption
                {
                    Id = x.Id.Trim(),
                    Name = x.Name.Trim(),
                    PromptInstruction = x.PromptInstruction?.Trim() ?? string.Empty,
                    IsBuiltIn = false
                })
                .ToList();

            settings.CustomChartColorThemes = settings.CustomChartColorThemes
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new ChartColorThemeOption
                {
                    Id = x.Id.Trim(),
                    Name = x.Name.Trim(),
                    PaletteHexColors = x.PaletteHexColors?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList() ?? new List<string>(),
                    StylePrompt = x.StylePrompt?.Trim() ?? string.Empty,
                    IsBuiltIn = false
                })
                .ToList();

            settings.SelectedChartLanguageId = ThemePresetCatalog.ResolveSelectedLanguage(settings).Id;
            settings.SelectedChartColorThemeId = ThemePresetCatalog.ResolveSelectedColorTheme(settings).Id;

            return settings;
        }

        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsFilePath)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
            }
        }
    }
}
