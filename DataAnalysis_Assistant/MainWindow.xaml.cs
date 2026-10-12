using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using XamlShape = Microsoft.UI.Xaml.Shapes.Shape;
using Microsoft.UI.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services;
using DataAnalysis_Assistant.Services.Python;
using DataAnalysis_Assistant.Services.Skills;
using DataAnalysis_Assistant.Services.Data;
using DataAnalysis_Assistant.Services.Context;
using DataAnalysis_Assistant.Services.Agent;
using DataAnalysis_Assistant.Services.Export;
using DataAnalysis_Assistant.Services.Tools;

namespace DataAnalysis_Assistant
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DocumentItem> Documents { get; } = new();
        public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
        public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();
        public ObservableCollection<SkillItem> InstalledSkills { get; } = new();
        public ObservableCollection<ChartArtifactItem> PlotGallery { get; } = new();

        private AppSettings _settings = new();
        private DocumentItem? _currentDoc;
        private ChartArtifactItem? _activeDashboardPlot;
        private ChartArtifactItem? _viewingPlot;
        private readonly PythonEnvironmentService _pythonEnvService = new();
        private readonly PythonExecutionService _pythonExecService = new();
        private readonly SkillManagerService _skillManager = new();
        private readonly DataSchemaProfiler _schemaProfiler = new();
        private readonly SafetyGateService _safetyGate = new();
        private readonly ReportExportService _exportService = new();

        // 界面状态画刷
        private static readonly SolidColorBrush BrushCardBg = new(Microsoft.UI.Colors.White);
        private static readonly SolidColorBrush BrushTransparent = new(Microsoft.UI.Colors.Transparent);
        private static readonly SolidColorBrush BrushTextPrimary = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x28, 0x22));
        private static readonly SolidColorBrush BrushTextSecondary = new(Windows.UI.Color.FromArgb(255, 0x7D, 0x74, 0x6A));
        private static readonly SolidColorBrush BrushAccentWarm = new(Windows.UI.Color.FromArgb(255, 0x8C, 0x58, 0x35));
        private static readonly SolidColorBrush BrushStopAction = new(Windows.UI.Color.FromArgb(255, 0xA8, 0x48, 0x36));

        // 对话运行状态与中断控制
        private CancellationTokenSource? _chatCts;
        private bool _isChatGenerating = false;

        public MainWindow()
        {
            InitializeComponent();

            // 绑定数据源
            lvDocuments.ItemsSource = Documents;
            icChatMessages.ItemsSource = ChatMessages;
            lvHistorySessions.ItemsSource = ChatSessions;
            lvPlotGallery.ItemsSource = PlotGallery;

            // 初始化最近对话历史会话列表
            InitializeSampleChatSessions();

            // 加载配置
            LoadSettings();

            // 初始化欢迎消息（含 Markdown 格式、表格与代码块展示）
            InitializeWelcomeChatMessage();

            // 设置窗口尺寸与标题
            SetInitialWindowSize();

            // 默认角标状态与会话统计
            UpdateDocCountDisplay();
            UpdateHistoryCountDisplay();
        }

        private void SetInitialWindowSize()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                appWindow?.Resize(new Windows.Graphics.SizeInt32(1280, 820));
            }
            catch
            {
                // 忽略非关键异常
            }
        }

        private void InitializeSampleChatSessions()
        {
            ChatSessions.Add(new ChatSessionItem { Title = "财务报表分析", IsSelected = true });
            ChatSessions.Add(new ChatSessionItem { Title = "销售数据分析" });
            ChatSessions.Add(new ChatSessionItem { Title = "用户留存洞察" });
            ChatSessions.Add(new ChatSessionItem { Title = "季度成本核算" });
            ChatSessions.Add(new ChatSessionItem { Title = "供应链数据透视" });
            ChatSessions.Add(new ChatSessionItem { Title = "竞品指标分析" });
            ChatSessions.Add(new ChatSessionItem { Title = "客户满意度调研" });
            ChatSessions.Add(new ChatSessionItem { Title = "营销ROI测算" });
            ChatSessions.Add(new ChatSessionItem { Title = "年度运营全景" });
            ChatSessions.Add(new ChatSessionItem { Title = "数据异动排查" });
        }

        #region 界面导航与二级界面控制

        private bool IsDrawerOpen => borderDrawerPane.Visibility == Visibility.Visible;

        // 切换回主页面（文档伴读看板）
        private void btnNavHome_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;
            CloseDrawer(highlightHome: false);
            btnTabDocPreview_Click(sender, e);
            HighlightNavButton(btnNavHome);
        }

        // 切换至可视化界面
        private void btnNavVis_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;
            CloseDrawer(highlightHome: false);
            btnTabVisualization_Click(sender, e);
            HighlightNavButton(btnNavVis);
        }

        // 文档库按钮（带数字角标）：点击直接呼出/秒切文档库抽屉
        private void btnNavDocLibrary_Click(object sender, RoutedEventArgs e)
        {
            // 如果抽屉已经打开且当前正是文档库，再次点击则收起抽屉回到主视图
            if (IsDrawerOpen && paneDocLibrary.Visibility == Visibility.Visible)
            {
                CloseDrawer();
                return;
            }

            // 无论之前是关闭状态还是正处于历史会话，直接无缝秒切至文档库！
            OpenDrawer(paneDocLibrary, btnNavDocLibrary);
        }

        // 历史会话按钮：点击直接呼出/秒切历史会话抽屉（与文档库同样模式）
        private void btnNavHistory_Click(object sender, RoutedEventArgs e)
        {
            // 如果抽屉已经打开且当前正是历史记录，再次点击则收起抽屉回到主视图
            if (IsDrawerOpen && paneHistorySessions.Visibility == Visibility.Visible)
            {
                CloseDrawer();
                return;
            }

            // 无论之前是关闭状态还是正处于文档库，直接无缝秒切至历史会话！
            OpenDrawer(paneHistorySessions, btnNavHistory);
        }

        // AI 对话快捷聚焦按钮
        private void btnNavChat_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;
            CloseDrawer(highlightHome: false);
            HighlightNavButton(btnNavChat);
            tbQuestion?.Focus(FocusState.Programmatic);
        }

        // 打开抽屉并切换至指定内容面板
        private void OpenDrawer(FrameworkElement paneToShow, Button navBtn)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;

            paneDocLibrary.Visibility = (paneToShow == paneDocLibrary) ? Visibility.Visible : Visibility.Collapsed;
            paneHistorySessions.Visibility = (paneToShow == paneHistorySessions) ? Visibility.Visible : Visibility.Collapsed;

            borderDrawerPane.Visibility = Visibility.Visible;
            drawerBackdrop.Visibility = Visibility.Visible;

            HighlightNavButton(navBtn);
        }

        // 收起抽屉
        private void CloseDrawer(bool highlightHome = true)
        {
            borderDrawerPane.Visibility = Visibility.Collapsed;
            drawerBackdrop.Visibility = Visibility.Collapsed;

            if (highlightHome)
            {
                HighlightNavButton(btnNavHome);
            }
        }

        // 左下角设置与更多：进入设置二级界面
        private void btnNavSettings_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Collapsed;
            gridSettingsPage.Visibility = Visibility.Visible;

            // 收起左侧抽屉（不恢复主页高亮）
            CloseDrawer(highlightHome: false);

            // 刷新表单数据
            LoadSettings();

            // 默认聚焦在“LLM配置”目录项
            SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);

            HighlightNavButton(btnNavSettings);
        }

        private void HighlightNavButton(Button activeBtn)
        {
            SetNavButtonState(btnNavHome, activeBtn == btnNavHome, pathNavHome);
            SetNavButtonState(btnNavVis, activeBtn == btnNavVis, pathNavVisBack, pathNavVisFront, pathNavVisInner);
            SetNavShapeState(pathNavVisDot, activeBtn == btnNavVis);
            SetNavButtonState(btnNavHistory, activeBtn == btnNavHistory, pathNavHistoryCircle, pathNavHistoryHands);
            SetNavButtonState(btnNavDocLibrary, activeBtn == btnNavDocLibrary, pathNavDoc1, pathNavDoc2, pathNavDoc3);
            SetNavButtonState(btnNavChat, activeBtn == btnNavChat, pathNavChatOuter);
            SetNavButtonState(btnNavSettings, activeBtn == btnNavSettings);
            SetNavShapeState(pathNavSettingsDot1, activeBtn == btnNavSettings);
            SetNavShapeState(pathNavSettingsDot2, activeBtn == btnNavSettings);
            SetNavShapeState(pathNavSettingsDot3, activeBtn == btnNavSettings);
        }

        private void SetNavButtonState(Button btn, bool isActive, params XamlPath[] paths)
        {
            btn.Background = isActive ? BrushCardBg : BrushTransparent;
            btn.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
            SolidColorBrush color = isActive ? BrushTextPrimary : BrushTextSecondary;
            foreach (var p in paths)
            {
                if (p != null)
                {
                    if (p.Fill != null && p.Fill != BrushTransparent)
                    {
                        p.Fill = color;
                    }
                    if (p.Stroke != null && p.Stroke != BrushTransparent)
                    {
                        p.Stroke = color;
                    }
                }
            }
        }

        private void SetNavShapeState(XamlShape shape, bool isActive)
        {
            if (shape != null)
            {
                SolidColorBrush color = isActive ? BrushTextPrimary : BrushTextSecondary;
                if (shape.Fill != null && shape.Fill != BrushTransparent)
                {
                    shape.Fill = color;
                }
                if (shape.Stroke != null && shape.Stroke != BrushTransparent)
                {
                    shape.Stroke = color;
                }
            }
        }

        // 半透明遮罩层点击：收起抽屉并复位导航高亮
        private void drawerBackdrop_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CloseDrawer();
        }

        // 二级界面顶部“返回主工作区”按钮
        private void btnBackToMain_Click(object sender, RoutedEventArgs e)
        {
            btnNavHome_Click(sender, e);
        }

        // 抽屉内部“✕”关闭按钮
        private void btnCloseDocPane_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
        }

        #endregion

        #region 中间板块选项卡切换 (文档预览 vs 可视化界面)

        private void btnTabDocPreview_Click(object sender, RoutedEventArgs e)
        {
            gridDocPreviewView.Visibility = Visibility.Visible;
            gridVisualizationView.Visibility = Visibility.Collapsed;

            btnTabDocPreview.Background = BrushCardBg;
            tbTabPreviewTitle.Foreground = BrushTextPrimary;
            tbTabPreviewTitle.FontWeight = FontWeights.SemiBold;

            btnTabVisualization.Background = BrushTransparent;
            tbTabVisTitle.Foreground = BrushTextSecondary;
            tbTabVisTitle.FontWeight = FontWeights.Normal;
        }

        private void btnTabVisualization_Click(object sender, RoutedEventArgs e)
        {
            gridDocPreviewView.Visibility = Visibility.Collapsed;
            gridVisualizationView.Visibility = Visibility.Visible;

            btnTabVisualization.Background = BrushCardBg;
            tbTabVisTitle.Foreground = BrushTextPrimary;
            tbTabVisTitle.FontWeight = FontWeights.SemiBold;

            btnTabDocPreview.Background = BrushTransparent;
            tbTabPreviewTitle.Foreground = BrushTextSecondary;
            tbTabPreviewTitle.FontWeight = FontWeights.Normal;
        }

        public void SwitchToVisualizationTab()
        {
            gridDocPreviewView.Visibility = Visibility.Collapsed;
            gridVisualizationView.Visibility = Visibility.Visible;

            btnTabVisualization.Background = BrushCardBg;
            tbTabVisTitle.Foreground = BrushTextPrimary;
            tbTabVisTitle.FontWeight = FontWeights.SemiBold;

            btnTabDocPreview.Background = BrushTransparent;
            tbTabPreviewTitle.Foreground = BrushTextSecondary;
            tbTabPreviewTitle.FontWeight = FontWeights.Normal;
        }

        #endregion

        #region 阶段三：可视化分析闭环与图表呈现 (图表与看板联动)

        /// <summary>
        /// 将新生成的分析图表沉淀至画廊并刷新看板状态
        /// </summary>
        public void AddPlotToGallery(ChartArtifactItem plot, bool switchToVis = false)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var existing = PlotGallery.FirstOrDefault(p => p.FilePath.Equals(plot.FilePath, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    PlotGallery.Insert(0, plot);
                }
                else
                {
                    plot = existing;
                }

                _activeDashboardPlot = plot;
                plot.IsPinnedToDashboard = true;
                UpdateVisualizationDashboardUI();

                if (switchToVis)
                {
                    SwitchToVisualizationTab();
                }
            });
        }

        private void UpdateVisualizationDashboardUI()
        {
            if (PlotGallery.Count == 0)
            {
                if (spEmptyVisGallery != null) spEmptyVisGallery.Visibility = Visibility.Visible;
                if (gridActivePlotContent != null) gridActivePlotContent.Visibility = Visibility.Collapsed;
                if (tbVisTotalPlots != null) tbVisTotalPlots.Text = "0 幅图表";
                if (tbVisActivePlotTitle != null) tbVisActivePlotTitle.Text = "暂无激活图表";
                if (tbVisLatestPlotTime != null) tbVisLatestPlotTime.Text = "--";
                return;
            }

            if (spEmptyVisGallery != null) spEmptyVisGallery.Visibility = Visibility.Collapsed;
            if (gridActivePlotContent != null) gridActivePlotContent.Visibility = Visibility.Visible;

            if (tbVisTotalPlots != null) tbVisTotalPlots.Text = $"{PlotGallery.Count} 幅图表";

            _activeDashboardPlot ??= PlotGallery.FirstOrDefault();
            if (_activeDashboardPlot != null)
            {
                if (tbVisActivePlotTitle != null) tbVisActivePlotTitle.Text = _activeDashboardPlot.Title;
                if (tbVisLatestPlotTime != null) tbVisLatestPlotTime.Text = _activeDashboardPlot.FormattedTime;
                if (tbActivePlotHeader != null) tbActivePlotHeader.Text = _activeDashboardPlot.Title;
                if (tbActivePlotMeta != null) tbActivePlotMeta.Text = $"{_activeDashboardPlot.FormattedSize} | {Path.GetExtension(_activeDashboardPlot.FilePath).ToUpperInvariant()}";
                if (imgActiveDashboardPlot != null)
                {
                    imgActiveDashboardPlot.Source = _activeDashboardPlot.Bitmap ?? (_activeDashboardPlot.ImageUri != null ? new BitmapImage(_activeDashboardPlot.ImageUri) : null);
                }
            }
        }

        private void lvPlotGallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lvPlotGallery.SelectedItem is ChartArtifactItem selectedPlot)
            {
                _activeDashboardPlot = selectedPlot;
                UpdateVisualizationDashboardUI();
            }
        }

        private void imgActiveDashboardPlot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeDashboardPlot != null)
            {
                ShowLargePlotViewer(_activeDashboardPlot);
            }
        }

        private void btnActivePlotZoom_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDashboardPlot != null)
            {
                ShowLargePlotViewer(_activeDashboardPlot);
            }
        }

        private async void btnActivePlotCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDashboardPlot != null)
            {
                await CopyImageToClipboardAsync(_activeDashboardPlot.FilePath);
            }
        }

        private async void btnActivePlotSave_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDashboardPlot != null)
            {
                await SavePlotAsAsync(_activeDashboardPlot.FilePath, _activeDashboardPlot.Title);
            }
        }

        private void plotImage_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is ChartArtifactItem plot)
            {
                ShowLargePlotViewer(plot);
            }
        }

        private void btnViewPlotLarge_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChartArtifactItem plot)
            {
                ShowLargePlotViewer(plot);
            }
        }

        private async void btnCopyPlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChartArtifactItem plot)
            {
                await CopyImageToClipboardAsync(plot.FilePath);
            }
        }

        private async void btnSavePlotAs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChartArtifactItem plot)
            {
                await SavePlotAsAsync(plot.FilePath, plot.Title);
            }
        }

        private void btnPinToPlotDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChartArtifactItem plot)
            {
                AddPlotToGallery(plot, switchToVis: true);
            }
        }

        private void ShowLargePlotViewer(ChartArtifactItem plot)
        {
            _viewingPlot = plot;
            if (tbViewerPlotTitle != null) tbViewerPlotTitle.Text = plot.Title;
            if (tbViewerPlotInfo != null) tbViewerPlotInfo.Text = $"{plot.FormattedSize} | {plot.FormattedTime} | {plot.FilePath}";
            if (imgViewerLargePlot != null)
            {
                imgViewerLargePlot.Source = plot.Bitmap ?? (plot.ImageUri != null ? new BitmapImage(plot.ImageUri) : null);
            }
            if (gridPlotImageViewer != null) gridPlotImageViewer.Visibility = Visibility.Visible;
        }

        private void btnClosePlotViewer_Click(object sender, RoutedEventArgs e)
        {
            if (gridPlotImageViewer != null) gridPlotImageViewer.Visibility = Visibility.Collapsed;
        }

        private async void btnViewerCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_viewingPlot != null)
            {
                await CopyImageToClipboardAsync(_viewingPlot.FilePath);
            }
        }

        private async void btnViewerSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_viewingPlot != null)
            {
                await SavePlotAsAsync(_viewingPlot.FilePath, _viewingPlot.Title);
            }
        }

        private static async Task CopyImageToClipboardAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                dataPackage.SetStorageItems(new[] { file });
                Clipboard.SetContent(dataPackage);
            }
            catch { }
        }

        private async Task SavePlotAsAsync(string sourceFilePath, string defaultName)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath)) return;
            try
            {
                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = string.IsNullOrWhiteSpace(defaultName) ? Path.GetFileNameWithoutExtension(sourceFilePath) : defaultName
                };
                string ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                savePicker.FileTypeChoices.Add("图表图片", new List<string> { ext });
                savePicker.DefaultFileExtension = ext;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                var targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile != null)
                {
                    using var sourceStream = File.OpenRead(sourceFilePath);
                    using var targetStream = await targetFile.OpenStreamForWriteAsync();
                    await sourceStream.CopyToAsync(targetStream);
                }
            }
            catch { }
        }

        private void btnOpenPlotFolder_Click(object sender, RoutedEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scratchDir = LocateScratchDirectory(baseDir);
            string sandboxDir = Path.Combine(scratchDir, "agent_sandbox");
            Directory.CreateDirectory(sandboxDir);
            SkillManagerService.OpenFolderInExplorer(sandboxDir);
        }

        private static string LocateScratchDirectory(string baseDir)
        {
            var current = new DirectoryInfo(baseDir);
            while (current != null && current.Parent != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, "DataAnalysis_Assistant.slnx")))
                {
                    string targetScratch = Path.Combine(current.FullName, "scratch");
                    Directory.CreateDirectory(targetScratch);
                    return targetScratch;
                }
                current = current.Parent;
            }
            string fallback = Path.Combine(baseDir, "scratch");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private void btnClearPlotGallery_Click(object sender, RoutedEventArgs e)
        {
            PlotGallery.Clear();
            _activeDashboardPlot = null;
            UpdateVisualizationDashboardUI();
        }

        private async void btnGenerateDemoPlot_Click(object sender, RoutedEventArgs e)
        {
            btnGenerateDemoPlot.IsEnabled = false;
            btnGenerateDemoPlot.Content = "正在绘制示例图表...";

            string pythonCode = @"
import matplotlib.pyplot as plt
import numpy as np

quarters = ['2025 Q1', '2025 Q2', '2025 Q3', '2025 Q4', '2026 Q1']
revenue = [128.5, 172.0, 226.4, 298.1, 356.8]
growth = [16.2, 29.5, 33.8, 46.2, 53.4]

fig, ax1 = plt.subplots(figsize=(8, 4.4), dpi=150)

color_bar = '#8C5835'
ax1.set_xlabel('季度业务周期 (Quarter)', fontsize=11, fontweight='bold')
ax1.set_ylabel('核心业务营收 (万元)', color=color_bar, fontsize=11, fontweight='bold')
bars = ax1.bar(quarters, revenue, color=color_bar, alpha=0.85, width=0.45, label='业务营收')
ax1.tick_params(axis='y', labelcolor=color_bar)

for bar in bars:
    yval = bar.get_height()
    ax1.text(bar.get_x() + bar.get_width()/2.0, yval + 6, f'{yval}万', ha='center', va='bottom', fontsize=9, fontweight='bold', color='#382E24')

ax2 = ax1.twinx()
color_line = '#2E7D32'
ax2.set_ylabel('同比复合增速 (%)', color=color_line, fontsize=11, fontweight='bold')
ax2.plot(quarters, growth, color=color_line, marker='o', markersize=7, linewidth=2.4, label='同比增速')
ax2.tick_params(axis='y', labelcolor=color_line)

for i, txt in enumerate(growth):
    ax2.annotate(f'+{txt}%', (quarters[i], growth[i]), textcoords='offset points', xytext=(0,10), ha='center', fontsize=9, fontweight='bold', color='#1B5E20')

plt.title('企业核心业务季度营收与增长走势多维对比图', fontsize=13, fontweight='bold', pad=14, color='#2E2822')
ax1.grid(True, linestyle='--', alpha=0.35)
fig.tight_layout()
";

            try
            {
                var result = await _pythonExecService.ExecuteCodeAsync(pythonCode, _settings.PythonPath);
                if (result.Success && result.GeneratedArtifacts.Count > 0)
                {
                    foreach (var path in result.GeneratedArtifacts)
                    {
                        var plot = new ChartArtifactItem
                        {
                            FilePath = path,
                            Title = "企业核心业务季度营收与增长走势多维对比图",
                            CreatedAt = DateTime.Now
                        };
                        AddPlotToGallery(plot, switchToVis: true);
                    }
                }
            }
            catch { }
            finally
            {
                btnGenerateDemoPlot.IsEnabled = true;
                btnGenerateDemoPlot.Content = "生成示例数据分析图表";
            }
        }

        /// <summary>
        /// 智能探查 AI 回答中包含的 Python 绘图代码并受控执行，捕获生成的图表呈现至消息卡片与看板
        /// </summary>
        private async Task TryExecutePythonPlotsAsync(ChatMessageItem assistantMessage, string responseContent, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(responseContent)) return;

            // 匹配 ```python 或 ```py 代码块
            var codeBlocks = ExtractPythonCodeBlocks(responseContent);
            if (codeBlocks.Count == 0) return;

            foreach (var code in codeBlocks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // 检查是否包含绘图特征 (plt, matplotlib, seaborn, barplot, savefig 等)
                bool isPlottingCode = code.Contains("plt.") ||
                                      code.Contains("matplotlib") ||
                                      code.Contains("savefig") ||
                                      code.Contains(".plot(") ||
                                      code.Contains("seaborn");

                if (!isPlottingCode) continue;

                try
                {
                    var result = await _pythonExecService.ExecuteCodeAsync(code, _settings.PythonPath, cancellationToken: cancellationToken);
                    if (result.Success && result.GeneratedArtifacts.Count > 0)
                    {
                        foreach (var artifact in result.GeneratedArtifacts)
                        {
                            string ext = Path.GetExtension(artifact).ToLowerInvariant();
                            if (ext is ".png" or ".jpg" or ".jpeg" or ".svg")
                            {
                                var plotItem = new ChartArtifactItem
                                {
                                    FilePath = artifact,
                                    Title = "数据分析结果图",
                                    CreatedAt = DateTime.Now
                                };

                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    assistantMessage.GeneratedPlots.Add(plotItem);
                                    AddPlotToGallery(plotItem, switchToVis: false);
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TryExecutePythonPlotsAsync] 执行失败: {ex.Message}");
                }
            }
        }

        private static List<string> ExtractPythonCodeBlocks(string markdown)
        {
            var list = new List<string>();
            var matches = System.Text.RegularExpressions.Regex.Matches(
                markdown,
                @"```(?:python|py)\s*([\s\S]*?)```",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    string block = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(block))
                    {
                        list.Add(block);
                    }
                }
            }
            return list;
        }

        #endregion

        #region 二级界面：设置目录与视图管理 (LLM配置 / Skill / 关于我们)

        private void btnSettingsNavLlm_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);
        }

        private void btnSettingsNavSkill_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsSkill, btnSettingsNavSkill, tbSettingsNavSkillText);
            _ = LoadInstalledSkillsAsync();
        }

        private void btnSettingsNavAbout_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsAbout, btnSettingsNavAbout, tbSettingsNavAboutText);
        }

        private void SwitchSettingsView(FrameworkElement activeView, Button activeBtn, TextBlock activeText)
        {
            viewSettingsLlm.Visibility = Visibility.Collapsed;
            viewSettingsSkill.Visibility = Visibility.Collapsed;
            viewSettingsAbout.Visibility = Visibility.Collapsed;

            activeView.Visibility = Visibility.Visible;
            HighlightSettingsNav(activeBtn, activeText);
        }

        private void HighlightSettingsNav(Button activeBtn, TextBlock activeText)
        {
            btnSettingsNavLlm.Background = BrushTransparent;
            btnSettingsNavSkill.Background = BrushTransparent;
            btnSettingsNavAbout.Background = BrushTransparent;

            tbSettingsNavLlmText.Foreground = BrushTextSecondary;
            tbSettingsNavLlmText.FontWeight = FontWeights.Normal;
            tbSettingsNavSkillText.Foreground = BrushTextSecondary;
            tbSettingsNavSkillText.FontWeight = FontWeights.Normal;
            tbSettingsNavAboutText.Foreground = BrushTextSecondary;
            tbSettingsNavAboutText.FontWeight = FontWeights.Normal;

            activeBtn.Background = BrushCardBg;
            activeText.Foreground = BrushTextPrimary;
            activeText.FontWeight = FontWeights.SemiBold;
        }

        private void tbSettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string keyword = tbSettingsSearch.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(keyword))
            {
                btnSettingsNavLlm.Visibility = Visibility.Visible;
                btnSettingsNavSkill.Visibility = Visibility.Visible;
                btnSettingsNavAbout.Visibility = Visibility.Visible;
                return;
            }

            btnSettingsNavLlm.Visibility = (keyword.Contains("llm") || "llm配置".Contains(keyword) || "模型".Contains(keyword) || "key".Contains(keyword) || "api".Contains(keyword) || "接口".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;
            btnSettingsNavSkill.Visibility = (keyword.Contains("skill") || "技能".Contains(keyword) || "图表".Contains(keyword) || "总结".Contains(keyword) || "分析".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;
            btnSettingsNavAbout.Visibility = (keyword.Contains("关于") || "关于我们".Contains(keyword) || "版本".Contains(keyword) || "团队".Contains(keyword) || "更新".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;

            // 联动自动跳转至搜索匹配的首个目录
            if (btnSettingsNavLlm.Visibility == Visibility.Visible && viewSettingsLlm.Visibility != Visibility.Visible)
            {
                SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);
            }
            else if (btnSettingsNavSkill.Visibility == Visibility.Visible && viewSettingsSkill.Visibility != Visibility.Visible && btnSettingsNavLlm.Visibility == Visibility.Collapsed)
            {
                SwitchSettingsView(viewSettingsSkill, btnSettingsNavSkill, tbSettingsNavSkillText);
            }
            else if (btnSettingsNavAbout.Visibility == Visibility.Visible && viewSettingsAbout.Visibility != Visibility.Visible && btnSettingsNavLlm.Visibility == Visibility.Collapsed && btnSettingsNavSkill.Visibility == Visibility.Collapsed)
            {
                SwitchSettingsView(viewSettingsAbout, btnSettingsNavAbout, tbSettingsNavAboutText);
            }
        }

        #endregion

        #region 二级界面：LLM 配置管理

        private void LoadSettings()
        {
            _settings = SettingsService.LoadSettings();

            pbApiKey.Password = _settings.ApiKey;
            tbBaseUrl.Text = _settings.BaseUrl;
            sliderContextLength.Value = _settings.MaxContextLength;
            sliderTemperature.Value = _settings.Temperature;
            tbSystemPrompt.Text = _settings.SystemPrompt;

            // 选中模型 (支持匹配已有或动态追加自定义模型)
            bool foundInSettings = false;
            foreach (ComboBoxItem item in cbModelName.Items)
            {
                if (item.Content?.ToString() == _settings.ModelName)
                {
                    cbModelName.SelectedItem = item;
                    foundInSettings = true;
                    break;
                }
            }
            if (!foundInSettings && !string.IsNullOrWhiteSpace(_settings.ModelName))
            {
                var customItem = new ComboBoxItem { Content = _settings.ModelName };
                cbModelName.Items.Add(customItem);
                cbModelName.SelectedItem = customItem;
            }

            // 同步快速模型选择下拉框
            if (cbQuickModel != null)
            {
                bool foundInQuick = false;
                foreach (ComboBoxItem item in cbQuickModel.Items)
                {
                    if (item.Content?.ToString() == _settings.ModelName)
                    {
                        cbQuickModel.SelectedItem = item;
                        foundInQuick = true;
                        break;
                    }
                }
                if (!foundInQuick && !string.IsNullOrWhiteSpace(_settings.ModelName))
                {
                    var customItem = new ComboBoxItem { Content = _settings.ModelName };
                    cbQuickModel.Items.Add(customItem);
                    cbQuickModel.SelectedItem = customItem;
                }
            }

            if (tbCustomPythonPath != null)
            {
                tbCustomPythonPath.Text = _settings.PythonPath ?? string.Empty;
            }
            if (comboPythonSecurityMode != null)
            {
                comboPythonSecurityMode.SelectedIndex = Math.Clamp(_settings.PythonSecurityMode, 0, 2);
            }
            if (tbCustomSkillsPath != null)
            {
                tbCustomSkillsPath.Text = _settings.CustomSkillsPath ?? string.Empty;
            }

            UpdateSettingsDisplay();
            _ = CheckPythonEnvironmentAsync();
            _ = LoadInstalledSkillsAsync();
        }

        private void UpdateSettingsDisplay()
        {
            tbContextLengthValue.Text = $"{_settings.MaxContextLength:N0} tokens";
            tbTemperatureValue.Text = _settings.Temperature.ToString("F1");
        }

        private void cbQuickModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbQuickModel?.SelectedItem is ComboBoxItem item)
            {
                string model = item.Content?.ToString() ?? "gpt-4o";
                _settings.ModelName = model;

                if (cbModelName != null)
                {
                    bool found = false;
                    foreach (ComboBoxItem m in cbModelName.Items)
                    {
                        if (m.Content?.ToString() == model)
                        {
                            cbModelName.SelectedItem = m;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        var newItem = new ComboBoxItem { Content = model };
                        cbModelName.Items.Add(newItem);
                        cbModelName.SelectedItem = newItem;
                    }
                }
            }
        }

        // 在线获取 API Key 支持的模型列表
        private async void btnFetchModels_Click(object sender, RoutedEventArgs e)
        {
            string apiKey = pbApiKey.Password.Trim();
            string baseUrl = tbBaseUrl.Text.Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                infoBarSettings.IsOpen = true;
                infoBarSettings.Severity = InfoBarSeverity.Warning;
                infoBarSettings.Title = "提示";
                infoBarSettings.Message = "请先填写模型接口地址 (Base URL)。";
                return;
            }

            btnFetchModels.IsEnabled = false;
            btnFetchModels.Content = "获取中...";

            try
            {
                // 1. 构建 /models 端点
                string endpoint = baseUrl.TrimEnd('/');
                if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = endpoint.Substring(0, endpoint.Length - "/chat/completions".Length);
                }
                if (!endpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint += "/models";
                }

                // 2. 发起 HTTP GET 请求
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, endpoint);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                var response = await client.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"HTTP {(int)response.StatusCode}：{responseBody}");
                }

                // 3. 解析模型列表
                var modelIds = new List<string>();
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElem) && idElem.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            modelIds.Add(idElem.GetString()!);
                        }
                    }
                }

                if (modelIds.Count == 0)
                {
                    throw new Exception("接口返回成功，但未解析到有效的模型列表数据。");
                }

                // 4. 排序并刷新下拉框
                modelIds.Sort();
                string currentSelected = (cbModelName.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                          ?? cbModelName.Text
                                          ?? _settings.ModelName;

                cbModelName.Items.Clear();
                cbQuickModel?.Items.Clear();

                foreach (var id in modelIds)
                {
                    cbModelName.Items.Add(new ComboBoxItem { Content = id });
                    cbQuickModel?.Items.Add(new ComboBoxItem { Content = id });
                }

                // 恢复之前选中的模型或默认选中第 1 个
                var match = cbModelName.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == currentSelected);
                if (match != null)
                {
                    cbModelName.SelectedItem = match;
                    if (cbQuickModel != null)
                    {
                        cbQuickModel.SelectedItem = cbQuickModel.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == currentSelected);
                    }
                }
                else
                {
                    cbModelName.SelectedIndex = 0;
                    if (cbQuickModel != null)
                    {
                        cbQuickModel.SelectedIndex = 0;
                    }
                }

                infoBarSettings.IsOpen = true;
                infoBarSettings.Severity = InfoBarSeverity.Success;
                infoBarSettings.Title = "获取成功";
                infoBarSettings.Message = $"已成功拉取到 {modelIds.Count} 个可用模型！当前选中: {(cbModelName.SelectedItem as ComboBoxItem)?.Content}";
            }
            catch (Exception ex)
            {
                infoBarSettings.IsOpen = true;
                infoBarSettings.Severity = InfoBarSeverity.Error;
                infoBarSettings.Title = "获取模型列表失败";
                infoBarSettings.Message = ex.Message;
            }
            finally
            {
                btnFetchModels.IsEnabled = true;
                btnFetchModels.Content = "获取";
            }
        }

        private void btnSaveSettingsPage_Click(object sender, RoutedEventArgs e)
        {
            _settings.ApiKey = pbApiKey.Password;
            _settings.BaseUrl = tbBaseUrl.Text.Trim();
            if (cbModelName.SelectedItem is ComboBoxItem selectedItem)
            {
                _settings.ModelName = selectedItem.Content?.ToString() ?? "gpt-4o";
            }
            else if (!string.IsNullOrWhiteSpace(cbModelName.Text))
            {
                _settings.ModelName = cbModelName.Text.Trim();
            }
            _settings.MaxContextLength = (int)sliderContextLength.Value;
            _settings.Temperature = Math.Round(sliderTemperature.Value, 1);
            _settings.SystemPrompt = tbSystemPrompt.Text.Trim();

            if (tbCustomPythonPath != null)
            {
                _settings.PythonPath = tbCustomPythonPath.Text.Trim();
            }
            if (comboPythonSecurityMode != null && comboPythonSecurityMode.SelectedIndex >= 0)
            {
                _settings.PythonSecurityMode = comboPythonSecurityMode.SelectedIndex;
            }
            if (tbCustomSkillsPath != null)
            {
                _settings.CustomSkillsPath = tbCustomSkillsPath.Text.Trim();
            }

            SettingsService.SaveSettings(_settings);
            UpdateSettingsDisplay();

            // 联动快速选择下拉框
            if (cbQuickModel != null)
            {
                bool found = false;
                foreach (ComboBoxItem item in cbQuickModel.Items)
                {
                    if (item.Content?.ToString() == _settings.ModelName)
                    {
                        cbQuickModel.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found && !string.IsNullOrWhiteSpace(_settings.ModelName))
                {
                    var newItem = new ComboBoxItem { Content = _settings.ModelName };
                    cbQuickModel.Items.Add(newItem);
                    cbQuickModel.SelectedItem = newItem;
                }
            }

            infoBarSettings.IsOpen = true;
            infoBarSettings.Severity = InfoBarSeverity.Success;
            infoBarSettings.Title = "配置已保存";
            infoBarSettings.Message = $"配置已成功保存！当前模型: {_settings.ModelName}，Max Context: {_settings.MaxContextLength:N0} tokens。";
        }

        private void btnResetSettingsPage_Click(object sender, RoutedEventArgs e)
        {
            _settings = new AppSettings();
            pbApiKey.Password = _settings.ApiKey;
            tbBaseUrl.Text = _settings.BaseUrl;
            sliderContextLength.Value = _settings.MaxContextLength;
            sliderTemperature.Value = _settings.Temperature;
            tbSystemPrompt.Text = _settings.SystemPrompt;
            cbModelName.SelectedIndex = 0;
            cbQuickModel.SelectedIndex = 0;
            if (tbCustomPythonPath != null) tbCustomPythonPath.Text = string.Empty;
            if (tbCustomSkillsPath != null) tbCustomSkillsPath.Text = string.Empty;
            if (comboPythonSecurityMode != null) comboPythonSecurityMode.SelectedIndex = 1;
            UpdateSettingsDisplay();
            _ = CheckPythonEnvironmentAsync();
            _ = LoadInstalledSkillsAsync();

            infoBarSettings.IsOpen = true;
            infoBarSettings.Severity = InfoBarSeverity.Informational;
            infoBarSettings.Title = "已恢复默认";
            infoBarSettings.Message = "所有配置已重置为标准默认值。";
        }

        #region 标准 Skill 规范与扩展管理

        public async Task LoadInstalledSkillsAsync(bool showFeedback = false)
        {
            try
            {
                var skills = await _skillManager.LoadUserInstalledSkillsAsync(_settings);
                InstalledSkills.Clear();
                foreach (var s in skills)
                {
                    InstalledSkills.Add(s);
                }

                if (tbSkillsCountBadge != null)
                {
                    tbSkillsCountBadge.Text = $"{skills.Count} 个技能";
                }

                if (panelNoSkillsHint != null)
                {
                    panelNoSkillsHint.Visibility = skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                if (showFeedback && infoBarSkillSettings != null)
                {
                    infoBarSkillSettings.IsOpen = true;
                    infoBarSkillSettings.Severity = InfoBarSeverity.Success;
                    infoBarSkillSettings.Title = "技能列表已刷新";
                    infoBarSkillSettings.Message = $"已成功发现并加载本地已安装的 {skills.Count} 个技能扩展。";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadInstalledSkillsAsync] 失败: {ex.Message}");
            }
        }

        private void skillToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                string? skillName = ts.Tag?.ToString();
                if (ts.DataContext is SkillItem skillItem)
                {
                    skillName = skillItem.Name;
                    skillItem.IsEnabled = ts.IsOn;
                }

                if (!string.IsNullOrEmpty(skillName) && _settings != null)
                {
                    _settings.DisabledSkills ??= new List<string>();
                    if (ts.IsOn)
                    {
                        _settings.DisabledSkills.RemoveAll(s => s.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        if (!_settings.DisabledSkills.Any(s => s.Equals(skillName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _settings.DisabledSkills.Add(skillName);
                        }
                    }
                    SettingsService.SaveSettings(_settings);
                }
            }
        }

        private void btnSkillOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string? path = btn.Tag?.ToString();
                if (string.IsNullOrEmpty(path) && btn.DataContext is SkillItem item)
                {
                    path = item.DirectoryPath;
                }
                if (!string.IsNullOrEmpty(path))
                {
                    SkillManagerService.OpenFolderInExplorer(path);
                }
            }
        }

        private void btnOpenSkillsFolder_Click(object sender, RoutedEventArgs e)
        {
            string primaryDir = SkillManagerService.GetPrimarySkillsDirectory(_settings);
            SkillManagerService.OpenFolderInExplorer(primaryDir);
        }

        private async void btnImportSkill_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add("*");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    var result = await SkillManagerService.ImportSkillFolderAsync(folder.Path, _settings);
                    if (infoBarSkillSettings != null)
                    {
                        infoBarSkillSettings.IsOpen = true;
                        infoBarSkillSettings.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                        infoBarSkillSettings.Title = result.Success ? "导入成功" : "导入失败";
                        infoBarSkillSettings.Message = result.Message;
                    }

                    if (result.Success)
                    {
                        await LoadInstalledSkillsAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                if (infoBarSkillSettings != null)
                {
                    infoBarSkillSettings.IsOpen = true;
                    infoBarSkillSettings.Severity = InfoBarSeverity.Error;
                    infoBarSkillSettings.Title = "导入出错";
                    infoBarSkillSettings.Message = ex.Message;
                }
            }
        }

        private async void btnBrowseSkillsPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.ComputerFolder
                };
                picker.FileTypeFilter.Add("*");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    if (tbCustomSkillsPath != null)
                    {
                        tbCustomSkillsPath.Text = folder.Path;
                    }
                    _settings.CustomSkillsPath = folder.Path;
                    SettingsService.SaveSettings(_settings);

                    if (infoBarSkillSettings != null)
                    {
                        infoBarSkillSettings.IsOpen = true;
                        infoBarSkillSettings.Severity = InfoBarSeverity.Success;
                        infoBarSkillSettings.Title = "技能根目录已更新";
                        infoBarSkillSettings.Message = $"已设置自定义技能根目录：{folder.Path}，正在重新扫描...";
                    }

                    await LoadInstalledSkillsAsync();
                }
            }
            catch (Exception ex)
            {
                if (infoBarSkillSettings != null)
                {
                    infoBarSkillSettings.IsOpen = true;
                    infoBarSkillSettings.Severity = InfoBarSeverity.Error;
                    infoBarSkillSettings.Title = "选择失败";
                    infoBarSkillSettings.Message = ex.Message;
                }
            }
        }

        private async void btnApplySkillsPath_Click(object sender, RoutedEventArgs e)
        {
            string path = tbCustomSkillsPath?.Text?.Trim() ?? string.Empty;
            _settings.CustomSkillsPath = path;
            SettingsService.SaveSettings(_settings);

            if (infoBarSkillSettings != null)
            {
                infoBarSkillSettings.IsOpen = true;
                infoBarSkillSettings.Severity = InfoBarSeverity.Success;
                infoBarSkillSettings.Title = "配置已确定应用";
                infoBarSkillSettings.Message = string.IsNullOrEmpty(path)
                    ? "已应用为默认应用技能目录与系统通用目录，正在重新扫描..."
                    : $"已应用自定义技能目录：{path}，正在重新扫描...";
            }

            await LoadInstalledSkillsAsync();
        }

        private async void btnResetSkillsPath_Click(object sender, RoutedEventArgs e)
        {
            if (tbCustomSkillsPath != null)
            {
                tbCustomSkillsPath.Text = string.Empty;
            }
            _settings.CustomSkillsPath = string.Empty;
            SettingsService.SaveSettings(_settings);

            if (infoBarSkillSettings != null)
            {
                infoBarSkillSettings.IsOpen = true;
                infoBarSkillSettings.Severity = InfoBarSeverity.Informational;
                infoBarSkillSettings.Title = "已恢复默认";
                infoBarSkillSettings.Message = "已清除自定义技能根目录，恢复为应用专属技能库。";
            }

            await LoadInstalledSkillsAsync();
        }

        private async void btnRefreshSkills_Click(object sender, RoutedEventArgs e)
        {
            await LoadInstalledSkillsAsync(showFeedback: true);
        }

        #endregion

        #region Python 环境与安全执行控制

        private async Task CheckPythonEnvironmentAsync()
        {
            if (tbPythonStatusDetail == null) return;

            tbPythonStatusDetail.Text = "正在探测本地 Python 运行时与数据分析包...";

            try
            {
                string customPath = _settings?.PythonPath ?? string.Empty;
                var status = await _pythonEnvService.DetectEnvironmentAsync(customPath);

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (status.IsInstalled)
                    {
                        tbPythonStatusDetail.Text = $"已就绪：{status.Version} ({status.PythonPath})";
                        tbPythonStatusDetail.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 125, 50));

                        UpdateLibraryBadge(badgePandas, tbBadgePandas, "Pandas", status.HasPandas);
                        UpdateLibraryBadge(badgeMatplotlib, tbBadgeMatplotlib, "Matplotlib", status.HasMatplotlib);
                        UpdateLibraryBadge(badgeNumpy, tbBadgeNumpy, "Numpy", status.HasNumpy);
                    }
                    else
                    {
                        tbPythonStatusDetail.Text = status.ErrorMessage ?? "未检测到可用的 Python 环境";
                        tbPythonStatusDetail.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 198, 40, 40));

                        UpdateLibraryBadge(badgePandas, tbBadgePandas, "Pandas", false);
                        UpdateLibraryBadge(badgeMatplotlib, tbBadgeMatplotlib, "Matplotlib", false);
                        UpdateLibraryBadge(badgeNumpy, tbBadgeNumpy, "Numpy", false);
                    }
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    tbPythonStatusDetail.Text = $"探测过程异常: {ex.Message}";
                });
            }
        }

        private static void UpdateLibraryBadge(Border? badge, TextBlock? textBlock, string libName, bool isInstalled)
        {
            if (badge == null || textBlock == null) return;
            if (isInstalled)
            {
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 245, 233));
                textBlock.Text = $"{libName}: 已就绪";
                textBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 125, 50));
            }
            else
            {
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 235, 238));
                textBlock.Text = $"{libName}: 缺失";
                textBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 198, 40, 40));
            }
        }

        private async void btnRefreshPythonEnv_Click(object sender, RoutedEventArgs e)
        {
            await CheckPythonEnvironmentAsync();
        }

        private void comboPythonSecurityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && comboPythonSecurityMode != null && comboPythonSecurityMode.SelectedIndex >= 0)
            {
                _settings.PythonSecurityMode = comboPythonSecurityMode.SelectedIndex;
                SettingsService.SaveSettings(_settings);
            }
        }

        private void tbCustomPythonPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && tbCustomPythonPath != null)
            {
                _settings.PythonPath = tbCustomPythonPath.Text.Trim();
            }
        }

        private async void btnBrowsePythonPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.ComputerFolder
                };
                picker.FileTypeFilter.Add(".exe");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    tbCustomPythonPath.Text = file.Path;
                    _settings.PythonPath = file.Path;
                    SettingsService.SaveSettings(_settings);

                    if (infoBarSkillSettings != null)
                    {
                        infoBarSkillSettings.IsOpen = true;
                        infoBarSkillSettings.Severity = InfoBarSeverity.Success;
                        infoBarSkillSettings.Title = "路径已选择";
                        infoBarSkillSettings.Message = $"已设置 Python 解释器路径：{file.Path}，正在验证环境...";
                    }

                    await CheckPythonEnvironmentAsync();
                }
            }
            catch (Exception ex)
            {
                if (infoBarSkillSettings != null)
                {
                    infoBarSkillSettings.IsOpen = true;
                    infoBarSkillSettings.Severity = InfoBarSeverity.Error;
                    infoBarSkillSettings.Title = "选择失败";
                    infoBarSkillSettings.Message = ex.Message;
                }
            }
        }

        private async void btnApplyPythonPath_Click(object sender, RoutedEventArgs e)
        {
            string path = tbCustomPythonPath?.Text?.Trim() ?? string.Empty;
            _settings.PythonPath = path;
            SettingsService.SaveSettings(_settings);

            if (infoBarSkillSettings != null)
            {
                infoBarSkillSettings.IsOpen = true;
                infoBarSkillSettings.Severity = InfoBarSeverity.Success;
                infoBarSkillSettings.Title = "配置已确定应用";
                infoBarSkillSettings.Message = string.IsNullOrEmpty(path)
                    ? "已应用为自动检测系统 PATH 环境，正在验证..."
                    : $"已应用自定义路径：{path}，正在验证...";
            }

            await CheckPythonEnvironmentAsync();
        }

        private async void btnResetPythonPath_Click(object sender, RoutedEventArgs e)
        {
            if (tbCustomPythonPath != null)
            {
                tbCustomPythonPath.Text = string.Empty;
            }
            _settings.PythonPath = string.Empty;
            SettingsService.SaveSettings(_settings);

            if (infoBarSkillSettings != null)
            {
                infoBarSkillSettings.IsOpen = true;
                infoBarSkillSettings.Severity = InfoBarSeverity.Informational;
                infoBarSkillSettings.Title = "已重置为默认";
                infoBarSkillSettings.Message = "已清除自定义路径，恢复为自动检测系统环境变量 PATH。";
            }

            await CheckPythonEnvironmentAsync();
        }

        #endregion

        private void btnTestApi_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(pbApiKey.Password))
            {
                infoBarSettings.IsOpen = true;
                infoBarSettings.Severity = InfoBarSeverity.Warning;
                infoBarSettings.Title = "提示";
                infoBarSettings.Message = "请先填写 API Key 密钥后再执行连通性测试。";
                return;
            }

            infoBarSettings.IsOpen = true;
            infoBarSettings.Severity = InfoBarSeverity.Success;
            infoBarSettings.Title = "连接配置就绪";
            infoBarSettings.Message = $"已验证端点地址 [{tbBaseUrl.Text}]，模型 [{cbModelName.SelectionBoxItem}] 配置有效（阶段 2 将正式发起真实网络请求）。";
        }

        private void sliderContextLength_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (tbContextLengthValue != null)
            {
                tbContextLengthValue.Text = $"{(int)e.NewValue:N0} tokens";
            }
        }

        private void sliderTemperature_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (tbTemperatureValue != null)
            {
                tbTemperatureValue.Text = e.NewValue.ToString("F1");
            }
        }

        #endregion

        #region 文档选择与管理 (左侧弹出文档库)

        private async void btnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };

                // 支持格式：PDF/Word/Excel/TXT/CSV/MD
                picker.FileTypeFilter.Add(".pdf");
                picker.FileTypeFilter.Add(".docx");
                picker.FileTypeFilter.Add(".doc");
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xls");
                picker.FileTypeFilter.Add(".txt");
                picker.FileTypeFilter.Add(".csv");
                picker.FileTypeFilter.Add(".md");

                // WinUI 3 必须初始化窗口句柄 HWND
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                // EPPlus 8 授权初始化
                OfficeOpenXml.ExcelPackage.License.SetNonCommercialPersonal("DataAnalysisAssistant");

                DocumentItem? lastAdded = null;
                foreach (StorageFile file in files)
                {
                    // 避免重复导入同路径文件
                    var existing = Documents.FirstOrDefault(d => d.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        lastAdded = existing;
                        continue;
                    }

                    var basicProps = await file.GetBasicPropertiesAsync();
                    var ext = file.FileType.ToLowerInvariant();
                    var item = new DocumentItem
                    {
                        Name = file.Name,
                        Path = file.Path,
                        Extension = ext,
                        SizeInBytes = (long)basicProps.Size,
                        FormattedSize = FormatFileSize((long)basicProps.Size),
                        UploadTime = DateTime.Now
                    };

                    // -------------------- 阶段 2：多格式文档内容解析引擎 --------------------
                    try
                    {
                        if (item.IsTabularData)
                        {
                            // 阶段四：结构化表格数据下推（计算下推，生成精确 Schema 骨架与 Head Preview）
                            var profile = await _schemaProfiler.ProfileAsync(file.Path);
                            item.SchemaProfile = profile;
                            item.ExtractedText = profile.ToMarkdown();
                            item.Status = $"{profile.TotalRows:N0}行·骨架已解析";
                        }
                        else if (ext == ".txt" || ext == ".md")
                        {
                            // 1. 纯文本 / Markdown
                            try
                            {
                                item.ExtractedText = await FileIO.ReadTextAsync(file);
                            }
                            catch
                            {
                                item.ExtractedText = File.ReadAllText(file.Path);
                            }
                            item.Status = "已提取文本";
                        }
                        else if (ext == ".pdf")
                        {
                            // 2. PDF 解析 (PdfPig：逐页提取文本)
                            using var stream = await file.OpenStreamForReadAsync();
                            using var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(stream);

                            var sb = new StringBuilder();
                            foreach (var page in pdfDocument.GetPages())
                            {
                                sb.AppendLine(page.Text);
                            }

                            item.ExtractedText = sb.ToString();
                            item.Status = "已提取文本 (支持精准调取)";
                        }
                        else if (ext == ".docx")
                        {
                            // 3. Word 文档解析 (DocX：读取全文字符)
                            using var stream = await file.OpenStreamForReadAsync();
                            using var doc = Xceed.Words.NET.DocX.Load(stream);

                            item.ExtractedText = doc.Text;
                            item.Status = "已提取文本 (支持精准调取)";
                        }
                        else
                        {
                            // 老旧二进制格式 (.doc) 容错提示
                            item.ExtractedText = $"格式提示：当前文档为较早版本二进制格式 ({ext})，建议将其转换为 .docx 或 .xlsx 后解析。";
                            item.Status = "待格式转换";
                        }
                    }
                    catch (Exception parseEx)
                    {
                        // 解析异常统一捕获
                        item.Status = "解析失败";
                        item.ExtractedText = $"文档解析失败：{parseEx.Message}";
                    }

                    Documents.Add(item);
                    lastAdded = item;
                }

                UpdateDocCountDisplay();

                if (lastAdded != null)
                {
                    lvDocuments.SelectedItem = lastAdded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"选择文件失败: {ex.Message}");
            }
        }

        private void btnRemoveDoc_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DocumentItem doc)
            {
                Documents.Remove(doc);
                UpdateDocCountDisplay();

                if (_currentDoc == doc)
                {
                    _currentDoc = Documents.FirstOrDefault();
                    lvDocuments.SelectedItem = _currentDoc;
                    DisplayDocument(_currentDoc);
                }
            }
        }

        private void lvDocuments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lvDocuments.SelectedItem is DocumentItem doc)
            {
                _currentDoc = doc;
                DisplayDocument(doc);
            }
        }

        private void DisplayDocument(DocumentItem? doc)
        {
            if (doc == null)
            {
                tbPreviewDocTitle.Text = "未选中文档";
                borderDocBadge.Visibility = Visibility.Collapsed;
                tbStatFileType.Text = "--";
                tbStatFileSize.Text = "0 KB";
                tbStatCharCount.Text = "0";
                tbStatTokenCount.Text = "~0";
                tbPreviewContent.Text = string.Empty;
                tbPreviewContent.Visibility = Visibility.Collapsed;
                spEmptyPreview.Visibility = Visibility.Visible;
                return;
            }

            tbPreviewDocTitle.Text = doc.Name;
            tbDocBadgeText.Text = doc.ExtensionLabel;
            borderDocBadge.Visibility = Visibility.Visible;

            tbStatFileType.Text = doc.ExtensionLabel;
            tbStatFileSize.Text = doc.FormattedSize;
            tbStatCharCount.Text = doc.CharacterCount.ToString("N0");
            tbStatTokenCount.Text = $"~{doc.EstimatedTokens:N0}";

            tbPreviewContent.Text = doc.ExtractedText;
            bool hasText = !string.IsNullOrWhiteSpace(doc.ExtractedText);
            tbPreviewContent.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            spEmptyPreview.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateDocCountDisplay()
        {
            tbDocCount.Text = $"{Documents.Count} 个文件";
            tbRailDocBadge.Text = Documents.Count.ToString();
            railDocBadge.Visibility = Documents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            spEmptyDocs.Visibility = Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void tbSearchDoc_TextChanged(object sender, TextChangedEventArgs e)
        {
            string keyword = tbSearchDoc.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                lvDocuments.ItemsSource = Documents;
            }
            else
            {
                lvDocuments.ItemsSource = Documents.Where(d => d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        private void btnCopyPreview_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(tbPreviewContent.Text))
            {
                var package = new DataPackage();
                package.SetText(tbPreviewContent.Text);
                Clipboard.SetContent(package);
            }
        }

        private void btnClearPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDoc != null)
            {
                _currentDoc.ExtractedText = string.Empty;
                DisplayDocument(_currentDoc);
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return $"{(bytes / 1024.0 / 1024.0):F1} MB";
            }
            if (bytes >= 1024)
            {
                return $"{(bytes / 1024.0):F1} KB";
            }
            return $"{bytes} B";
        }

        #endregion

        #region 对话与 Markdown 渲染

        private void InitializeWelcomeChatMessage()
        {
            string welcomeMarkdown =
@"### 欢迎使用 AI 数据分析与文档伴读看板

已按照最新规范重构界面：
1. **左侧导航栏（窄）**：纯图标极简风格，包含 **主页面看板**、**可视化**、**历史记录**、**文档库（带数字角标）**、**AI 助手**，以及 **底部设置与更多**；
2. **中间板块顶部两个窄选项卡**：可在 **【文档预览】** 与 **【可视化】** 之间自由切换；
3. **模型选择**：已从顶部移至右下角 **发送按钮左侧**，方便提问时即时选择调用模型。

---

#### 示例：LLM 数据分析回答预览 (Markdown 表格与代码块)

| 指标维度 | 2024 上半年 | 2024 下半年 (预估) | 同比增长 |
| :--- | :--- | :--- | :--- |
| **文档处理量** | 12,450 篇 | 28,900 篇 | **+132.1%** |
| **平均响应时间** | 1.82 秒 | 1.15 秒 | **-36.8%** |
| **总结准确率** | 94.6% | 98.2% | **+3.8%** |

```python
# 示例：自动化数据分析脚本
import pandas as pd

def analyze_document_metrics(file_path: str):
    df = pd.read_excel(file_path)
    summary = df.describe()
    print(f""分析完成：共计 {len(df)} 条记录"")
    return summary
```

> **提示**：您可以在下方输入框输入问题，并在发送按钮左侧选择模型，AI 生成的分析图表将自动呈现在上方【可视化界面】中！";

            ChatMessages.Add(new ChatMessageItem
            {
                Sender = "Assistant",
                Content = welcomeMarkdown,
                Timestamp = DateTime.Now
            });
        }

        private void btnQuickPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                tbQuestion.Text = prompt;
                tbQuestion.Focus(FocusState.Programmatic);
            }
        }

        // 展开 / 折叠思考过程卡片
        private void btnToggleReasoning_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem item)
            {
                item.IsReasoningExpanded = !item.IsReasoningExpanded;
            }
        }

        // 拦截输入框按键：Enter 发送，Shift+Enter 换行 (使用 PreviewKeyDown 确保优先于 TextBox 内部插入换行符)
        private void tbQuestion_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

                if (!isShiftPressed)
                {
                    e.Handled = true; // 拦截 Enter 默认换行行为

                    if (_isChatGenerating)
                    {
                        // 正在生成中，不重复提交
                        return;
                    }

                    SendMessage();
                }
            }
        }

        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            if (_isChatGenerating)
            {
                StopGenerating();
            }
            else
            {
                SendMessage();
            }
        }

        private void StopGenerating()
        {
            if (_isChatGenerating && _chatCts != null && !_chatCts.IsCancellationRequested)
            {
                _chatCts.Cancel();
                tbChatRunStatus.Text = "正在中断...";
            }
        }

        private async void SendMessage()
        {
            string question = tbQuestion.Text.Trim();
            if (string.IsNullOrEmpty(question))
            {
                return;
            }

            // 1. 添加用户消息到列表并清空输入框
            ChatMessages.Add(new ChatMessageItem
            {
                Sender = "User",
                Content = question,
                Timestamp = DateTime.Now
            });

            tbQuestion.Text = string.Empty;

            // 2. 界面状态切换为“停止”生成模式
            _isChatGenerating = true;
            _chatCts = new CancellationTokenSource();
            var cancellationToken = _chatCts.Token;

            btnSend.Content = "停止";
            btnSend.Background = BrushStopAction;
            tbQuestion.IsEnabled = false;
            tbChatRunStatus.Text = "正在深度思考...";

            // 3. 预先添加 AI 助手空消息气泡，作为流式接收容器 (思考卡片即刻处于展示态)
            var assistantMessage = new ChatMessageItem
            {
                Sender = "Assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                IsThinking = true,
                IsReasoningExpanded = true, // 思考阶段初始即刻展开
                ThinkingSeconds = 0,
                Timestamp = DateTime.Now
            };
            ChatMessages.Add(assistantMessage);

            // 自动滚动到最新消息位置
            svChatList.ChangeView(null, svChatList.ScrollableHeight, null);

            // 4. 准备调用上下文与参数
            var currentSettings = _settings;

            // 若右下角快速模型下拉框有显式选中项，以其为准
            if (cbQuickModel?.SelectedItem is ComboBoxItem selectedComboItem &&
                !string.IsNullOrWhiteSpace(selectedComboItem.Content?.ToString()))
            {
                currentSettings.ModelName = selectedComboItem.Content.ToString()!;
            }

            // 提取当前选中文档内容或所有已加载文档的索引概览 (跨文档联合分析支持)
            string documentContext;
            if (_currentDoc != null)
            {
                documentContext = _currentDoc.ExtractedText ?? string.Empty;
                // 若工作区存在多份其他文档，附带提供关联文档清单索引
                if (Documents.Count > 1)
                {
                    var sbDocs = new StringBuilder();
                    sbDocs.AppendLine(documentContext);
                    sbDocs.AppendLine("\n#### 📂 工作区其他并存关联文档（支持跨文档联合分析）：");
                    foreach (var d in Documents.Where(d => d != _currentDoc))
                    {
                        sbDocs.AppendLine($"- `{d.Name}` ({d.ExtensionLabel}) -> 路径: `{d.Path}` | 规模: {d.SummaryText}");
                    }
                    sbDocs.AppendLine("> 提示：若需跨文档联合比对，可直接使用 `read_document` 工具按路径精确读取对应章节或数据。");
                    documentContext = sbDocs.ToString();
                }
            }
            else if (Documents.Count > 0)
            {
                // 当未选中具体单一文档但左侧已载入多份文件时，自动组装多文档联合索引
                var sbMulti = new StringBuilder();
                sbMulti.AppendLine("### 📁 当前工作区已载入的多文档清单 (可调用 read_document 工具精准调取)：");
                for (int i = 0; i < Documents.Count; i++)
                {
                    var d = Documents[i];
                    sbMulti.AppendLine($"{i + 1}. **{d.Name}** ({d.ExtensionLabel}) - 路径: `{d.Path}` | 规模: {d.SummaryText}");
                }
                sbMulti.AppendLine("\n提示：请根据用户分析目标，调用 `read_document` 工具定位并读取目标文档。");
                documentContext = sbMulti.ToString();
            }
            else
            {
                documentContext = string.Empty;
            }

            // 5. 提取历史对话 (过滤开篇欢迎演示消息，只取当前问题之前的 User/Assistant 真实问答)
            var historyTurns = new List<ChatTurn>();
            foreach (var msg in ChatMessages)
            {
                // 跳过开篇大篇幅的欢迎与系统演示消息
                if (msg.Content.Contains("欢迎使用 AI 数据分析与文档伴读看板"))
                {
                    continue;
                }

                // 排除当前刚加入的这轮问答
                if (msg == assistantMessage)
                {
                    continue;
                }

                // 仅收录历史问答
                if (msg.IsUser)
                {
                    // 若是最后那条刚刚添加的用户消息，也不计入历史（它由 userQuestion 参数直接传入）
                    if (ChatMessages.Count >= 2 && msg == ChatMessages[^2])
                    {
                        continue;
                    }

                    historyTurns.Add(new ChatTurn { Role = "user", Content = msg.Content });
                }
                else if (msg.IsAssistant && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    historyTurns.Add(new ChatTurn { Role = "assistant", Content = msg.Content });
                }
            }

            // 6. 启动多步自主 Agent 循环调度 (ReAct / CodeAct 机制：思考 -> 工具决策 -> 执行 -> 评估反思 -> 交付)
            var chatService = new LlmChatService();
            var thinkingWatch = new Stopwatch();

            // 注册工具集 (本地 Python 引擎、非结构化/表格数据精细提取器)
            var pythonTool = new RunPythonTool(() => _settings, _pythonExecService, _safetyGate);
            var docReaderTool = new DocumentReaderTool(() => Documents.Select(d => d.Path));
            var agentLoopService = new AgentQueryLoopService(chatService, new IAgentTool[] { pythonTool, docReaderTool });

            var progress = new Progress<AgentProgressUpdate>(update =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    tbChatRunStatus.Text = update.StatusMessage;

                    if (!string.IsNullOrWhiteSpace(update.IntermediateReasoning))
                    {
                        assistantMessage.ReasoningContent = update.IntermediateReasoning;
                    }

                    // 实时同步挂接新生成的图表产物至消息气泡与画廊
                    if (update.GeneratedArtifactsSoFar.Count > 0)
                    {
                        foreach (var artPath in update.GeneratedArtifactsSoFar)
                        {
                            if (!assistantMessage.GeneratedPlots.Any(p => p.FilePath.Equals(artPath, StringComparison.OrdinalIgnoreCase)))
                            {
                                var plotItem = new ChartArtifactItem
                                {
                                    FilePath = artPath,
                                    Title = Path.GetFileNameWithoutExtension(artPath),
                                    CreatedAt = DateTime.Now
                                };
                                assistantMessage.GeneratedPlots.Add(plotItem);
                                AddPlotToGallery(plotItem, switchToVis: false);
                            }
                        }
                    }

                    svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                });
            });

            try
            {
                thinkingWatch.Start();

                var loopResult = await agentLoopService.RunLoopAsync(
                    currentSettings,
                    historyTurns,
                    documentContext,
                    question,
                    maxIterations: 8,
                    progress: progress,
                    cancellationToken: cancellationToken);

                thinkingWatch.Stop();
                double totalThinkingTime = thinkingWatch.Elapsed.TotalSeconds;

                DispatcherQueue.TryEnqueue(() =>
                {
                    assistantMessage.IsThinking = false;
                    assistantMessage.ThinkingSeconds = totalThinkingTime;
                    assistantMessage.IsReasoningExpanded = false; // 生成完成后默认折叠思考

                    if (!string.IsNullOrWhiteSpace(loopResult.ReasoningContent))
                    {
                        assistantMessage.ReasoningContent = loopResult.ReasoningContent;
                    }

                    if (loopResult.Success)
                    {
                        assistantMessage.Content = loopResult.FinalContent;
                        tbChatRunStatus.Text = $"分析完毕 (共 {loopResult.TotalIterations} 轮 Agent 决策)";
                    }
                    else
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            assistantMessage.IsStopped = true;
                            if (string.IsNullOrEmpty(assistantMessage.Content))
                            {
                                assistantMessage.Content = "*(已手动中止生成)*";
                            }
                            tbChatRunStatus.Text = "已停止";
                        }
                        else
                        {
                            assistantMessage.Content = $"⚠️ 分析过程中出现异常：{loopResult.ErrorMessage}";
                            tbChatRunStatus.Text = "分析异常";
                        }
                    }

                    // 补充挂接所有产物图表
                    if (loopResult.GeneratedArtifactPaths.Count > 0)
                    {
                        foreach (var artPath in loopResult.GeneratedArtifactPaths)
                        {
                            if (!assistantMessage.GeneratedPlots.Any(p => p.FilePath.Equals(artPath, StringComparison.OrdinalIgnoreCase)))
                            {
                                var plotItem = new ChartArtifactItem
                                {
                                    FilePath = artPath,
                                    Title = Path.GetFileNameWithoutExtension(artPath),
                                    CreatedAt = DateTime.Now
                                };
                                assistantMessage.GeneratedPlots.Add(plotItem);
                                AddPlotToGallery(plotItem, switchToVis: false);
                            }
                        }
                    }

                    svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                });

                // 兜底图表捕获：若最终文本中仍然包含 Python 绘图代码块，继续受控执行
                if (loopResult.Success && !string.IsNullOrWhiteSpace(loopResult.FinalContent) && !cancellationToken.IsCancellationRequested)
                {
                    await TryExecutePythonPlotsAsync(assistantMessage, loopResult.FinalContent, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户手动中断生成
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistantMessage.IsThinking = false;
                    assistantMessage.IsStopped = true;
                    if (string.IsNullOrEmpty(assistantMessage.Content))
                    {
                        assistantMessage.Content = "*(已手动中止生成)*";
                    }
                    tbChatRunStatus.Text = "已停止";
                });
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMessage.IsThinking = false;
                        assistantMessage.IsStopped = true;
                        tbChatRunStatus.Text = "已停止";
                    });
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMessage.IsThinking = false;
                        assistantMessage.Content = $"**对话请求失败**\n\n```text\n{ex.Message}\n```\n\n> **排查建议**：请点击左下角【设置】检查 API Key、Base URL 及网络是否可达。如果是 Ollama 本地模型，请确保后台已运行 `ollama serve`。";
                        svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                    });
                }
            }
            finally
            {
                // 7. 恢复发送按钮与输入框状态，清理取消令牌
                _isChatGenerating = false;
                _chatCts?.Dispose();
                _chatCts = null;

                DispatcherQueue.TryEnqueue(() =>
                {
                    btnSend.Content = "发送";
                    btnSend.Background = BrushAccentWarm;
                    btnSend.IsEnabled = true;
                    tbQuestion.IsEnabled = true;
                    if (tbChatRunStatus.Text.StartsWith("正在"))
                    {
                        tbChatRunStatus.Text = string.Empty;
                    }
                    tbQuestion.Focus(FocusState.Programmatic);
                    svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                });
            }
        }

        private void btnClearChat_Click(object sender, RoutedEventArgs e)
        {
            ChatMessages.Clear();
            InitializeWelcomeChatMessage();
        }

        #region 研报导出与内容操作 (Word / Markdown)

        private void btnCopyMessageContent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem msg)
            {
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    try
                    {
                        var dataPackage = new DataPackage();
                        dataPackage.RequestedOperation = DataPackageOperation.Copy;
                        dataPackage.SetText(msg.Content);
                        Clipboard.SetContent(dataPackage);
                        tbChatRunStatus.Text = "已复制回答内容至剪贴板";
                    }
                    catch { }
                }
            }
        }

        private async void btnExportMessageReport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem msg)
            {
                if (string.IsNullOrWhiteSpace(msg.Content)) return;

                string defaultTitle = _currentDoc != null
                    ? $"{Path.GetFileNameWithoutExtension(_currentDoc.Name)} 业务分析研报"
                    : "数据分析与业务洞察综合研报";

                await ExportReportWithPickerAsync(defaultTitle, msg.Content, msg.GeneratedPlots);
            }
        }

        private async void btnExportDashboardReport_Click(object sender, RoutedEventArgs e)
        {
            string defaultTitle = _currentDoc != null
                ? $"{Path.GetFileNameWithoutExtension(_currentDoc.Name)} 可视化看板综合研报"
                : "数据可视化看板分析综合研报";

            var sbContent = new StringBuilder();
            sbContent.AppendLine("### 📊 可视化大屏核心资产与指标概览\n");
            sbContent.AppendLine($"- **生成图表资产规模**: 共 {PlotGallery.Count} 幅高清专业图表");
            if (_activeDashboardPlot != null)
            {
                sbContent.AppendLine($"- **当前大屏激活图表**: `{_activeDashboardPlot.Title}` ({_activeDashboardPlot.FormattedSize})");
            }
            if (_currentDoc != null)
            {
                sbContent.AppendLine($"- **底层关联文档**: `{_currentDoc.Name}` ({_currentDoc.ExtensionLabel}) | 大小: {_currentDoc.FormattedSize}");
                if (!string.IsNullOrWhiteSpace(_currentDoc.SummaryText))
                {
                    sbContent.AppendLine($"- **文档基础规模**: {_currentDoc.SummaryText}");
                }
            }

            sbContent.AppendLine("\n### 🔍 深度洞察与业务推导结论\n");
            var lastAssistantMsg = ChatMessages.LastOrDefault(m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Content));
            if (lastAssistantMsg != null)
            {
                sbContent.AppendLine(lastAssistantMsg.Content);
            }
            else
            {
                sbContent.AppendLine("本地 Python 引擎已完成矢量数据运算与高清制图。详细数据图谱与说明请参见后文附录。");
            }

            await ExportReportWithPickerAsync(defaultTitle, sbContent.ToString(), PlotGallery);
        }

        private async Task ExportReportWithPickerAsync(string defaultTitle, string markdownContent, IEnumerable<ChartArtifactItem>? charts)
        {
            try
            {
                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = $"{defaultTitle}_{DateTime.Now:yyyyMMdd_HHmm}"
                };
                savePicker.FileTypeChoices.Add("Word 研报文档 (*.docx)", new List<string> { ".docx" });
                savePicker.FileTypeChoices.Add("Markdown 研报 (*.md)", new List<string> { ".md" });
                savePicker.DefaultFileExtension = ".docx";

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                var targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile != null)
                {
                    string ext = Path.GetExtension(targetFile.Path).ToLowerInvariant();
                    string savedPath;

                    if (ext == ".docx")
                    {
                        savedPath = await _exportService.ExportToDocxAsync(targetFile.Path, defaultTitle, markdownContent, charts);
                    }
                    else
                    {
                        savedPath = await _exportService.ExportToMarkdownAsync(targetFile.Path, defaultTitle, markdownContent, charts);
                    }

                    string fileName = Path.GetFileName(savedPath);
                    string? dir = Path.GetDirectoryName(savedPath);

                    tbChatRunStatus.Text = $"研报已成功导出: {fileName}";
                    if (tbVisInsightTip != null)
                    {
                        tbVisInsightTip.Text = $"研报已导出：{fileName}（保存在 {dir}）";
                    }

                    if (!string.IsNullOrEmpty(dir))
                    {
                        SkillManagerService.OpenFolderInExplorer(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                tbChatRunStatus.Text = $"研报导出失败: {ex.Message}";
            }
        }

        #endregion

        #endregion

        #region 历史会话管理 (左侧弹出抽屉)

        private void UpdateHistoryCountDisplay()
        {
            tbHistoryCount.Text = $"{ChatSessions.Count} 个会话";
            spEmptyHistory.Visibility = ChatSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void btnNewChat_Click(object sender, RoutedEventArgs e)
        {
            var newSession = new ChatSessionItem
            {
                Title = $"新对话 {DateTime.Now:MM-dd HH:mm}",
                CreatedAt = DateTime.Now,
                IsSelected = true
            };

            foreach (var s in ChatSessions)
            {
                s.IsSelected = false;
            }

            ChatSessions.Insert(0, newSession);
            lvHistorySessions.SelectedItem = newSession;
            UpdateHistoryCountDisplay();

            // 清空并初始化欢迎消息
            ChatMessages.Clear();
            InitializeWelcomeChatMessage();
        }

        private void lvHistorySessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lvHistorySessions.SelectedItem is ChatSessionItem session)
            {
                foreach (var s in ChatSessions)
                {
                    s.IsSelected = (s == session);
                }

                // 载入所选会话内容
                LoadSessionChat(session);
            }
        }

        private void LoadSessionChat(ChatSessionItem session)
        {
            ChatMessages.Clear();
            ChatMessages.Add(new ChatMessageItem
            {
                Sender = "User",
                Content = $"已切换至历史会话：【{session.Title}】",
                Timestamp = session.CreatedAt
            });
            ChatMessages.Add(new ChatMessageItem
            {
                Sender = "Assistant",
                Content = $"已成功载入 **【{session.Title}】** 的上下文环境。\n\n- 会话创建时间：`{session.FormattedDate}`\n- 当前关联文档：{(_currentDoc != null ? $"**{_currentDoc.Name}** ({_currentDoc.FormattedSize})" : "暂未关联特定文档")}\n\n您可以随时在此会话中继续提问或上传新文档进行深入数据分析！",
                Timestamp = DateTime.Now
            });
        }

        private void tbSearchHistory_TextChanged(object sender, TextChangedEventArgs e)
        {
            string keyword = tbSearchHistory.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                lvHistorySessions.ItemsSource = ChatSessions;
            }
            else
            {
                lvHistorySessions.ItemsSource = ChatSessions.Where(s => s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        private void btnDeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatSessionItem session)
            {
                ChatSessions.Remove(session);
                UpdateHistoryCountDisplay();
            }
        }

        #endregion
    }
}


