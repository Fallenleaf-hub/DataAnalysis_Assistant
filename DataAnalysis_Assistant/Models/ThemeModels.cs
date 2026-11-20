using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DataAnalysis_Assistant.Models
{
    public class ChartLanguageOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PromptInstruction { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; } = false;
    }

    public class ChartColorThemeOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> PaletteHexColors { get; set; } = new();
        public string StylePrompt { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; } = false;
    }

    public static class ThemePresetCatalog
    {
        public const string DefaultLanguageId = "zh-CN";
        public const string DefaultColorThemeId = "auto";

        private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

        public static IReadOnlyList<ChartLanguageOption> BuiltInLanguages { get; } = new List<ChartLanguageOption>
        {
            new()
            {
                Id = "en",
                Name = "English",
                PromptInstruction = "All chart titles, subtitles, axis labels, ticks, legends, and data annotations must be written in clear, professional English; if raw data categorical values are in Chinese, map and translate them to standard English labels before plotting (e.g., 优秀→Excellent, 良好→Good, 合格→Pass, 不合格→Fail) without leaving untranslated Chinese characters or missing-glyph boxes.",
                IsBuiltIn = true
            },
            new()
            {
                Id = "zh-CN",
                Name = "简体中文",
                PromptInstruction = "所有统计图表的标题、副标题、坐标轴名称、刻度、图例与数据标签请统一使用规范简体中文表达。",
                IsBuiltIn = true
            }
        };

        public static IReadOnlyList<ChartColorThemeOption> BuiltInColorThemes { get; } = new List<ChartColorThemeOption>
        {
            new()
            {
                Id = "auto",
                Name = "默认自动",
                PaletteHexColors = new List<string>(),
                StylePrompt = "未指定固定色板，请结合数据语义、系列数量或当前 Skill 指南自行选用克制专业的配色方案。",
                IsBuiltIn = true
            },
            new()
            {
                Id = "warm_business",
                Name = "暖棕商务",
                PaletteHexColors = new List<string> { "#B85D19", "#2E2822", "#D49A6A", "#6B7B6E", "#8C6D46", "#C9B8A3" },
                StylePrompt = "呼应本软件暖大地 UI 风格，纯白或浅暖底色、细灰辅助网格、去除顶部与右侧边框脊。",
                IsBuiltIn = true
            },
            new()
            {
                Id = "nature_npg",
                Name = "Nature 学术 (NPG)",
                PaletteHexColors = new List<string> { "#E64B35", "#4DBBD5", "#00A087", "#3C5488", "#F39B7F", "#8491B4" },
                StylePrompt = "顶级学术期刊克制规范风，纯白底、清晰刻度线、去除上右轴脊、高分辨率字号层级与精确标注。",
                IsBuiltIn = true
            },
            new()
            {
                Id = "mckinsey_blue",
                Name = "咨询深蓝 (McKinsey)",
                PaletteHexColors = new List<string> { "#051C2C", "#2251FF", "#00A9F4", "#667B8A", "#A2AAAD", "#D9A520" },
                StylePrompt = "经典商业咨询研报风，主次深蓝强调核心序列、中性灰弱化次要维度、关键数据标签直出。",
                IsBuiltIn = true
            },
            new()
            {
                Id = "morandi_soft",
                Name = "莫兰迪柔和",
                PaletteHexColors = new List<string> { "#7A8B99", "#A89F91", "#B5838D", "#84A59D", "#D4A373", "#9C89B8" },
                StylePrompt = "低饱和度温和汇报配色，多系列对比和谐不刺眼，适合长篇商务报告与PPT配图。",
                IsBuiltIn = true
            },
            new()
            {
                Id = "okabe_ito",
                Name = "色盲友好 (Okabe-Ito)",
                PaletteHexColors = new List<string> { "#E69F00", "#56B4E9", "#009E73", "#F0E442", "#0072B2", "#D55E00" },
                StylePrompt = "国际标准无障碍科学调色板，兼顾红绿/蓝黄色觉障碍人群的高可区分度。",
                IsBuiltIn = true
            }
        };

        public static List<ChartLanguageOption> GetAllLanguages(AppSettings? settings)
        {
            var result = new List<ChartLanguageOption>(BuiltInLanguages);
            var seenIds = new HashSet<string>(BuiltInLanguages.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

            if (settings?.CustomChartLanguages != null)
            {
                foreach (var item in settings.CustomChartLanguages)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                        continue;

                    if (seenIds.Add(item.Id))
                    {
                        item.IsBuiltIn = false;
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        public static List<ChartColorThemeOption> GetAllColorThemes(AppSettings? settings)
        {
            var result = new List<ChartColorThemeOption>(BuiltInColorThemes);
            var seenIds = new HashSet<string>(BuiltInColorThemes.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

            if (settings?.CustomChartColorThemes != null)
            {
                foreach (var item in settings.CustomChartColorThemes)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                        continue;

                    if (seenIds.Add(item.Id))
                    {
                        item.IsBuiltIn = false;
                        item.PaletteHexColors ??= new List<string>();
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        public static ChartLanguageOption ResolveSelectedLanguage(AppSettings? settings)
        {
            var all = GetAllLanguages(settings);
            string targetId = settings?.SelectedChartLanguageId?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(targetId))
            {
                var found = all.FirstOrDefault(x => x.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }

            return all.FirstOrDefault(x => x.Id.Equals(DefaultLanguageId, StringComparison.OrdinalIgnoreCase))
                   ?? BuiltInLanguages[1];
        }

        public static ChartColorThemeOption ResolveSelectedColorTheme(AppSettings? settings)
        {
            var all = GetAllColorThemes(settings);
            string targetId = settings?.SelectedChartColorThemeId?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(targetId))
            {
                var found = all.FirstOrDefault(x => x.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }

            return all.FirstOrDefault(x => x.Id.Equals(DefaultColorThemeId, StringComparison.OrdinalIgnoreCase))
                   ?? BuiltInColorThemes[0];
        }

        public static string BuildChartThemePrompt(AppSettings? settings)
        {
            var lang = ResolveSelectedLanguage(settings);
            var colorTheme = ResolveSelectedColorTheme(settings);

            string langInstruction = string.IsNullOrWhiteSpace(lang.PromptInstruction)
                ? $"所有统计图表的标题、副标题、坐标轴名称、刻度、图例与数据标签请统一使用 {lang.Name} 表达。"
                : lang.PromptInstruction.Trim();
            string trimmedLangInst = langInstruction.TrimEnd('。', '.');

            var sb = new StringBuilder();
            sb.AppendLine("================ 图表主题与视觉规范 (Chart Theme) ================");
            sb.AppendLine("【生效范围】：以下语言与配色要求仅作用于使用 Python（如 Matplotlib/Seaborn）生成的统计图表（标题、副标题、坐标轴标签、刻度、图例、数据标注及色板），正文分析文字仍遵循用户提问语言与系统角色设定。");
            sb.AppendLine("【冲突优先级】：用户当次提问的明确要求 > 当前启用的专属 Skill 规范 > 本主题全局设置 > 系统默认偏好。");
            sb.AppendLine($"1. 图表语言：{lang.Name}（{trimmedLangInst}）。");

            if (colorTheme.Id.Equals(DefaultColorThemeId, StringComparison.OrdinalIgnoreCase) ||
                colorTheme.PaletteHexColors == null ||
                colorTheme.PaletteHexColors.Count == 0)
            {
                string autoDesc = string.IsNullOrWhiteSpace(colorTheme.StylePrompt)
                    ? "未指定固定色板，请结合数据语义、系列数量或当前 Skill 指南自行选用克制专业的配色方案"
                    : colorTheme.StylePrompt.Trim().TrimEnd('。', '.');
                sb.AppendLine($"2. 图表配色方案：{colorTheme.Name}（{autoDesc}）。");
            }
            else
            {
                sb.AppendLine($"2. 图表配色方案：{colorTheme.Name}");
                sb.AppendLine($"   - 优先按序使用以下 Hex 色板：{string.Join(", ", colorTheme.PaletteHexColors)}");
                if (!string.IsNullOrWhiteSpace(colorTheme.StylePrompt))
                {
                    sb.AppendLine($"   - 风格指导：{colorTheme.StylePrompt.Trim()}");
                }
            }

            sb.AppendLine("3. 图表防重叠与版面质量红线：");
            sb.AppendLine("   - 主副标题禁止无间距堆叠：若同时使用主标题与副标题，须使用 fig.suptitle(..., y=0.98) 配合 ax.set_title(..., pad=10) 及 plt.tight_layout(rect=[0, 0, 1, 0.92])（或采用单标题换行配合 pad=12），严禁主副标题相互遮挡。");
            sb.AppendLine("   - 饼图/环形图占比小于 5% 的窄扇区禁止将百分比文字硬塞入扇区内部，应改用外部引线标注（annotate）或移入图例说明。");
            sb.AppendLine("   - 箱线图/散点图的样本量标注（如 n=...）应置于须线上方留白区或并入 X 轴刻度标签（如 \"Excellent\\n(n=76)\"），严禁压住箱体中位线或数据散点。");
            sb.AppendLine("   - 保存图片统一使用 dpi=300, bbox_inches='tight' 防止边缘文字被裁切，且禁止清除中文字体回退配置。");
            sb.Append("====================================================================");
            return sb.ToString();
        }

        public static bool ParseHexPalette(
            string? rawInput,
            out List<string> normalizedHexColors,
            out string? errorMessage)
        {
            normalizedHexColors = new List<string>();
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(rawInput))
            {
                errorMessage = "请至少输入 1 个 Hex 色值（推荐 4~6 个），例如：#B85D19, #2E2822";
                return false;
            }

            char[] separators = { ',', '，', ';', '；', ' ', '\t', '\r', '\n' };
            string[] tokens = rawInput.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens.Length == 0)
            {
                errorMessage = "请至少输入 1 个 Hex 色值（推荐 4~6 个），例如：#B85D19, #2E2822";
                return false;
            }

            if (tokens.Length > 8)
            {
                errorMessage = $"最多支持 8 个 Hex 色值（当前输入 {tokens.Length} 个），请精简色板数量。";
                return false;
            }

            foreach (string token in tokens)
            {
                if (!HexColorRegex.IsMatch(token))
                {
                    errorMessage = $"色值 \"{token}\" 格式无效，请使用 #RGB 或 #RRGGBB 格式（如 #E64B35）。";
                    normalizedHexColors.Clear();
                    return false;
                }

                normalizedHexColors.Add(NormalizeHex(token));
            }

            return true;
        }

        public static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
        {
            r = 0;
            g = 0;
            b = 0;

            if (string.IsNullOrWhiteSpace(hex)) return false;
            string trimmed = hex.Trim();
            if (!HexColorRegex.IsMatch(trimmed)) return false;

            string normalized = NormalizeHex(trimmed);
            return byte.TryParse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                   byte.TryParse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                   byte.TryParse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        private static string NormalizeHex(string hex)
        {
            string upper = hex.Trim().ToUpperInvariant();
            if (upper.Length == 4)
            {
                return $"#{upper[1]}{upper[1]}{upper[2]}{upper[2]}{upper[3]}{upper[3]}";
            }
            return upper;
        }
    }
}
