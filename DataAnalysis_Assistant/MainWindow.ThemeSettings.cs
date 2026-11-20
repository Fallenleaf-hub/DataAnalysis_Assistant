using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services;
using XamlRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace DataAnalysis_Assistant
{
    public partial class MainWindow
    {
        private static readonly SolidColorBrush BrushThemeAccent = new(Color.FromArgb(255, 0xB8, 0x5D, 0x19));
        private static readonly SolidColorBrush BrushThemeBorderMuted = new(Color.FromArgb(255, 0xE2, 0xDD, 0xD0));
        private static readonly SolidColorBrush BrushThemeHoverBorder = new(Color.FromArgb(255, 0xC9, 0xA9, 0x8D));
        private static readonly SolidColorBrush BrushThemeCardSubtle = new(Color.FromArgb(255, 0xFD, 0xFC, 0xF9));
        private static readonly SolidColorBrush BrushThemeSelectedBg = new(Color.FromArgb(255, 0xFD, 0xF7, 0xF0));
        private static readonly SolidColorBrush BrushThemeAutoSwatchBg = new(Color.FromArgb(255, 0xF3, 0xEF, 0xE6));
        private static readonly SolidColorBrush BrushThemeError = new(Color.FromArgb(255, 0xC6, 0x28, 0x28));

        private bool _isThemeDialogOpen = false;

        private void btnSettingsNavTheme_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsTheme, btnSettingsNavTheme, tbSettingsNavThemeText);
            RenderThemeSectionCards();
        }

        private void tbSystemPrompt_LostFocus(object sender, RoutedEventArgs e)
        {
            if (tbSystemPrompt == null) return;
            string trimmed = tbSystemPrompt.Text.Trim();
            if (!string.Equals(_settings.SystemPrompt, trimmed, StringComparison.Ordinal))
            {
                _settings.SystemPrompt = trimmed;
                SettingsService.SaveSettings(_settings);
            }
        }

        private void btnSaveThemeSettings_Click(object sender, RoutedEventArgs e)
        {
            if (tbSystemPrompt != null)
            {
                _settings.SystemPrompt = tbSystemPrompt.Text.Trim();
            }

            SettingsService.SaveSettings(_settings);

            var lang = ThemePresetCatalog.ResolveSelectedLanguage(_settings);
            var colorTheme = ThemePresetCatalog.ResolveSelectedColorTheme(_settings);

            if (infoBarThemeSettings != null)
            {
                infoBarThemeSettings.IsOpen = true;
                infoBarThemeSettings.Severity = InfoBarSeverity.Success;
                infoBarThemeSettings.Title = "主题配置已保存";
                infoBarThemeSettings.Message = $"当前图表语言：{lang.Name}，图表配色：{colorTheme.Name}，系统角色提示词已保存生效。";
            }
        }

        private void btnResetThemeSettings_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettings();
            _settings.SelectedChartLanguageId = ThemePresetCatalog.DefaultLanguageId;
            _settings.SelectedChartColorThemeId = ThemePresetCatalog.DefaultColorThemeId;
            _settings.SystemPrompt = defaults.SystemPrompt;

            if (tbSystemPrompt != null)
            {
                tbSystemPrompt.Text = _settings.SystemPrompt;
            }

            SettingsService.SaveSettings(_settings);
            RenderThemeSectionCards();

            if (infoBarThemeSettings != null)
            {
                infoBarThemeSettings.IsOpen = true;
                infoBarThemeSettings.Severity = InfoBarSeverity.Informational;
                infoBarThemeSettings.Title = "已恢复默认";
                infoBarThemeSettings.Message = "图表语言（简体中文）、图表配色（默认自动）及系统角色提示词已重置为默认值（已保留自定义项与 API Key）。";
            }
        }

        private void RenderThemeSectionCards()
        {
            if (panelThemeLanguages == null || panelThemeColorPalettes == null) return;

            panelThemeLanguages.Children.Clear();
            var languages = ThemePresetCatalog.GetAllLanguages(_settings);
            string selectedLangId = ThemePresetCatalog.ResolveSelectedLanguage(_settings).Id;
            var langCards = new List<UIElement>(languages.Count + 1);
            foreach (var lang in languages)
            {
                bool isSelected = lang.Id.Equals(selectedLangId, StringComparison.OrdinalIgnoreCase);
                langCards.Add(CreateLanguageCard(lang, isSelected));
            }
            langCards.Add(CreateAddOptionCard(56, "新增语言", async () => await ShowLanguageEditorDialogAsync(null)));
            BuildCardGridRows(panelThemeLanguages, langCards, columnsPerRow: 5);

            panelThemeColorPalettes.Children.Clear();
            var colorThemes = ThemePresetCatalog.GetAllColorThemes(_settings);
            string selectedColorId = ThemePresetCatalog.ResolveSelectedColorTheme(_settings).Id;
            var colorCards = new List<UIElement>(colorThemes.Count + 1);
            foreach (var theme in colorThemes)
            {
                bool isSelected = theme.Id.Equals(selectedColorId, StringComparison.OrdinalIgnoreCase);
                colorCards.Add(CreateColorThemeCard(theme, isSelected));
            }
            colorCards.Add(CreateAddOptionCard(80, "新增配色", async () => await ShowColorThemeEditorDialogAsync(null)));
            BuildCardGridRows(panelThemeColorPalettes, colorCards, columnsPerRow: 4);
        }

        private static void BuildCardGridRows(StackPanel container, IReadOnlyList<UIElement> items, int columnsPerRow)
        {
            for (int start = 0; start < items.Count; start += columnsPerRow)
            {
                var rowGrid = new Grid { ColumnSpacing = 12 };
                for (int c = 0; c < columnsPerRow; c++)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                int count = Math.Min(columnsPerRow, items.Count - start);
                for (int c = 0; c < count; c++)
                {
                    var item = items[start + c];
                    Grid.SetColumn((FrameworkElement)item, c);
                    rowGrid.Children.Add(item);
                }

                container.Children.Add(rowGrid);
            }
        }

        private Border CreateLanguageCard(ChartLanguageOption lang, bool isSelected)
        {
            var card = new Border
            {
                Height = 56,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                BorderBrush = isSelected ? BrushThemeAccent : BrushThemeBorderMuted,
                Background = isSelected ? BrushThemeSelectedBg : BrushThemeCardSubtle
            };

            var contentGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tbName = new TextBlock
            {
                Text = lang.Name,
                FontSize = 13,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = isSelected ? BrushThemeAccent : BrushTextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tbName, 0);
            contentGrid.Children.Add(tbName);

            if (isSelected)
            {
                var tbSelected = new TextBlock
                {
                    Text = "当前",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushThemeAccent,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tbSelected, 1);
                contentGrid.Children.Add(tbSelected);
            }

            card.Child = contentGrid;

            string sourceLabel = lang.IsBuiltIn ? "内置只读" : "自定义";
            string promptSummary = string.IsNullOrWhiteSpace(lang.PromptInstruction)
                ? $"按「{lang.Name}」自动生成图表标注语言指令"
                : lang.PromptInstruction;
            ToolTipService.SetToolTip(card, $"{lang.Name}（{sourceLabel}）\n{promptSummary}\n右键可查看或管理");

            card.PointerEntered += (_, _) =>
            {
                if (!isSelected)
                {
                    card.BorderBrush = BrushThemeHoverBorder;
                    card.Background = BrushCardBg;
                }
            };
            card.PointerExited += (_, _) =>
            {
                if (!isSelected)
                {
                    card.BorderBrush = BrushThemeBorderMuted;
                    card.Background = BrushThemeCardSubtle;
                }
            };
            card.Tapped += (_, _) =>
            {
                _settings.SelectedChartLanguageId = lang.Id;
                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            };

            card.ContextFlyout = BuildLanguageContextFlyout(lang);
            return card;
        }

        private Border CreateColorThemeCard(ChartColorThemeOption theme, bool isSelected)
        {
            var card = new Border
            {
                Height = 80,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 9, 10, 9),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                BorderBrush = isSelected ? BrushThemeAccent : BrushThemeBorderMuted,
                Background = isSelected ? BrushThemeSelectedBg : BrushThemeCardSubtle
            };

            var layoutGrid = new Grid { RowSpacing = 8 };
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            bool isAuto = theme.Id.Equals(ThemePresetCatalog.DefaultColorThemeId, StringComparison.OrdinalIgnoreCase) &&
                          (theme.PaletteHexColors == null || theme.PaletteHexColors.Count == 0);
            var swatchBar = BuildPaletteSwatchBar(theme.PaletteHexColors, height: 22, isAuto: isAuto);
            Grid.SetRow(swatchBar, 0);
            layoutGrid.Children.Add(swatchBar);

            var footerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tbName = new TextBlock
            {
                Text = theme.Name,
                FontSize = 12,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = isSelected ? BrushThemeAccent : BrushTextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tbName, 0);
            footerGrid.Children.Add(tbName);

            if (isSelected)
            {
                var tbSelected = new TextBlock
                {
                    Text = "当前",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushThemeAccent,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tbSelected, 1);
                footerGrid.Children.Add(tbSelected);
            }

            Grid.SetRow(footerGrid, 1);
            layoutGrid.Children.Add(footerGrid);

            card.Child = layoutGrid;

            string sourceLabel = theme.IsBuiltIn ? "内置只读" : "自定义";
            string hexSummary = theme.PaletteHexColors != null && theme.PaletteHexColors.Count > 0
                ? string.Join(", ", theme.PaletteHexColors)
                : "无固定色板（自动搭配）";
            string styleLine = string.IsNullOrWhiteSpace(theme.StylePrompt) ? string.Empty : $"\n{theme.StylePrompt}";
            ToolTipService.SetToolTip(card, $"{theme.Name}（{sourceLabel}）\n色板：{hexSummary}{styleLine}\n右键可查看或管理");

            card.PointerEntered += (_, _) =>
            {
                if (!isSelected)
                {
                    card.BorderBrush = BrushThemeHoverBorder;
                    card.Background = BrushCardBg;
                }
            };
            card.PointerExited += (_, _) =>
            {
                if (!isSelected)
                {
                    card.BorderBrush = BrushThemeBorderMuted;
                    card.Background = BrushThemeCardSubtle;
                }
            };
            card.Tapped += (_, _) =>
            {
                _settings.SelectedChartColorThemeId = theme.Id;
                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            };

            card.ContextFlyout = BuildColorThemeContextFlyout(theme);
            return card;
        }

        private static Border BuildPaletteSwatchBar(IReadOnlyList<string>? hexColors, double height, bool isAuto)
        {
            if (isAuto || hexColors == null || hexColors.Count == 0)
            {
                return new Border
                {
                    Height = height,
                    CornerRadius = new CornerRadius(5),
                    Background = BrushThemeAutoSwatchBg,
                    BorderBrush = BrushThemeBorderMuted,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "自动搭配",
                        FontSize = 10,
                        Foreground = BrushTextSecondary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }

            var swatchGrid = new Grid { ColumnSpacing = 2 };
            for (int i = 0; i < hexColors.Count; i++)
            {
                swatchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                SolidColorBrush cellBrush = BrushThemeAutoSwatchBg;
                if (ThemePresetCatalog.TryParseHexColor(hexColors[i], out byte r, out byte g, out byte b))
                {
                    cellBrush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
                }

                var cell = new Border
                {
                    Background = cellBrush,
                    CornerRadius = new CornerRadius(3)
                };
                Grid.SetColumn(cell, i);
                swatchGrid.Children.Add(cell);
            }

            return new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Background = BrushCardBg,
                BorderBrush = BrushThemeBorderMuted,
                BorderThickness = new Thickness(1),
                Child = swatchGrid
            };
        }

        private UIElement CreateAddOptionCard(double height, string hoverText, Func<Task> onClick)
        {
            var container = new Grid
            {
                Height = height,
                Background = BrushTransparent
            };

            var dashedRect = new XamlRectangle
            {
                Stroke = BrushThemeBorderMuted,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 8,
                RadiusY = 8,
                Fill = BrushThemeCardSubtle
            };

            var label = new TextBlock
            {
                Text = "+",
                FontSize = 22,
                FontWeight = FontWeights.Normal,
                Foreground = BrushTextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            container.Children.Add(dashedRect);
            container.Children.Add(label);

            ToolTipService.SetToolTip(container, hoverText);

            container.PointerEntered += (_, _) =>
            {
                dashedRect.Stroke = BrushThemeAccent;
                dashedRect.Fill = BrushThemeSelectedBg;
                label.Text = hoverText;
                label.FontSize = 13;
                label.FontWeight = FontWeights.SemiBold;
                label.Foreground = BrushThemeAccent;
            };

            container.PointerExited += (_, _) =>
            {
                dashedRect.Stroke = BrushThemeBorderMuted;
                dashedRect.Fill = BrushThemeCardSubtle;
                label.Text = "+";
                label.FontSize = 22;
                label.FontWeight = FontWeights.Normal;
                label.Foreground = BrushTextSecondary;
            };

            container.Tapped += async (_, _) =>
            {
                await onClick();
            };

            return container;
        }

        private MenuFlyout BuildLanguageContextFlyout(ChartLanguageOption lang)
        {
            var flyout = new MenuFlyout();

            if (lang.IsBuiltIn)
            {
                var viewItem = new MenuFlyoutItem { Text = "查看详情" };
                viewItem.Click += async (_, _) => await ShowLanguageDetailDialogAsync(lang);
                flyout.Items.Add(viewItem);

                var copyItem = new MenuFlyoutItem { Text = "复制为新自定义项" };
                copyItem.Click += async (_, _) =>
                {
                    string copyName = MakeUniqueLanguageName($"{lang.Name} 副本");
                    await ShowLanguageEditorDialogAsync(null, copyName, lang.PromptInstruction);
                };
                flyout.Items.Add(copyItem);
            }
            else
            {
                var editItem = new MenuFlyoutItem { Text = "编辑" };
                editItem.Click += async (_, _) => await ShowLanguageEditorDialogAsync(lang);
                flyout.Items.Add(editItem);

                var deleteItem = new MenuFlyoutItem { Text = "删除" };
                deleteItem.Click += async (_, _) => await DeleteCustomLanguageAsync(lang);
                flyout.Items.Add(deleteItem);
            }

            return flyout;
        }

        private MenuFlyout BuildColorThemeContextFlyout(ChartColorThemeOption theme)
        {
            var flyout = new MenuFlyout();

            if (theme.IsBuiltIn)
            {
                var viewItem = new MenuFlyoutItem { Text = "查看详情" };
                viewItem.Click += async (_, _) => await ShowColorThemeDetailDialogAsync(theme);
                flyout.Items.Add(viewItem);

                var copyItem = new MenuFlyoutItem { Text = "复制为新自定义项" };
                copyItem.Click += async (_, _) =>
                {
                    string copyName = MakeUniqueColorThemeName($"{theme.Name} 副本");
                    string prefillHex = theme.PaletteHexColors != null && theme.PaletteHexColors.Count > 0
                        ? string.Join(", ", theme.PaletteHexColors)
                        : "#B85D19, #2E2822, #D49A6A, #6B7B6E";
                    await ShowColorThemeEditorDialogAsync(null, copyName, prefillHex, theme.StylePrompt);
                };
                flyout.Items.Add(copyItem);
            }
            else
            {
                var editItem = new MenuFlyoutItem { Text = "编辑" };
                editItem.Click += async (_, _) => await ShowColorThemeEditorDialogAsync(theme);
                flyout.Items.Add(editItem);

                var deleteItem = new MenuFlyoutItem { Text = "删除" };
                deleteItem.Click += async (_, _) => await DeleteCustomColorThemeAsync(theme);
                flyout.Items.Add(deleteItem);
            }

            return flyout;
        }

        private async Task DeleteCustomLanguageAsync(ChartLanguageOption lang)
        {
            if (_isThemeDialogOpen) return;
            _isThemeDialogOpen = true;
            try
            {
                bool confirmed = await ShowConfirmDialogAsync(
                    "删除自定义语言",
                    $"确定删除自定义图表语言「{lang.Name}」吗？\n若当前正在使用该语言，系统将自动回退至默认语言（简体中文）。");
                if (!confirmed) return;

                _settings.CustomChartLanguages.RemoveAll(x => x.Id.Equals(lang.Id, StringComparison.OrdinalIgnoreCase));
                if (_settings.SelectedChartLanguageId.Equals(lang.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.SelectedChartLanguageId = ThemePresetCatalog.DefaultLanguageId;
                }

                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private async Task DeleteCustomColorThemeAsync(ChartColorThemeOption theme)
        {
            if (_isThemeDialogOpen) return;
            _isThemeDialogOpen = true;
            try
            {
                bool confirmed = await ShowConfirmDialogAsync(
                    "删除自定义配色",
                    $"确定删除自定义图表配色「{theme.Name}」吗？\n若当前正在使用该配色，系统将自动回退至默认配色（默认自动）。");
                if (!confirmed) return;

                _settings.CustomChartColorThemes.RemoveAll(x => x.Id.Equals(theme.Id, StringComparison.OrdinalIgnoreCase));
                if (_settings.SelectedChartColorThemeId.Equals(theme.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.SelectedChartColorThemeId = ThemePresetCatalog.DefaultColorThemeId;
                }

                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private async Task ShowLanguageDetailDialogAsync(ChartLanguageOption lang)
        {
            if (_isThemeDialogOpen || this.Content?.XamlRoot == null) return;
            _isThemeDialogOpen = true;
            try
            {
                string promptText = string.IsNullOrWhiteSpace(lang.PromptInstruction)
                    ? $"所有统计图表的标题、副标题、坐标轴名称、刻度、图例与数据标签请统一使用 {lang.Name} 表达。"
                    : lang.PromptInstruction;

                var panel = new StackPanel { Spacing = 10, Width = 420 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"语言名称：{lang.Name}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"类型：{(lang.IsBuiltIn ? "内置只读预设" : "用户自定义")}",
                    FontSize = 12,
                    Foreground = BrushTextSecondary
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "Prompt 注入指令：",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                });
                panel.Children.Add(new Border
                {
                    Background = BrushThemeCardSubtle,
                    BorderBrush = BrushThemeBorderMuted,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = new TextBlock
                    {
                        Text = promptText,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                });

                var dialog = new ContentDialog
                {
                    Title = "图表语言详情",
                    Content = panel,
                    CloseButtonText = "关闭",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private async Task ShowColorThemeDetailDialogAsync(ChartColorThemeOption theme)
        {
            if (_isThemeDialogOpen || this.Content?.XamlRoot == null) return;
            _isThemeDialogOpen = true;
            try
            {
                bool isAuto = theme.Id.Equals(ThemePresetCatalog.DefaultColorThemeId, StringComparison.OrdinalIgnoreCase) &&
                              (theme.PaletteHexColors == null || theme.PaletteHexColors.Count == 0);

                var panel = new StackPanel { Spacing = 10, Width = 440 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"配色名称：{theme.Name}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"类型：{(theme.IsBuiltIn ? "内置只读预设" : "用户自定义")}",
                    FontSize = 12,
                    Foreground = BrushTextSecondary
                });
                panel.Children.Add(BuildPaletteSwatchBar(theme.PaletteHexColors, height: 26, isAuto: isAuto));

                string hexText = theme.PaletteHexColors != null && theme.PaletteHexColors.Count > 0
                    ? string.Join(", ", theme.PaletteHexColors)
                    : "无固定 Hex 色板（由模型结合数据语义与 Skill 自动匹配）";
                panel.Children.Add(new TextBlock
                {
                    Text = $"Hex 色板：{hexText}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(theme.StylePrompt))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "视觉风格描述：",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    });
                    panel.Children.Add(new Border
                    {
                        Background = BrushThemeCardSubtle,
                        BorderBrush = BrushThemeBorderMuted,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 8, 10, 8),
                        Child = new TextBlock
                        {
                            Text = theme.StylePrompt,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
                }

                var dialog = new ContentDialog
                {
                    Title = "图表配色详情",
                    Content = panel,
                    CloseButtonText = "关闭",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private async Task ShowLanguageEditorDialogAsync(
            ChartLanguageOption? existing,
            string? prefillName = null,
            string? prefillPrompt = null)
        {
            if (_isThemeDialogOpen || this.Content?.XamlRoot == null) return;
            _isThemeDialogOpen = true;
            try
            {
                bool isEdit = existing != null;

                var tbName = new TextBox
                {
                    Header = "语言名称（必填）",
                    PlaceholderText = "例如：日本語、Français、繁體中文",
                    MaxLength = 30,
                    Text = existing?.Name ?? prefillName ?? string.Empty
                };

                var tbInstruction = new TextBox
                {
                    Header = "图表语言补充提示词（选填）",
                    PlaceholderText = "留空将按语言名称自动生成规范图表标注提示词，也可指定日期/数字格式要求",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = 88,
                    Text = existing?.PromptInstruction ?? prefillPrompt ?? string.Empty
                };

                var tbError = new TextBlock
                {
                    Foreground = BrushThemeError,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Visibility = Visibility.Collapsed
                };

                var panel = new StackPanel { Spacing = 12, Width = 420 };
                panel.Children.Add(tbName);
                panel.Children.Add(tbInstruction);
                panel.Children.Add(tbError);

                var dialog = new ContentDialog
                {
                    Title = isEdit ? "编辑自定义语言" : "新增图表语言",
                    Content = panel,
                    PrimaryButtonText = "保存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                void Validate()
                {
                    string name = tbName.Text.Trim();
                    string? err = null;

                    if (string.IsNullOrEmpty(name))
                    {
                        err = "请输入语言名称。";
                    }
                    else
                    {
                        bool duplicated = ThemePresetCatalog.GetAllLanguages(_settings)
                            .Any(x => !x.Id.Equals(existing?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                                      x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (duplicated)
                        {
                            err = $"已存在名为「{name}」的图表语言，请更换名称。";
                        }
                    }

                    tbError.Text = err ?? string.Empty;
                    tbError.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
                    dialog.IsPrimaryButtonEnabled = string.IsNullOrEmpty(err);
                }

                tbName.TextChanged += (_, _) => Validate();
                Validate();

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                string finalName = tbName.Text.Trim();
                string finalInstruction = tbInstruction.Text.Trim();

                if (existing == null)
                {
                    string newId = $"custom_lang_{Guid.NewGuid():N}"[..24];
                    var created = new ChartLanguageOption
                    {
                        Id = newId,
                        Name = finalName,
                        PromptInstruction = finalInstruction,
                        IsBuiltIn = false
                    };
                    _settings.CustomChartLanguages.Add(created);
                    _settings.SelectedChartLanguageId = created.Id;
                }
                else
                {
                    existing.Name = finalName;
                    existing.PromptInstruction = finalInstruction;
                }

                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private async Task ShowColorThemeEditorDialogAsync(
            ChartColorThemeOption? existing,
            string? prefillName = null,
            string? prefillHex = null,
            string? prefillStyle = null)
        {
            if (_isThemeDialogOpen || this.Content?.XamlRoot == null) return;
            _isThemeDialogOpen = true;
            try
            {
                bool isEdit = existing != null;

                var tbName = new TextBox
                {
                    Header = "配色名称（必填）",
                    PlaceholderText = "例如：科研对比蓝橙、高对比演示",
                    MaxLength = 30,
                    Text = existing?.Name ?? prefillName ?? string.Empty
                };

                string initialHex = existing != null
                    ? string.Join(", ", existing.PaletteHexColors)
                    : (prefillHex ?? string.Empty);

                var tbHexPalette = new TextBox
                {
                    Header = "Hex 色板定义（必填，1~8 个，支持逗号/空格分隔）",
                    PlaceholderText = "例如：#B85D19, #2E2822, #D49A6A, #6B7B6E",
                    Text = initialHex
                };

                var previewHost = new StackPanel { Spacing = 4 };
                previewHost.Children.Add(new TextBlock
                {
                    Text = "色块实时预览",
                    FontSize = 12,
                    Foreground = BrushTextSecondary
                });
                var previewContainer = new Grid();
                previewHost.Children.Add(previewContainer);

                var tbStylePrompt = new TextBox
                {
                    Header = "风格补充描述（选填）",
                    PlaceholderText = "可填写背景底色、网格线风格、边框轴脊或数据标注偏好",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = 76,
                    Text = existing?.StylePrompt ?? prefillStyle ?? string.Empty
                };

                var tbError = new TextBlock
                {
                    Foreground = BrushThemeError,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Visibility = Visibility.Collapsed
                };

                var panel = new StackPanel { Spacing = 12, Width = 440 };
                panel.Children.Add(tbName);
                panel.Children.Add(tbHexPalette);
                panel.Children.Add(previewHost);
                panel.Children.Add(tbStylePrompt);
                panel.Children.Add(tbError);

                var dialog = new ContentDialog
                {
                    Title = isEdit ? "编辑自定义配色" : "新增图表配色",
                    Content = panel,
                    PrimaryButtonText = "保存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                List<string> parsedColors = new();

                void ValidateAndRefreshPreview()
                {
                    string name = tbName.Text.Trim();
                    string? err = null;

                    if (string.IsNullOrEmpty(name))
                    {
                        err = "请输入配色名称。";
                    }
                    else
                    {
                        bool duplicated = ThemePresetCatalog.GetAllColorThemes(_settings)
                            .Any(x => !x.Id.Equals(existing?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                                      x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (duplicated)
                        {
                            err = $"已存在名为「{name}」的图表配色，请更换名称。";
                        }
                    }

                    bool hexOk = ThemePresetCatalog.ParseHexPalette(tbHexPalette.Text, out var colors, out string? hexErr);
                    parsedColors = colors;

                    previewContainer.Children.Clear();
                    if (hexOk && colors.Count > 0)
                    {
                        previewContainer.Children.Add(BuildPaletteSwatchBar(colors, height: 24, isAuto: false));
                    }
                    else
                    {
                        previewContainer.Children.Add(new Border
                        {
                            Height = 24,
                            CornerRadius = new CornerRadius(5),
                            Background = BrushThemeAutoSwatchBg,
                            BorderBrush = BrushThemeBorderMuted,
                            BorderThickness = new Thickness(1),
                            Child = new TextBlock
                            {
                                Text = "请输入有效的 Hex 色值以预览色块",
                                FontSize = 10,
                                Foreground = BrushTextSecondary,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        });
                    }

                    if (string.IsNullOrEmpty(err) && !hexOk)
                    {
                        err = hexErr;
                    }

                    tbError.Text = err ?? string.Empty;
                    tbError.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
                    dialog.IsPrimaryButtonEnabled = string.IsNullOrEmpty(err);
                }

                tbName.TextChanged += (_, _) => ValidateAndRefreshPreview();
                tbHexPalette.TextChanged += (_, _) => ValidateAndRefreshPreview();
                ValidateAndRefreshPreview();

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                string finalName = tbName.Text.Trim();
                string finalStyle = tbStylePrompt.Text.Trim();

                if (existing == null)
                {
                    string newId = $"custom_color_{Guid.NewGuid():N}"[..25];
                    var created = new ChartColorThemeOption
                    {
                        Id = newId,
                        Name = finalName,
                        PaletteHexColors = new List<string>(parsedColors),
                        StylePrompt = finalStyle,
                        IsBuiltIn = false
                    };
                    _settings.CustomChartColorThemes.Add(created);
                    _settings.SelectedChartColorThemeId = created.Id;
                }
                else
                {
                    existing.Name = finalName;
                    existing.PaletteHexColors = new List<string>(parsedColors);
                    existing.StylePrompt = finalStyle;
                }

                SettingsService.SaveSettings(_settings);
                RenderThemeSectionCards();
            }
            finally
            {
                _isThemeDialogOpen = false;
            }
        }

        private string MakeUniqueLanguageName(string baseName)
        {
            var existing = new HashSet<string>(
                ThemePresetCatalog.GetAllLanguages(_settings).Select(x => x.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{baseName} {i}";
                if (!existing.Contains(candidate)) return candidate;
            }
            return $"{baseName} {DateTime.Now:HHmmss}";
        }

        private string MakeUniqueColorThemeName(string baseName)
        {
            var existing = new HashSet<string>(
                ThemePresetCatalog.GetAllColorThemes(_settings).Select(x => x.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{baseName} {i}";
                if (!existing.Contains(candidate)) return candidate;
            }
            return $"{baseName} {DateTime.Now:HHmmss}";
        }
    }
}
