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
using Microsoft.UI.Xaml.Media.Animation;
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
using DataAnalysis_Assistant.Services.Persistence;

namespace DataAnalysis_Assistant
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DocumentGroup> DocumentGroups { get; } = new();
        public ObservableCollection<DocumentItem> Documents { get; } = new();
        public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
        public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();
        public ObservableCollection<SkillItem> InstalledSkills { get; } = new();
        public ObservableCollection<SkillItem> FilteredSkills { get; } = new();
        public ObservableCollection<ChartArtifactItem> PlotGallery { get; } = new();

        private SkillItem? _activeMountedSkill;
        private bool _isUpdatingQuestionTextProgrammatically = false;

        private AppSettings _settings = new();
        private DocumentGroup? _currentGroup;
        private DocumentItem? _currentDoc;
        private ChatSessionItem? _currentSession;
        private ChartArtifactItem? _activeDashboardPlot;
        private ChartArtifactItem? _viewingPlot;
        private bool _isViewerFullscreen = false;
        private double _savedViewerWidth = 1050;
        private double _savedViewerHeight = 720;
        private bool _isResizingViewerLeft = false;
        private bool _isResizingViewerRight = false;
        private bool _isPanningViewer = false;
        private Windows.Foundation.Point _lastPanPointerPos;
        private readonly PythonEnvironmentService _pythonEnvService = new();
        private readonly PythonExecutionService _pythonExecService = new();
        private readonly SkillManagerService _skillManager = new();
        private readonly DataSchemaProfiler _schemaProfiler = new();
        private readonly SafetyGateService _safetyGate = new();
        private readonly ReportExportService _exportService = new();

        private static readonly SolidColorBrush BrushCardBg = new(Microsoft.UI.Colors.White);
        private static readonly SolidColorBrush BrushTransparent = new(Microsoft.UI.Colors.Transparent);
        private static readonly SolidColorBrush BrushTextPrimary = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x28, 0x22));
        private static readonly SolidColorBrush BrushTextSecondary = new(Windows.UI.Color.FromArgb(255, 0x7D, 0x74, 0x6A));
        private static readonly SolidColorBrush BrushAccentWarm = new(Windows.UI.Color.FromArgb(255, 0x8C, 0x58, 0x35));
        private static readonly SolidColorBrush BrushStopAction = new(Windows.UI.Color.FromArgb(255, 0xA8, 0x48, 0x36));

        private CancellationTokenSource? _chatCts;
        private bool _isChatGenerating = false;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly SemaphoreSlim _docsLoadLock = new(1, 1);
        private DateTime _lastDebouncePersistTime = DateTime.MinValue;
        private bool _isSelectingGroupProgrammatically = false;

        public MainWindow()
        {
            InitializeComponent();

            lvDocumentGroups.ItemsSource = DocumentGroups;
            lvDocuments.ItemsSource = Documents;
            icChatMessages.ItemsSource = ChatMessages;
            lvHistorySessions.ItemsSource = ChatSessions;
            lvPlotGallery.ItemsSource = PlotGallery;

            _ = LoadPersistedLibraryDataAsync();

            LoadSettings();

            _ = LoadInstalledSkillsAsync();

            InitializeWelcomeChatMessage();

            SetInitialWindowSize();

            InitSplitterCursor();

            _ = LoadAppBrandIconAsync();

            this.Activated += (s, e) => { _ = LoadAppBrandIconAsync(); };

            // 监听窗口尺寸变化，防止缩小窗口后右侧对话栏被挤出视图
            this.SizeChanged += MainWindow_SizeChanged;

            UpdateDocCountDisplay();
            UpdateHistoryCountDisplay();

            // 监听输入框运行状态变更，动静互斥展示并配置悬浮 ToolTip，防止重叠
            tbChatRunStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (s, e) =>
            {
                bool hasStatus = !string.IsNullOrWhiteSpace(tbChatRunStatus.Text);
                tbChatRunStatus.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
                ToolTipService.SetToolTip(tbChatRunStatus, hasStatus ? tbChatRunStatus.Text : null);
                if (tbKeyHint != null)
                {
                    tbKeyHint.Visibility = hasStatus ? Visibility.Collapsed : Visibility.Visible;
                }
            });
        }

        private void SetInitialWindowSize()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                appWindow?.Resize(new Windows.Graphics.SizeInt32(1280, 820));

                string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    appWindow?.SetIcon(iconPath);
                }
            }
            catch
            {
            }
        }

        private async Task LoadAppBrandIconAsync()
        {
            try
            {
                if (imgAboutLogo == null) return;

                string iconPng = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (!File.Exists(iconPng))
                {
                    iconPng = Path.Combine(AppContext.BaseDirectory, "Icon.png");
                }
                if (!File.Exists(iconPng))
                {
                    iconPng = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.png");
                }

                if (File.Exists(iconPng))
                {
                    try
                    {
                        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(iconPng);
                        using var stream = await storageFile.OpenAsync(Windows.Storage.FileAccessMode.Read);
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(stream);
                        imgAboutLogo.Source = bmp;
                        return;
                    }
                    catch
                    {
                        var fallbackBmp = new BitmapImage { UriSource = new Uri(iconPng) };
                        imgAboutLogo.Source = fallbackBmp;
                    }
                }
            }
            catch
            {
            }
        }

        private async Task LoadPersistedLibraryDataAsync()
        {
            try
            {
                var groups = await LibraryPersistenceService.Instance.ListAllGroupsAsync();
                DocumentGroups.Clear();
                foreach (var g in groups)
                {
                    DocumentGroups.Add(g);
                }

                var docs = await LibraryPersistenceService.Instance.ListAllDocumentsAsync();
                Documents.Clear();
                foreach (var d in docs)
                {
                    Documents.Add(d);
                }

                var sessions = await LibraryPersistenceService.Instance.ListAllSessionsAsync();
                ChatSessions.Clear();
                if (sessions.Count > 0)
                {
                    foreach (var s in sessions)
                    {
                        ChatSessions.Add(s);
                    }
                    _currentSession = ChatSessions.FirstOrDefault();
                    if (_currentSession != null)
                    {
                        _currentSession.IsSelected = true;
                        lvHistorySessions.SelectedItem = _currentSession;
                        if (!string.IsNullOrEmpty(_currentSession.BindGroupId))
                        {
                            _currentGroup = DocumentGroups.FirstOrDefault(g => g.GroupId == _currentSession.BindGroupId);
                            if (_currentGroup != null)
                            {
                                _currentGroup.IsSelected = true;
                                if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != _currentGroup)
                                {
                                    _isSelectingGroupProgrammatically = true;
                                    try
                                    {
                                        lvDocumentGroups.SelectedItem = _currentGroup;
                                    }
                                    finally
                                    {
                                        _isSelectingGroupProgrammatically = false;
                                    }
                                }
                                await LoadGroupDocumentsAsync(_currentGroup, selectAndPreviewFirstDoc: true);
                            }
                        }
                    }
                }
                else
                {
                    var defaultSession = new ChatSessionItem
                    {
                        Title = "默认数据分析会话",
                        CreatedAt = DateTime.Now,
                        LastActiveAt = DateTime.Now,
                        IsSelected = true
                    };
                    await LibraryPersistenceService.Instance.SaveSessionAsync(defaultSession);
                    ChatSessions.Add(defaultSession);
                    _currentSession = defaultSession;
                    lvHistorySessions.SelectedItem = defaultSession;
                }

                UpdateDocCountDisplay();
                UpdateHistoryCountDisplay();
                SyncGroupSessions();

                _currentDoc = Documents.FirstOrDefault();
                if (_currentDoc != null)
                {
                    lvDocuments.SelectedItem = _currentDoc;
                    DisplayDocument(_currentDoc);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载持久化数据失败: {ex.Message}");
            }
        }

        #region 界面导航与二级界面控制

        private bool IsDrawerOpen => borderDrawerPane.Visibility == Visibility.Visible;

        private void btnNavHome_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;
            CloseDrawer(highlightHome: false);
            btnTabDocPreview_Click(sender, e);
            HighlightNavButton(btnNavHome);
        }

        private void btnNavVis_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;
            CloseDrawer(highlightHome: false);
            btnTabVisualization_Click(sender, e);
            HighlightNavButton(btnNavVis);
        }

        private void btnNavDocLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (IsDrawerOpen)
            {
                CloseDrawer();
                return;
            }

            OpenDrawer(paneDocLibrary, btnNavDocLibrary);
        }

        private void OpenDrawer(FrameworkElement paneToShow, Button navBtn)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;

            paneDocLibrary.Visibility = Visibility.Visible;

            borderDrawerPane.Visibility = Visibility.Visible;
            drawerBackdrop.Visibility = Visibility.Visible;

            HighlightNavButton(navBtn);
        }

        private void CloseDrawer(bool highlightHome = true)
        {
            borderDrawerPane.Visibility = Visibility.Collapsed;
            drawerBackdrop.Visibility = Visibility.Collapsed;

            if (highlightHome)
            {
                HighlightNavButton(btnNavHome);
            }
        }

        private void btnNavSettings_Click(object sender, RoutedEventArgs e)
        {
            gridMainWorkspace.Visibility = Visibility.Collapsed;
            gridSettingsPage.Visibility = Visibility.Visible;

            CloseDrawer(highlightHome: false);

            LoadSettings();

            SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);

            HighlightNavButton(btnNavSettings);
        }

        private void HighlightNavButton(Button activeBtn)
        {
            SetNavButtonState(btnNavHome, activeBtn == btnNavHome, pathNavHome);
            SetNavButtonState(btnNavVis, activeBtn == btnNavVis, pathNavVisBack, pathNavVisFront, pathNavVisInner);
            SetNavShapeState(pathNavVisDot, activeBtn == btnNavVis);
            SetNavButtonState(btnNavDocLibrary, activeBtn == btnNavDocLibrary, pathNavDoc1, pathNavDoc2, pathNavDoc3);
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

        private void drawerBackdrop_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CloseDrawer();
        }

        private void btnBackToMain_Click(object sender, RoutedEventArgs e)
        {
            btnNavHome_Click(sender, e);
        }

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

        public void AddPlotToGallery(ChartArtifactItem plot, bool switchToVis = false)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string normPath;
                try
                {
                    normPath = Path.GetFullPath(plot.FilePath);
                    plot.FilePath = normPath;
                }
                catch
                {
                    normPath = plot.FilePath;
                }

                // 统一安全过滤：若图片位于 scratch 体系内，必须严格位于 agent_sandbox 目录下（带唯一编号），杜绝根目录无编号临时图入画廊
                string scratchDir = PythonExecutionService.LocateOrInitScratchDirectory(_currentGroup?.GroupId);
                string scratchPrefix = scratchDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (normPath.StartsWith(scratchPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !PythonExecutionService.IsInSandboxDirectory(normPath, _currentGroup?.GroupId))
                {
                    return;
                }

                string fileNameNoExt = Path.GetFileNameWithoutExtension(normPath);
                if (System.Text.RegularExpressions.Regex.IsMatch(fileNameNoExt, @"^chart_\d{10,}_\d+$"))
                {
                    return;
                }

                var existing = PlotGallery.FirstOrDefault(p =>
                {
                    try { return Path.GetFullPath(p.FilePath).Equals(normPath, StringComparison.OrdinalIgnoreCase); }
                    catch { return p.FilePath.Equals(normPath, StringComparison.OrdinalIgnoreCase); }
                });

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

        private void ClearPlotVisualizationDashboard()
        {
            PlotGallery.Clear();
            _activeDashboardPlot = null;
            if (imgActiveDashboardPlot != null)
            {
                imgActiveDashboardPlot.Source = null;
            }
            if (tbActivePlotHeader != null)
            {
                tbActivePlotHeader.Text = "数据可视化大屏展示";
            }
            if (tbActivePlotMeta != null)
            {
                tbActivePlotMeta.Text = "高清 PNG";
            }
            UpdateVisualizationDashboardUI();
        }

        private void UpdateVisualizationDashboardUI()
        {
            if (PlotGallery.Count == 0)
            {
                _activeDashboardPlot = null;
                if (imgActiveDashboardPlot != null) imgActiveDashboardPlot.Source = null;
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

        private void lvPlotGallery_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                var delta = e.GetCurrentPoint(lvPlotGallery).Properties.MouseWheelDelta;
                var scrollViewer = FindVisualChild<ScrollViewer>(lvPlotGallery);
                if (scrollViewer != null && delta != 0)
                {
                    double targetOffset = scrollViewer.HorizontalOffset - (delta * 0.8);
                    targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableWidth));
                    scrollViewer.ChangeView(targetOffset, null, null, false);
                    e.Handled = true;
                }
            }
            catch
            {
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;
                var foundDescendant = FindVisualChild<T>(child);
                if (foundDescendant != null)
                    return foundDescendant;
            }
            return null;
        }

        private void btnActivePlotZoom_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDashboardPlot != null)
            {
                ShowLargePlotViewer(_activeDashboardPlot);
            }
        }

        private void imgActiveDashboardPlot_PointerPressed(object sender, PointerRoutedEventArgs e)
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
                ShowGlobalToast("已复制到剪贴板");
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
                ShowGlobalToast("已复制到剪贴板");
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

            _isViewerFullscreen = true;
            _savedViewerWidth = 1050;
            _savedViewerHeight = 720;
            if (cardPlotViewerContainer != null)
            {
                cardPlotViewerContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
                cardPlotViewerContainer.VerticalAlignment = VerticalAlignment.Stretch;
                cardPlotViewerContainer.Width = double.NaN;
                cardPlotViewerContainer.Height = double.NaN;
                cardPlotViewerContainer.Margin = new Thickness(24, 20, 24, 20);
            }
            if (btnViewerToggleFullscreen != null)
            {
                btnViewerToggleFullscreen.Content = "还原";
            }
            if (borderViewerResizeGripLeft != null)
            {
                borderViewerResizeGripLeft.Visibility = Visibility.Collapsed;
            }
            if (borderViewerResizeGripRight != null)
            {
                borderViewerResizeGripRight.Visibility = Visibility.Collapsed;
            }

            if (gridPlotImageViewer != null) gridPlotImageViewer.Visibility = Visibility.Visible;

            DispatcherQueue.TryEnqueue(() =>
            {
                FitPlotImageToViewport(resetView: true);
            });
        }

        private void btnClosePlotViewer_Click(object sender, RoutedEventArgs e)
        {
            if (gridPlotImageViewer != null) gridPlotImageViewer.Visibility = Visibility.Collapsed;
        }

        #region 高清大图查看器交互控制 (自适应完全适屏显示、缩放、全屏切换、双侧跟手尺寸调节、自由平移与文件定位)

        private void FitPlotImageToViewport(bool resetView = true)
        {
            if (imgViewerLargePlot == null || svLargePlotViewer == null) return;

            double availWidth = svLargePlotViewer.ActualWidth;
            double availHeight = svLargePlotViewer.ActualHeight;
            if (availWidth <= 0 || availHeight <= 0) return;

            availWidth = Math.Max(100, availWidth - 28);
            availHeight = Math.Max(100, availHeight - 28);

            double naturalWidth = 0;
            double naturalHeight = 0;

            if (imgViewerLargePlot.Source is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
            {
                naturalWidth = bs.PixelWidth;
                naturalHeight = bs.PixelHeight;
            }
            else if (_viewingPlot?.Bitmap != null && _viewingPlot.Bitmap.PixelWidth > 0)
            {
                naturalWidth = _viewingPlot.Bitmap.PixelWidth;
                naturalHeight = _viewingPlot.Bitmap.PixelHeight;
            }

            if (naturalWidth > 0 && naturalHeight > 0)
            {
                double scaleW = availWidth / naturalWidth;
                double scaleH = availHeight / naturalHeight;
                double fitScale = Math.Min(scaleW, scaleH);

                imgViewerLargePlot.Width = Math.Round(naturalWidth * fitScale);
                imgViewerLargePlot.Height = Math.Round(naturalHeight * fitScale);
            }
            else
            {
                imgViewerLargePlot.Width = availWidth;
                imgViewerLargePlot.Height = availHeight;
            }

            if (resetView)
            {
                svLargePlotViewer.ChangeView(0, 0, 1.0f, disableAnimation: true);
                UpdateViewerZoomRatioText(1.0f);
            }
        }

        private void imgViewerLargePlot_ImageOpened(object sender, RoutedEventArgs e)
        {
            FitPlotImageToViewport(resetView: true);
        }

        private void svLargePlotViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (gridPlotImageViewer != null && gridPlotImageViewer.Visibility == Visibility.Visible)
            {
                if (svLargePlotViewer != null && Math.Abs(svLargePlotViewer.ZoomFactor - 1.0f) < 0.05f)
                {
                    FitPlotImageToViewport(resetView: false);
                }
            }
        }

        private void btnViewerOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_viewingPlot != null && !string.IsNullOrWhiteSpace(_viewingPlot.FilePath) && File.Exists(_viewingPlot.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_viewingPlot.FilePath}\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"打开资源管理器定位文件失败: {ex.Message}");
                }
            }
        }

        private void svLargePlotViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown && svLargePlotViewer != null)
            {
                e.Handled = true;
                var delta = e.GetCurrentPoint(svLargePlotViewer).Properties.MouseWheelDelta;
                if (delta != 0)
                {
                    float currentZoom = svLargePlotViewer.ZoomFactor;
                    float factor = delta > 0 ? 1.15f : 0.87f;
                    float targetZoom = Math.Clamp(currentZoom * factor, 0.2f, 5.0f);
                    svLargePlotViewer.ChangeView(null, null, targetZoom, disableAnimation: false);
                    UpdateViewerZoomRatioText(targetZoom);
                }
            }
        }

        private void svLargePlotViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (svLargePlotViewer != null)
            {
                UpdateViewerZoomRatioText(svLargePlotViewer.ZoomFactor);
            }
        }

        private void UpdateViewerZoomRatioText(float zoomFactor)
        {
            if (btnViewerZoomReset != null)
            {
                int percent = (int)Math.Round(zoomFactor * 100);
                btnViewerZoomReset.Content = $"{percent}%";
            }
        }

        private void btnViewerZoomReset_Click(object sender, RoutedEventArgs e)
        {
            FitPlotImageToViewport(resetView: true);
        }

        private void btnViewerToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (cardPlotViewerContainer == null) return;

            _isViewerFullscreen = !_isViewerFullscreen;
            if (_isViewerFullscreen)
            {
                if (!double.IsNaN(cardPlotViewerContainer.Width)) _savedViewerWidth = cardPlotViewerContainer.Width;
                if (!double.IsNaN(cardPlotViewerContainer.Height)) _savedViewerHeight = cardPlotViewerContainer.Height;

                cardPlotViewerContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
                cardPlotViewerContainer.VerticalAlignment = VerticalAlignment.Stretch;
                cardPlotViewerContainer.Width = double.NaN;
                cardPlotViewerContainer.Height = double.NaN;
                cardPlotViewerContainer.Margin = new Thickness(24, 20, 24, 20);

                if (btnViewerToggleFullscreen != null) btnViewerToggleFullscreen.Content = "还原";
                if (borderViewerResizeGripLeft != null) borderViewerResizeGripLeft.Visibility = Visibility.Collapsed;
                if (borderViewerResizeGripRight != null) borderViewerResizeGripRight.Visibility = Visibility.Collapsed;
            }
            else
            {
                cardPlotViewerContainer.HorizontalAlignment = HorizontalAlignment.Center;
                cardPlotViewerContainer.VerticalAlignment = VerticalAlignment.Center;
                cardPlotViewerContainer.Width = _savedViewerWidth;
                cardPlotViewerContainer.Height = _savedViewerHeight;
                cardPlotViewerContainer.Margin = new Thickness(24, 20, 24, 20);

                if (btnViewerToggleFullscreen != null) btnViewerToggleFullscreen.Content = "全屏";
                if (borderViewerResizeGripLeft != null) borderViewerResizeGripLeft.Visibility = Visibility.Visible;
                if (borderViewerResizeGripRight != null) borderViewerResizeGripRight.Visibility = Visibility.Visible;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                FitPlotImageToViewport(resetView: true);
            });
        }

        #region 双侧透明边角热区与 1:1 绝对中心跟手拉伸 (系统原生双向箭头光标，如实拍照)

        private void borderViewerResizeGripRight_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isViewerFullscreen) return;
            if (sender is UIElement elem && e.GetCurrentPoint(elem).Properties.IsLeftButtonPressed)
            {
                _isResizingViewerRight = true;
                elem.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void borderViewerResizeGripRight_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizingViewerRight || cardPlotViewerContainer == null || gridPlotImageViewer == null) return;

            double centerX = gridPlotImageViewer.ActualWidth / 2.0;
            double centerY = gridPlotImageViewer.ActualHeight / 2.0;
            var mousePos = e.GetCurrentPoint(gridPlotImageViewer).Position;

            double targetWidth = 2.0 * Math.Max(0, mousePos.X - centerX);
            double targetHeight = 2.0 * Math.Max(0, mousePos.Y - centerY);

            double maxWidth = Math.Max(cardPlotViewerContainer.MinWidth, gridPlotImageViewer.ActualWidth - 48);
            double maxHeight = Math.Max(cardPlotViewerContainer.MinHeight, gridPlotImageViewer.ActualHeight - 48);

            cardPlotViewerContainer.Width = Math.Clamp(targetWidth, cardPlotViewerContainer.MinWidth, maxWidth);
            cardPlotViewerContainer.Height = Math.Clamp(targetHeight, cardPlotViewerContainer.MinHeight, maxHeight);
            _savedViewerWidth = cardPlotViewerContainer.Width;
            _savedViewerHeight = cardPlotViewerContainer.Height;

            if (svLargePlotViewer != null && Math.Abs(svLargePlotViewer.ZoomFactor - 1.0f) < 0.05f)
            {
                FitPlotImageToViewport(resetView: false);
            }

            e.Handled = true;
        }

        private void borderViewerResizeGripRight_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isResizingViewerRight)
            {
                _isResizingViewerRight = false;
                (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private void borderViewerResizeGripLeft_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isViewerFullscreen) return;
            if (sender is UIElement elem && e.GetCurrentPoint(elem).Properties.IsLeftButtonPressed)
            {
                _isResizingViewerLeft = true;
                elem.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void borderViewerResizeGripLeft_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizingViewerLeft || cardPlotViewerContainer == null || gridPlotImageViewer == null) return;

            double centerX = gridPlotImageViewer.ActualWidth / 2.0;
            double centerY = gridPlotImageViewer.ActualHeight / 2.0;
            var mousePos = e.GetCurrentPoint(gridPlotImageViewer).Position;

            double targetWidth = 2.0 * Math.Max(0, centerX - mousePos.X);
            double targetHeight = 2.0 * Math.Max(0, mousePos.Y - centerY);

            double maxWidth = Math.Max(cardPlotViewerContainer.MinWidth, gridPlotImageViewer.ActualWidth - 48);
            double maxHeight = Math.Max(cardPlotViewerContainer.MinHeight, gridPlotImageViewer.ActualHeight - 48);

            cardPlotViewerContainer.Width = Math.Clamp(targetWidth, cardPlotViewerContainer.MinWidth, maxWidth);
            cardPlotViewerContainer.Height = Math.Clamp(targetHeight, cardPlotViewerContainer.MinHeight, maxHeight);
            _savedViewerWidth = cardPlotViewerContainer.Width;
            _savedViewerHeight = cardPlotViewerContainer.Height;

            if (svLargePlotViewer != null && Math.Abs(svLargePlotViewer.ZoomFactor - 1.0f) < 0.05f)
            {
                FitPlotImageToViewport(resetView: false);
            }

            e.Handled = true;
        }

        private void borderViewerResizeGripLeft_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isResizingViewerLeft)
            {
                _isResizingViewerLeft = false;
                (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        #endregion

        #region 鼠标左键按压自由拖动图表平移 (Pan)

        private void gridViewerImageCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(gridViewerImageCanvas).Properties.IsLeftButtonPressed)
            {
                _isPanningViewer = true;
                gridViewerImageCanvas.CapturePointer(e.Pointer);
                _lastPanPointerPos = e.GetCurrentPoint(svLargePlotViewer).Position;
                e.Handled = true;
            }
        }

        private void gridViewerImageCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isPanningViewer && svLargePlotViewer != null)
            {
                var currentPos = e.GetCurrentPoint(svLargePlotViewer).Position;
                double deltaX = currentPos.X - _lastPanPointerPos.X;
                double deltaY = currentPos.Y - _lastPanPointerPos.Y;
                _lastPanPointerPos = currentPos;

                svLargePlotViewer.ChangeView(svLargePlotViewer.HorizontalOffset - deltaX, svLargePlotViewer.VerticalOffset - deltaY, null, disableAnimation: true);
                e.Handled = true;
            }
        }

        private void gridViewerImageCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isPanningViewer)
            {
                _isPanningViewer = false;
                gridViewerImageCanvas?.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        #endregion

        #endregion

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
            try
            {
                if (_activeDashboardPlot != null && !string.IsNullOrWhiteSpace(_activeDashboardPlot.FilePath) && File.Exists(_activeDashboardPlot.FilePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_activeDashboardPlot.FilePath}\"") { UseShellExecute = true });
                    return;
                }

                var firstExistingPlot = PlotGallery.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.FilePath) && File.Exists(p.FilePath));
                if (firstExistingPlot != null)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{firstExistingPlot.FilePath}\"") { UseShellExecute = true });
                    return;
                }

                string scratchDir = PythonExecutionService.LocateOrInitScratchDirectory(_currentGroup?.GroupId);
                if (Directory.Exists(scratchDir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{scratchDir}\"") { UseShellExecute = true });
                }
            }
            catch
            {
            }
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
                var result = await _pythonExecService.ExecuteCodeAsync(
                    pythonCode,
                    _settings.PythonPath,
                    options: new PythonExecutionOptions { TimeoutSeconds = Math.Clamp(_settings.PythonTimeoutSeconds, 30, 120) });
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

        #endregion

        #region 二级界面：设置目录与视图管理 (LLM配置 / 主题 / Skill / 关于我们)

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
            viewSettingsTheme.Visibility = Visibility.Collapsed;
            viewSettingsSkill.Visibility = Visibility.Collapsed;
            viewSettingsAbout.Visibility = Visibility.Collapsed;

            activeView.Visibility = Visibility.Visible;
            HighlightSettingsNav(activeBtn, activeText);

            if (activeView == viewSettingsAbout)
            {
                _ = LoadAppBrandIconAsync();
            }
        }

        private void HighlightSettingsNav(Button activeBtn, TextBlock activeText)
        {
            btnSettingsNavLlm.Background = BrushTransparent;
            btnSettingsNavTheme.Background = BrushTransparent;
            btnSettingsNavSkill.Background = BrushTransparent;
            btnSettingsNavAbout.Background = BrushTransparent;

            tbSettingsNavLlmText.Foreground = BrushTextSecondary;
            tbSettingsNavLlmText.FontWeight = FontWeights.Normal;
            tbSettingsNavThemeText.Foreground = BrushTextSecondary;
            tbSettingsNavThemeText.FontWeight = FontWeights.Normal;
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
                btnSettingsNavTheme.Visibility = Visibility.Visible;
                btnSettingsNavSkill.Visibility = Visibility.Visible;
                btnSettingsNavAbout.Visibility = Visibility.Visible;
                return;
            }

            btnSettingsNavLlm.Visibility = (keyword.Contains("llm") || "llm配置".Contains(keyword) || "模型".Contains(keyword) || "key".Contains(keyword) || "api".Contains(keyword) || "接口".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;
            btnSettingsNavTheme.Visibility = (keyword.Contains("主题") || keyword.Contains("语言") || keyword.Contains("配色") || keyword.Contains("提示词") || keyword.Contains("prompt") || keyword.Contains("颜色") || keyword.Contains("theme") || keyword.Contains("english") || keyword.Contains("中文") || keyword.Contains("人设") || "主题".Contains(keyword) || "语言".Contains(keyword) || "配色".Contains(keyword) || "系统角色提示词".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;
            btnSettingsNavSkill.Visibility = (keyword.Contains("skill") || "技能".Contains(keyword) || "图表".Contains(keyword) || "总结".Contains(keyword) || "分析".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;
            btnSettingsNavAbout.Visibility = (keyword.Contains("关于") || "关于我们".Contains(keyword) || "版本".Contains(keyword) || "团队".Contains(keyword) || "更新".Contains(keyword)) ? Visibility.Visible : Visibility.Collapsed;

            bool currentNavHidden =
                (viewSettingsLlm.Visibility == Visibility.Visible && btnSettingsNavLlm.Visibility == Visibility.Collapsed) ||
                (viewSettingsTheme.Visibility == Visibility.Visible && btnSettingsNavTheme.Visibility == Visibility.Collapsed) ||
                (viewSettingsSkill.Visibility == Visibility.Visible && btnSettingsNavSkill.Visibility == Visibility.Collapsed) ||
                (viewSettingsAbout.Visibility == Visibility.Visible && btnSettingsNavAbout.Visibility == Visibility.Collapsed);

            if (currentNavHidden)
            {
                if (btnSettingsNavLlm.Visibility == Visibility.Visible)
                {
                    SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);
                }
                else if (btnSettingsNavTheme.Visibility == Visibility.Visible)
                {
                    SwitchSettingsView(viewSettingsTheme, btnSettingsNavTheme, tbSettingsNavThemeText);
                    RenderThemeSectionCards();
                }
                else if (btnSettingsNavSkill.Visibility == Visibility.Visible)
                {
                    SwitchSettingsView(viewSettingsSkill, btnSettingsNavSkill, tbSettingsNavSkillText);
                    _ = LoadInstalledSkillsAsync();
                }
                else if (btnSettingsNavAbout.Visibility == Visibility.Visible)
                {
                    SwitchSettingsView(viewSettingsAbout, btnSettingsNavAbout, tbSettingsNavAboutText);
                }
            }
        }

        #region 二级界面：关于我们操作

        private async void btnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await ShowMessageDialogAsync(
                "检查更新",
                "您当前使用的是正式发布版：v1.4.0 (构建版本 2026.10)\n\n" +
                "当前版本已是最新正式版本，多文档组协同管理、实时流式分析、浮动 Toast 通知与沙盒执行功能均已完整就绪，无需更新。");
        }

        private async void btnCopyDiagnosticInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== DataAnalysis Assistant 客户端诊断信息 ===");
                sb.AppendLine($"软件版本: v1.4.0 (2026.10 Release)");
                sb.AppendLine($"运行时: .NET {Environment.Version}");
                sb.AppendLine($"操作系统: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
                sb.AppendLine($"执行架构: {(Environment.Is64BitProcess ? "x64 原生进程" : "x86")}");
                sb.AppendLine($"机器名称: {Environment.MachineName}");
                sb.AppendLine($"持久化库基目录: {LibraryPersistenceService.Instance.DataDirectory}");
                sb.AppendLine($"当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(sb.ToString());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                await ShowMessageDialogAsync("诊断信息已复制", "已成功将系统与客户端诊断参数复制到系统剪贴板，可直接粘贴提供给技术支持人员。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"btnCopyDiagnosticInfo_Click 异常: {ex.Message}");
            }
        }

        #endregion

        #endregion

        #region 二级界面：LLM 配置管理

        private void LoadSettings()
        {
            _settings = SettingsService.LoadSettings();

            pbApiKey.Password = _settings.ApiKey;
            tbBaseUrl.Text = _settings.BaseUrl;
            sliderContextLength.Value = Math.Clamp(_settings.MaxContextLength, 1024, 8192);
            sliderTemperature.Value = _settings.Temperature;
            tbSystemPrompt.Text = _settings.SystemPrompt;

            if (sliderMaxIterations != null)
            {
                sliderMaxIterations.Value = Math.Clamp(_settings.AgentMaxIterations > 0 ? _settings.AgentMaxIterations : 12, 6, 20);
            }
            if (toggleAutoFinalSynthesis != null)
            {
                toggleAutoFinalSynthesis.IsOn = _settings.EnableAutoFinalSynthesis;
            }
            if (sliderPythonTimeout != null)
            {
                sliderPythonTimeout.Value = Math.Clamp(_settings.PythonTimeoutSeconds > 0 ? _settings.PythonTimeoutSeconds : 60, 30, 120);
            }

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

            UpdateSettingsDisplay();
            RenderThemeSectionCards();
            _ = CheckPythonEnvironmentAsync();
            _ = LoadInstalledSkillsAsync();
        }

        private void UpdateSettingsDisplay()
        {
            tbContextLengthValue.Text = $"{_settings.MaxContextLength:N0} tokens";
            tbTemperatureValue.Text = _settings.Temperature.ToString("F1");
            if (tbMaxIterationsValue != null && sliderMaxIterations != null)
            {
                tbMaxIterationsValue.Text = $"{(int)sliderMaxIterations.Value} 轮";
            }
            if (tbPythonTimeoutValue != null && sliderPythonTimeout != null)
            {
                tbPythonTimeoutValue.Text = $"{(int)sliderPythonTimeout.Value} 秒";
            }
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
                string endpoint = baseUrl.TrimEnd('/');
                if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = endpoint.Substring(0, endpoint.Length - "/chat/completions".Length);
                }
                if (!endpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint += "/models";
                }

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

            if (tbCustomPythonPath != null)
            {
                _settings.PythonPath = tbCustomPythonPath.Text.Trim();
            }
            if (comboPythonSecurityMode != null && comboPythonSecurityMode.SelectedIndex >= 0)
            {
                _settings.PythonSecurityMode = comboPythonSecurityMode.SelectedIndex;
            }

            if (sliderMaxIterations != null)
            {
                _settings.AgentMaxIterations = (int)sliderMaxIterations.Value;
            }
            if (toggleAutoFinalSynthesis != null)
            {
                _settings.EnableAutoFinalSynthesis = toggleAutoFinalSynthesis.IsOn;
            }
            if (sliderPythonTimeout != null)
            {
                _settings.PythonTimeoutSeconds = Math.Clamp((int)sliderPythonTimeout.Value, 30, 120);
            }

            SettingsService.SaveSettings(_settings);
            UpdateSettingsDisplay();

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
            infoBarSettings.Message = $"配置已成功保存！当前模型: {_settings.ModelName}，Agent 决策上限: {_settings.AgentMaxIterations} 轮，Python 超时: {_settings.PythonTimeoutSeconds} 秒。";
        }

        private void btnResetSettingsPage_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettings();
            _settings.BaseUrl = defaults.BaseUrl;
            _settings.ModelName = defaults.ModelName;
            _settings.MaxContextLength = defaults.MaxContextLength;
            _settings.Temperature = defaults.Temperature;
            _settings.AgentMaxIterations = defaults.AgentMaxIterations;
            _settings.EnableAutoFinalSynthesis = defaults.EnableAutoFinalSynthesis;

            tbBaseUrl.Text = _settings.BaseUrl;
            sliderContextLength.Value = _settings.MaxContextLength;
            sliderTemperature.Value = _settings.Temperature;
            cbModelName.SelectedIndex = 0;
            if (cbQuickModel != null && cbQuickModel.Items.Count > 0) cbQuickModel.SelectedIndex = 0;
            if (sliderMaxIterations != null) sliderMaxIterations.Value = _settings.AgentMaxIterations;
            if (toggleAutoFinalSynthesis != null) toggleAutoFinalSynthesis.IsOn = _settings.EnableAutoFinalSynthesis;

            SettingsService.SaveSettings(_settings);
            UpdateSettingsDisplay();

            infoBarSettings.IsOpen = true;
            infoBarSettings.Severity = InfoBarSeverity.Informational;
            infoBarSettings.Title = "已恢复默认";
            infoBarSettings.Message = "LLM 连接与调度参数已重置为标准默认值（已保留 API Key 与主题/自定义配置）。";
        }

        #region 标准 Skill 规范与扩展管理

        public async Task LoadInstalledSkillsAsync(bool showFeedback = false)
        {
            try
            {
                var skills = await _skillManager.LoadUserInstalledSkillsAsync(_settings);
                InstalledSkills.Clear();
                FilteredSkills.Clear();
                foreach (var s in skills)
                {
                    InstalledSkills.Add(s);
                    if (s.IsEnabled)
                    {
                        FilteredSkills.Add(s);
                    }
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

        private void sliderMaxIterations_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (tbMaxIterationsValue != null)
            {
                tbMaxIterationsValue.Text = $"{(int)e.NewValue} 轮";
            }
        }

        private void sliderPythonTimeout_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (tbPythonTimeoutValue != null)
            {
                tbPythonTimeoutValue.Text = $"{(int)e.NewValue} 秒";
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

                OfficeOpenXml.ExcelPackage.License.SetNonCommercialPersonal("DataAnalysisAssistant");

                var importedDocs = new List<DocumentItem>();
                foreach (StorageFile file in files)
                {
                    // 避免重复解析同路径文件
                    var existing = Documents.FirstOrDefault(d => d.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        importedDocs.Add(existing);
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

                    try
                    {
                        if (item.IsTabularData)
                        {
                            var profile = await _schemaProfiler.ProfileAsync(file.Path);
                            item.SchemaProfile = profile;
                            item.ExtractedText = profile.ToMarkdown();
                            item.Status = $"{profile.TotalRows:N0}行·骨架已解析";
                        }
                        else if (ext == ".txt" || ext == ".md")
                        {
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
                            using var stream = await file.OpenStreamForReadAsync();
                            using var doc = Xceed.Words.NET.DocX.Load(stream);

                            item.ExtractedText = doc.Text;
                            item.Status = "已提取文本 (支持精准调取)";
                        }
                        else
                        {
                            item.ExtractedText = $"格式提示：当前文档为较早版本二进制格式 ({ext})，建议将其转换为 .docx 或 .xlsx 后解析。";
                            item.Status = "待格式转换";
                        }
                    }
                    catch (Exception parseEx)
                    {
                        item.Status = "解析失败";
                        item.ExtractedText = $"文档解析失败：{parseEx.Message}";
                    }

                    await LibraryPersistenceService.Instance.SaveDocumentMetaAsync(item);
                    importedDocs.Add(item);
                }

                if (importedDocs.Count == 0) return;

                string groupName;
                if (importedDocs.Count == 1)
                {
                    groupName = Path.GetFileNameWithoutExtension(importedDocs[0].Name);
                }
                else
                {
                    string firstDocName = Path.GetFileNameWithoutExtension(importedDocs[0].Name);
                    groupName = $"{firstDocName} 等{importedDocs.Count}个文件";
                }

                var docIds = importedDocs.Select(d => d.DocId).ToList();
                var group = await LibraryPersistenceService.Instance.CreateGroupAsync(groupName, docIds);
                DocumentGroups.Insert(0, group);
                _currentGroup = group;
                foreach (var g in DocumentGroups)
                {
                    g.IsSelected = (g == group);
                }
                if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
                {
                    _isSelectingGroupProgrammatically = true;
                    try
                    {
                        lvDocumentGroups.SelectedItem = group;
                    }
                    finally
                    {
                        _isSelectingGroupProgrammatically = false;
                    }
                }

                await LoadGroupDocumentsAsync(group, selectAndPreviewFirstDoc: true);

                var newSession = new ChatSessionItem
                {
                    Title = group.GroupName,
                    CreatedAt = DateTime.Now,
                    LastActiveAt = DateTime.Now,
                    BindGroupId = group.GroupId,
                    BindGroupName = group.GroupName,
                    IsSelected = true
                };

                foreach (var s in ChatSessions)
                {
                    s.IsSelected = false;
                }

                ChatSessions.Insert(0, newSession);
                _currentSession = newSession;
                lvHistorySessions.SelectedItem = newSession;
                await LibraryPersistenceService.Instance.SaveSessionAsync(newSession);
                UpdateHistoryCountDisplay();
                SyncGroupSessions();

                ChatMessages.Clear();
                tbChatRunStatus.Text = "";

                _currentDoc = Documents.FirstOrDefault();
                if (_currentDoc != null)
                {
                    lvDocuments.SelectedItem = _currentDoc;
                    DisplayDocument(_currentDoc);
                }

                btnTabDocPreview_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"选择文件失败: {ex.Message}");
            }
        }

        private async void btnRemoveDoc_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DocumentItem doc)
            {
                Documents.Remove(doc);

                if (_currentGroup != null && _currentGroup.DocumentIds.Contains(doc.DocId))
                {
                    await LibraryPersistenceService.Instance.RemoveDocumentFromGroupAsync(_currentGroup.GroupId, doc.DocId);
                    _currentGroup.LastActiveAt = DateTime.Now;
                }

                UpdateDocCountDisplay();

                if (_currentDoc == doc)
                {
                    _currentDoc = Documents.FirstOrDefault();
                    lvDocuments.SelectedItem = _currentDoc;
                    DisplayDocument(_currentDoc);
                }
            }
        }

        private async void btnRelocateDoc_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DocumentItem doc)
            {
                try
                {
                    var picker = new FileOpenPicker
                    {
                        ViewMode = PickerViewMode.List,
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                    };
                    if (!string.IsNullOrEmpty(doc.Extension))
                    {
                        picker.FileTypeFilter.Add(doc.Extension.ToLowerInvariant());
                    }
                    picker.FileTypeFilter.Add("*");

                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        doc.Path = file.Path;
                        doc.Name = file.Name;
                        LibraryPersistenceService.Instance.CheckDocumentFileExists(doc);
                        await LibraryPersistenceService.Instance.SaveDocumentMetaAsync(doc);

                        if (_currentDoc == doc)
                        {
                            DisplayDocument(doc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"重新定位文件失败: {ex.Message}");
                }
            }
        }

        private async void btnExportBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = $"DataAnalysis_Library_Backup_{DateTime.Now:yyyyMMdd_HHmm}"
                };
                savePicker.FileTypeChoices.Add("数据分析库备份包 (*.zip)", new List<string> { ".zip" });
                savePicker.DefaultFileExtension = ".zip";

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                var targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile != null)
                {
                    bool ok = await LibraryPersistenceService.Instance.ExportBackupAsync(targetFile.Path);
                    if (ok)
                    {
                        await ShowMessageDialogAsync("备份导出成功", $"已成功将文档元数据、文档组、历史会话与 Schema 缓存打包归档至：\n{targetFile.Path}");
                    }
                    else
                    {
                        await ShowMessageDialogAsync("导出失败", "导出备份压缩包过程中发生异常，请检查磁盘空间及写入权限。");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"btnExportBackup_Click 异常: {ex.Message}");
                await ShowMessageDialogAsync("导出异常", $"导出失败: {ex.Message}");
            }
        }

        private async void btnImportBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openPicker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                openPicker.FileTypeFilter.Add(".zip");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);

                var file = await openPicker.PickSingleFileAsync();
                if (file != null)
                {
                    int restoredCount = await LibraryPersistenceService.Instance.ImportBackupAsync(file.Path);
                    if (restoredCount > 0)
                    {
                        await LoadPersistedLibraryDataAsync();
                        await ShowMessageDialogAsync("备份恢复成功", $"已成功从备份包中导入并合并 {restoredCount} 个持久化条目，文档库与历史会话已同步更新。");
                    }
                    else
                    {
                        await ShowMessageDialogAsync("恢复未生效", "未能从该压缩包中读取到有效的数据分析库条目，请确认所选文件是否为有效的备份包。");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"btnImportBackup_Click 异常: {ex.Message}");
                await ShowMessageDialogAsync("恢复异常", $"导入备份失败: {ex.Message}");
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

            tbPreviewContent.Text = doc.ExtractedText;
            bool hasText = !string.IsNullOrWhiteSpace(doc.ExtractedText);
            tbPreviewContent.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            spEmptyPreview.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateDocCountDisplay()
        {
            int groupCount = DocumentGroups.Count;
            int docCount = Documents.Count;
            tbDocCount.Text = groupCount > 0 ? $"{groupCount} 个组 · {docCount} 个文件" : $"{docCount} 个文件";
            tbRailDocBadge.Text = (groupCount > 0 ? groupCount : docCount).ToString();
            railDocBadge.Visibility = (groupCount > 0 || docCount > 0) ? Visibility.Visible : Visibility.Collapsed;
            spEmptyDocs.Visibility = docCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (spEmptyGroups != null)
            {
                spEmptyGroups.Visibility = groupCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
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
                ShowGlobalToast("已复制到剪贴板");
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

        private void btnToggleReasoning_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem item)
            {
                item.IsReasoningExpanded = !item.IsReasoningExpanded;
            }
        }

        #region 对话框 Slash Command (/) 技能浮层与交互导航

        private void tbQuestion_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingQuestionTextProgrammatically) return;

            string text = tbQuestion.Text;

            if (!string.IsNullOrEmpty(text) && text.StartsWith("/"))
            {
                string query = text.Length > 1 ? text.Substring(1).Trim() : string.Empty;

                if (InstalledSkills.Count == 0)
                {
                    _ = LoadInstalledSkillsAsync().ContinueWith(_ =>
                    {
                        DispatcherQueue.TryEnqueue(() => FilterSkillsAndShowPopup(query));
                    });
                    return;
                }

                FilterSkillsAndShowPopup(query);
            }
            else
            {
                HideSkillSelectorPopup();
            }
        }

        private void FilterSkillsAndShowPopup(string query)
        {
            FilteredSkills.Clear();
            var matched = InstalledSkills.Where(s => s.IsEnabled &&
                (string.IsNullOrWhiteSpace(query) ||
                 s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

            foreach (var item in matched)
            {
                FilteredSkills.Add(item);
            }

            if (FilteredSkills.Count > 0)
            {
                borderSkillSelectorPopup.Visibility = Visibility.Visible;
                if (lvSkillSelector.SelectedIndex < 0 || lvSkillSelector.SelectedIndex >= FilteredSkills.Count)
                {
                    lvSkillSelector.SelectedIndex = 0;
                }
            }
            else
            {
                HideSkillSelectorPopup();
            }
        }

        private void HideSkillSelectorPopup()
        {
            if (borderSkillSelectorPopup != null)
            {
                borderSkillSelectorPopup.Visibility = Visibility.Collapsed;
            }
        }

        private void lvSkillSelector_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SkillItem skill)
            {
                SelectSkill(skill);
            }
        }

        private void SelectSkill(SkillItem skill)
        {
            if (skill == null) return;

            HideSkillSelectorPopup();

            _activeMountedSkill = skill;

            _isUpdatingQuestionTextProgrammatically = true;
            try
            {
                tbQuestion.Text = string.Empty;
            }
            finally
            {
                _isUpdatingQuestionTextProgrammatically = false;
            }

            if (borderActiveSkillPill != null && tbActiveSkillPillName != null)
            {
                tbActiveSkillPillName.Text = skill.Name;
                ToolTipService.SetToolTip(borderActiveSkillPill, string.IsNullOrWhiteSpace(skill.Description) ? skill.Name : $"{skill.Name}\n{skill.Description}");
                borderActiveSkillPill.Visibility = Visibility.Visible;
            }

            if (tbQuestion != null)
            {
                tbQuestion.PlaceholderText = $"已挂载技能 [{skill.Name}]，继续输入你的分析需求或数据问题 (Enter 发送)...";
                tbQuestion.Height = 52;
                tbQuestion.Focus(FocusState.Programmatic);
            }
        }

        private void btnRemoveActiveSkill_Click(object sender, RoutedEventArgs e)
        {
            RemoveActiveSkill();
        }

        private void RemoveActiveSkill()
        {
            _activeMountedSkill = null;
            if (borderActiveSkillPill != null)
            {
                borderActiveSkillPill.Visibility = Visibility.Collapsed;
            }

            if (tbQuestion != null)
            {
                tbQuestion.PlaceholderText = "输入你的数据分析或问答需求 (输入 / 触发技能扩展，Enter 发送)...";
                tbQuestion.Height = 70;
                tbQuestion.Focus(FocusState.Programmatic);
            }
        }

        #endregion

        private void tbQuestion_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (borderSkillSelectorPopup != null && borderSkillSelectorPopup.Visibility == Visibility.Visible)
            {
                if (e.Key == Windows.System.VirtualKey.Down)
                {
                    e.Handled = true;
                    if (FilteredSkills.Count > 0)
                    {
                        int next = (lvSkillSelector.SelectedIndex + 1) % FilteredSkills.Count;
                        lvSkillSelector.SelectedIndex = next;
                        lvSkillSelector.ScrollIntoView(FilteredSkills[next]);
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.Up)
                {
                    e.Handled = true;
                    if (FilteredSkills.Count > 0)
                    {
                        int prev = lvSkillSelector.SelectedIndex - 1;
                        if (prev < 0) prev = FilteredSkills.Count - 1;
                        lvSkillSelector.SelectedIndex = prev;
                        lvSkillSelector.ScrollIntoView(FilteredSkills[prev]);
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Tab)
                {
                    e.Handled = true;
                    if (lvSkillSelector.SelectedItem is SkillItem selectedSkill)
                    {
                        SelectSkill(selectedSkill);
                    }
                    else if (FilteredSkills.Count > 0)
                    {
                        SelectSkill(FilteredSkills[0]);
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    HideSkillSelectorPopup();
                    return;
                }
            }

            if (e.Key == Windows.System.VirtualKey.Back)
            {
                if (string.IsNullOrEmpty(tbQuestion.Text) && _activeMountedSkill != null)
                {
                    e.Handled = true;
                    RemoveActiveSkill();
                    return;
                }
            }

            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

                if (!isShiftPressed)
                {
                    e.Handled = true;

                    if (_isChatGenerating)
                    {
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

            string currentAgentTaskId = Guid.NewGuid().ToString("N");
            int currentTaskSeq = 1;
            if (ChatMessages.Count > 0)
            {
                currentTaskSeq = ChatMessages.Max(m => m.TaskSequenceNumber) + 1;
            }

            var currentSkill = _activeMountedSkill;
            RemoveActiveSkill();

            var userMessage = new ChatMessageItem
            {
                Sender = "User",
                Content = question,
                Timestamp = DateTime.Now,
                AgentTaskId = currentAgentTaskId,
                TaskSequenceNumber = currentTaskSeq,
                ActiveSkillName = currentSkill?.Name
            };
            ChatMessages.Add(userMessage);

            tbQuestion.Text = string.Empty;

            string? activeSkillInstruction = null;
            if (currentSkill != null)
            {
                try
                {
                    string? skillFullMarkdown = await SkillManagerService.GetSkillFullInstructionsAsync(currentSkill);
                    if (!string.IsNullOrWhiteSpace(skillFullMarkdown))
                    {
                        var sbSkill = new StringBuilder();
                        sbSkill.AppendLine($"【已启用专属专业扩展技能 (Specialized Skill: {currentSkill.Name})】");
                        sbSkill.AppendLine("在执行本次数据分析与推演任务时，请务必严格遵循以下技能指南的专业角色、工作流程与输出规范：");
                        sbSkill.AppendLine("【系统受控沙盒环境边界约束 (最高执行红线)】：");
                        sbSkill.AppendLine("1. 当前任务运行在本地独立数据分析沙盒中。技能指南中引用的内部配置文件与脚本（如 manifest.yaml、static/*、scripts/* 等）属于技能系统元架构，在当前沙盒执行环境中【物理不可达】；");
                        sbSkill.AppendLine("2. 严禁调用 read_document 或编写 Python 代码尝试定位、搜索、遍历或读取这些技能内部文件与脚本！若尝试读取必然报错并浪费轮次；");
                        sbSkill.AppendLine("3. 请直接吸收并贯彻技能指南中的专业设计哲学、配色方案（克制专业）、图表排版契约与分析框架，针对当前用户上传的真实业务数据编写本地 Python 分析脚本；");
                        sbSkill.AppendLine("==================== [SKILL GUIDE START] ====================");
                        sbSkill.AppendLine(skillFullMarkdown.Trim());
                        sbSkill.AppendLine("==================== [SKILL GUIDE END] ======================");
                        activeSkillInstruction = sbSkill.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SkillInstruction] 提取失败: {ex.Message}");
                }
            }

            // 关键节点 A: 用户提问即刻写盘（防止未完成推理时退出丢失提问）
            _ = PersistCurrentSessionAsync(force: true);

            _isChatGenerating = true;
            _chatCts = new CancellationTokenSource();
            var cancellationToken = _chatCts.Token;

            btnSend.Content = "停止";
            btnSend.Background = BrushStopAction;
            tbQuestion.IsEnabled = false;
            tbChatRunStatus.Text = "正在深度思考...";

            var assistantMessage = new ChatMessageItem
            {
                Sender = "Assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                IsThinking = true,
                IsReasoningExpanded = true,
                ThinkingSeconds = 0,
                Timestamp = DateTime.Now,
                AgentTaskId = currentAgentTaskId,
                TaskSequenceNumber = currentTaskSeq
            };
            ChatMessages.Add(assistantMessage);

            svChatList.ChangeView(null, svChatList.ScrollableHeight, null);

            var currentSettings = _settings;

            if (cbQuickModel?.SelectedItem is ComboBoxItem selectedComboItem &&
                !string.IsNullOrWhiteSpace(selectedComboItem.Content?.ToString()))
            {
                currentSettings.ModelName = selectedComboItem.Content.ToString()!;
            }

            string documentContext;
            if (_currentDoc != null)
            {
                documentContext = _currentDoc.ExtractedText ?? string.Empty;
                if (Documents.Count > 1)
                {
                    var sbDocs = new StringBuilder();
                    sbDocs.AppendLine(documentContext);
                    sbDocs.AppendLine("\n#### 工作区其他并存关联文档（支持跨文档联合分析）：");
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
                var sbMulti = new StringBuilder();
                sbMulti.AppendLine("### 当前工作区已载入的多文档清单 (可调用 read_document 工具精准调取)：");
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

            var historyTurns = new List<ChatTurn>();
            foreach (var msg in ChatMessages)
            {
                if (msg.Content?.Contains("欢迎使用 AI 数据分析与文档伴读看板") == true)
                {
                    continue;
                }

                if (msg == assistantMessage)
                {
                    continue;
                }

                if (msg.IsUser)
                {
                    if (ChatMessages.Count >= 2 && msg == ChatMessages[^2])
                    {
                        continue;
                    }

                    historyTurns.Add(new ChatTurn { Role = "user", Content = msg.Content ?? string.Empty });
                }
                else if (msg.IsAssistant)
                {
                    string content = msg.Content ?? string.Empty;

                    bool isPastTask = (!string.IsNullOrEmpty(msg.AgentTaskId) && msg.AgentTaskId != currentAgentTaskId) ||
                                      (msg.TaskSequenceNumber > 0 && msg.TaskSequenceNumber < currentTaskSeq);
                    if (isPastTask)
                    {
                        if (msg.GeneratedPlots != null && msg.GeneratedPlots.Count > 0)
                        {
                            var existingPlots = msg.GeneratedPlots.Where(p => p.IsFileExisting).ToList();
                            if (existingPlots.Count > 0 &&
                                !content.Contains("[已生成图表产物记录]") &&
                                !content.Contains("[系统历史记录-仅供上下文参考"))
                            {
                                var plotSummary = string.Join(", ", existingPlots.Select(p => $"`{System.IO.Path.GetFileName(p.FilePath)}` ({p.Title})"));
                                content = $"{content}\n\n[系统历史记录-仅供上下文参考，严禁在正式回答中复述此行] 已在本地渲染生成图表：{plotSummary}。后续分析应直接基于既有成果推进，无需重复绘图。";
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            historyTurns.Add(new ChatTurn { Role = "assistant", Content = content });
                            continue;
                        }
                    }

                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        var toolCallItems = msg.ToolCalls.Select(tc => new ToolCallItem
                        {
                            Id = string.IsNullOrWhiteSpace(tc.CallId) ? $"call_{Guid.NewGuid():N}" : tc.CallId,
                            Type = "function",
                            Function = new ToolCallFunction
                            {
                                Name = tc.ToolName,
                                Arguments = tc.Arguments
                            }
                        }).ToList();

                        historyTurns.Add(new ChatTurn
                        {
                            Role = "assistant",
                            Content = string.Empty,
                            ToolCalls = toolCallItems
                        });

                        for (int i = 0; i < msg.ToolCalls.Count; i++)
                        {
                            var tc = msg.ToolCalls[i];
                            string callId = toolCallItems[i].Id;
                            string toolOutput = tc.Output ?? string.Empty;

                            if (isPastTask && toolOutput.Length > 300)
                            {
                                toolOutput = toolOutput[..300] + "...\n[历史过往日志已折叠]";
                            }
                            else if (string.IsNullOrWhiteSpace(toolOutput))
                            {
                                toolOutput = "工具执行完成。";
                            }

                            historyTurns.Add(new ChatTurn
                            {
                                Role = "tool",
                                ToolCallId = callId,
                                Content = toolOutput
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            historyTurns.Add(new ChatTurn { Role = "assistant", Content = content });
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(content))
                    {
                        historyTurns.Add(new ChatTurn { Role = "assistant", Content = content });
                    }
                }
            }

            var chatService = new LlmChatService();
            var thinkingWatch = new Stopwatch();

            var pythonTool = new RunPythonTool(() => _settings, _pythonExecService, _safetyGate, () => _currentGroup?.GroupId);
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

                    if (!string.IsNullOrEmpty(update.IntermediateContent) && (update.Phase == "streaming" || update.Phase == "complete"))
                    {
                        assistantMessage.Content = update.IntermediateContent;
                    }

                    // 实时同步挂接新生成的图表产物至消息气泡与画廊 (全量标准化去重，严格限制在 agent_sandbox)
                    if (update.GeneratedArtifactsSoFar.Count > 0)
                    {
                        foreach (var artPath in update.GeneratedArtifactsSoFar)
                        {
                            string normArt;
                            try { normArt = Path.GetFullPath(artPath); }
                            catch { normArt = artPath; }

                            if (!PythonExecutionService.IsInSandboxDirectory(normArt, _currentGroup?.GroupId))
                            {
                                continue;
                            }

                            if (!assistantMessage.GeneratedPlots.Any(p =>
                            {
                                try { return Path.GetFullPath(p.FilePath).Equals(normArt, StringComparison.OrdinalIgnoreCase); }
                                catch { return p.FilePath.Equals(normArt, StringComparison.OrdinalIgnoreCase); }
                            }))
                            {
                                var plotItem = new ChartArtifactItem
                                {
                                    FilePath = normArt,
                                    Title = Path.GetFileNameWithoutExtension(normArt),
                                    CreatedAt = DateTime.Now
                                };
                                assistantMessage.GeneratedPlots.Add(plotItem);
                                AddPlotToGallery(plotItem, switchToVis: false);
                            }
                        }
                    }

                    bool isNearBottom = (svChatList.ScrollableHeight - svChatList.VerticalOffset) <= 40;
                    if (isNearBottom)
                    {
                        svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                    }

                    if (update.Phase == "complete")
                    {
                        _ = PersistCurrentSessionAsync(force: true);
                    }
                    else
                    {
                        _ = PersistCurrentSessionAsync(force: false);
                    }
                });
            });

            try
            {
                thinkingWatch.Start();

                int effectiveMaxIterations = Math.Clamp(currentSettings.AgentMaxIterations > 0 ? currentSettings.AgentMaxIterations : 12, 6, 20);

                var loopResult = await agentLoopService.RunLoopAsync(
                    currentSettings,
                    historyTurns,
                    documentContext,
                    question,
                    maxIterations: effectiveMaxIterations,
                    activeSkillInstruction: activeSkillInstruction,
                    progress: progress,
                    cancellationToken: cancellationToken);

                thinkingWatch.Stop();
                double totalThinkingTime = thinkingWatch.Elapsed.TotalSeconds;

                var uiUpdateTcs = new TaskCompletionSource<bool>();
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        assistantMessage.IsThinking = false;
                        assistantMessage.ThinkingSeconds = totalThinkingTime;
                        assistantMessage.IsReasoningExpanded = false;

                        assistantMessage.TerminationReason = loopResult.TerminationReason;
                        assistantMessage.IsStreamTruncated = loopResult.IsStreamTruncated;
                        if (loopResult.ExecutedToolCalls != null && loopResult.ExecutedToolCalls.Count > 0)
                        {
                            assistantMessage.ToolCalls = new List<ToolCallRecordItem>(loopResult.ExecutedToolCalls);
                        }

                        if (!string.IsNullOrWhiteSpace(loopResult.ReasoningContent))
                        {
                            assistantMessage.ReasoningContent = loopResult.ReasoningContent;
                        }

                        if (loopResult.Success)
                        {
                            assistantMessage.Content = loopResult.FinalContent;
                            if (loopResult.TerminationReason == AgentTerminationReason.MaxIterationsReached)
                            {
                                tbChatRunStatus.Text = $"分析完毕 (达到探索上限 {loopResult.TotalIterations} 轮，已生成综合研报)";
                            }
                            else if (loopResult.TerminationReason == AgentTerminationReason.ConsecutiveErrorsTripped)
                            {
                                tbChatRunStatus.Text = "分析完毕 (触发连续报错熔断，已汇聚阶段性研报)";
                            }
                            else
                            {
                                tbChatRunStatus.Text = $"分析完毕 (共 {loopResult.TotalIterations} 轮 Agent 决策)";
                            }
                        }
                        else
                        {
                            if (cancellationToken.IsCancellationRequested || loopResult.TerminationReason == AgentTerminationReason.UserCancelled)
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
                                assistantMessage.Content = $"分析过程中出现异常：{loopResult.ErrorMessage}";
                                tbChatRunStatus.Text = "分析异常";
                            }
                        }

                        if (loopResult.GeneratedArtifactPaths.Count > 0)
                        {
                            foreach (var artPath in loopResult.GeneratedArtifactPaths)
                            {
                                string normArt;
                                try { normArt = Path.GetFullPath(artPath); }
                                catch { normArt = artPath; }

                                if (!PythonExecutionService.IsInSandboxDirectory(normArt, _currentGroup?.GroupId))
                                {
                                    continue;
                                }

                                if (!assistantMessage.GeneratedPlots.Any(p =>
                                {
                                    try { return Path.GetFullPath(p.FilePath).Equals(normArt, StringComparison.OrdinalIgnoreCase); }
                                    catch { return p.FilePath.Equals(normArt, StringComparison.OrdinalIgnoreCase); }
                                }))
                                {
                                    var plotItem = new ChartArtifactItem
                                    {
                                        FilePath = normArt,
                                        Title = Path.GetFileNameWithoutExtension(normArt),
                                        CreatedAt = DateTime.Now
                                    };
                                    assistantMessage.GeneratedPlots.Add(plotItem);
                                    AddPlotToGallery(plotItem, switchToVis: false);
                                }
                            }
                        }

                        svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                        uiUpdateTcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        uiUpdateTcs.TrySetException(ex);
                    }
                });

                // 核心同步闸门：等待 UI 队列完成数据绑定与图表挂接，设置 2 秒安全超时防止 UI 队列死锁
                using var delayCts = new CancellationTokenSource();
                var completedTask = await Task.WhenAny(uiUpdateTcs.Task, Task.Delay(2000, delayCts.Token));
                if (completedTask == uiUpdateTcs.Task)
                {
                    delayCts.Cancel();
                }
                else
                {
                    assistantMessage.TerminationReason = loopResult.TerminationReason;
                    assistantMessage.IsStreamTruncated = loopResult.IsStreamTruncated;
                    if (loopResult.ExecutedToolCalls != null && loopResult.ExecutedToolCalls.Count > 0)
                    {
                        assistantMessage.ToolCalls = new List<ToolCallRecordItem>(loopResult.ExecutedToolCalls);
                    }
                    if (assistantMessage.ThinkingSeconds <= 0 && totalThinkingTime > 0)
                    {
                        assistantMessage.ThinkingSeconds = totalThinkingTime;
                    }
                }
            }
            catch (OperationCanceledException)
            {
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

                // 关键节点 C: Agent 最终执行完成、手动停止或异常中断，强制终态异步安全落盘
                await PersistCurrentSessionAsync(force: true);
            }
        }

        private void btnClearChat_Click(object sender, RoutedEventArgs e)
        {
            RemoveActiveSkill();
            ChatMessages.Clear();
            InitializeWelcomeChatMessage();
            _ = PersistCurrentSessionAsync(force: true);
        }

        private async Task PersistCurrentSessionAsync(bool force = false)
        {
            if (_currentSession == null) return;

            if (!force)
            {
                var now = DateTime.Now;
                if ((now - _lastDebouncePersistTime).TotalSeconds < 4.0)
                {
                    return;
                }
                _lastDebouncePersistTime = now;
            }

            await _persistLock.WaitAsync();
            try
            {
                var realMessages = ChatMessages.Where(m => !m.Content.Contains("欢迎使用 AI 数据分析与文档伴读看板")).ToList();
                _currentSession.Messages = realMessages;
                _currentSession.LastActiveAt = DateTime.Now;

                if (!string.IsNullOrEmpty(_currentSession.BindGroupId))
                {
                    var matchingGroup = DocumentGroups.FirstOrDefault(g => g.GroupId == _currentSession.BindGroupId);
                    if (matchingGroup != null)
                    {
                        _currentSession.BindGroupName = matchingGroup.GroupName;
                    }
                }

                if (realMessages.Count > 0 &&
                    (_currentSession.Title.StartsWith("新建对话") || _currentSession.Title.StartsWith("新对话") || _currentSession.Title.StartsWith("默认")))
                {
                    var firstUserMsg = realMessages.FirstOrDefault(m => m.IsUser);
                    if (firstUserMsg != null && !string.IsNullOrWhiteSpace(firstUserMsg.Content))
                    {
                        string cleanTitle = firstUserMsg.Content.Trim().Replace("\r", " ").Replace("\n", " ");
                        if (cleanTitle.Length > 16)
                        {
                            cleanTitle = cleanTitle.Substring(0, 16) + "...";
                        }
                        _currentSession.Title = cleanTitle;
                    }
                }

                await LibraryPersistenceService.Instance.SaveSessionAsync(_currentSession);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PersistCurrentSessionAsync 写入异常: {ex.Message}");
            }
            finally
            {
                _persistLock.Release();
            }
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
                        ShowGlobalToast("已复制到剪贴板");
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
            sbContent.AppendLine("### 可视化大屏核心资产与指标概览\n");
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

            sbContent.AppendLine("\n### 深度洞察与业务推导结论\n");
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

        #region 文档库双重视角切换与文档组管理

        private void btnViewDocGroups_Click(object sender, RoutedEventArgs e)
        {
            gridDocGroupView.Visibility = Visibility.Visible;
            gridAllDocsView.Visibility = Visibility.Collapsed;

            btnViewDocGroups.Background = BrushCardBg;
            btnViewDocGroups.Foreground = BrushTextPrimary;
            btnViewDocGroups.FontWeight = FontWeights.SemiBold;

            btnViewAllDocs.Background = BrushTransparent;
            btnViewAllDocs.Foreground = BrushTextSecondary;
            btnViewAllDocs.FontWeight = FontWeights.Normal;
        }

        private void btnViewAllDocs_Click(object sender, RoutedEventArgs e)
        {
            gridDocGroupView.Visibility = Visibility.Collapsed;
            gridAllDocsView.Visibility = Visibility.Visible;

            btnViewAllDocs.Background = BrushCardBg;
            btnViewAllDocs.Foreground = BrushTextPrimary;
            btnViewAllDocs.FontWeight = FontWeights.SemiBold;

            btnViewDocGroups.Background = BrushTransparent;
            btnViewDocGroups.Foreground = BrushTextSecondary;
            btnViewDocGroups.FontWeight = FontWeights.Normal;
        }

        private async void btnCreateNewGroup_Click(object sender, RoutedEventArgs e)
        {
            string? name = await ShowInputTextDialogAsync("新建文档组", $"文档组_{DateTime.Now:MMdd_HHmm}", "请输入文档组名称（例如：财务复盘组）");
            if (string.IsNullOrWhiteSpace(name)) return;

            var group = await LibraryPersistenceService.Instance.CreateGroupAsync(name);
            DocumentGroups.Insert(0, group);
            _currentGroup = group;
            if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
            {
                _isSelectingGroupProgrammatically = true;
                try
                {
                    lvDocumentGroups.SelectedItem = group;
                }
                finally
                {
                    _isSelectingGroupProgrammatically = false;
                }
            }
            UpdateDocCountDisplay();
        }

        private async void btnPackCurrentDocsToGroup_Click(object sender, RoutedEventArgs e)
        {
            if (Documents.Count == 0)
            {
                await ShowMessageDialogAsync("提示", "当前工作区尚未导入任何文档，请先在右侧或切换至【工作区文档】选择本地文档。");
                return;
            }

            string defaultName = Documents.Count == 1 ? $"{Path.GetFileNameWithoutExtension(Documents[0].Name)} 分析组" : $"数据组_{DateTime.Now:MMdd_HHmm}";
            string? name = await ShowInputTextDialogAsync("打包为新文档组", defaultName, "请输入新文档组名称");
            if (string.IsNullOrWhiteSpace(name)) return;

            var docIds = Documents.Select(d => d.DocId).ToList();
            var group = await LibraryPersistenceService.Instance.CreateGroupAsync(name, docIds);
            DocumentGroups.Insert(0, group);
            _currentGroup = group;

            if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
            {
                _isSelectingGroupProgrammatically = true;
                try
                {
                    lvDocumentGroups.SelectedItem = group;
                }
                finally
                {
                    _isSelectingGroupProgrammatically = false;
                }
            }

            // 严格遵循不可变规范：新建文档组一律创建专属新会话，绝不篡改任何现有会话
            await CreateNewSessionForGroupAsync(group);

            UpdateDocCountDisplay();
        }

        private async Task LoadGroupDocumentsAsync(DocumentGroup? group, bool selectAndPreviewFirstDoc = true)
        {
            await _docsLoadLock.WaitAsync();
            try
            {
                if (group == null)
                {
                    Documents.Clear();
                    _currentDoc = null;
                    DisplayDocument(null);
                    UpdateDocCountDisplay();
                    return;
                }

                // 1. 先在局部临时集合中顺序异步准备完整文档，避免交错并发导致集合半状态污染
                var targetDocs = new List<DocumentItem>();
                foreach (var docId in group.DocumentIds)
                {
                    var doc = await LibraryPersistenceService.Instance.LoadDocumentMetaAsync(docId, group.GroupId);
                    if (doc != null && !targetDocs.Any(d => d.DocId == doc.DocId))
                    {
                        targetDocs.Add(doc);
                    }
                }

                // 补充扫描本组专属 documents 目录，防止遗漏
                var groupDocsOnDisk = await LibraryPersistenceService.Instance.ListAllDocumentsAsync(group.GroupId);
                foreach (var doc in groupDocsOnDisk)
                {
                    if (!targetDocs.Any(d => d.DocId == doc.DocId))
                    {
                        targetDocs.Add(doc);
                        if (!group.DocumentIds.Contains(doc.DocId))
                        {
                            group.DocumentIds.Add(doc.DocId);
                        }
                    }
                }

                Documents.Clear();
                foreach (var doc in targetDocs)
                {
                    if (!Documents.Any(d => d.DocId == doc.DocId))
                    {
                        Documents.Add(doc);
                    }
                }

                UpdateDocCountDisplay();

                if (selectAndPreviewFirstDoc)
                {
                    _currentDoc = Documents.FirstOrDefault();
                    if (_currentDoc != null)
                    {
                        if (lvDocuments != null) lvDocuments.SelectedItem = _currentDoc;
                        DisplayDocument(_currentDoc);
                    }
                    else
                    {
                        DisplayDocument(null);
                    }
                }
            }
            finally
            {
                _docsLoadLock.Release();
            }
        }

        /// 统一选中文档组并联动工作区文档与组下会话（严格遵守文档组与会话永久绑定不可变原则）
        private async Task SelectDocumentGroupAsync(DocumentGroup group, bool switchSessionIfNeeded = true)
        {
            if (group == null) return;

            if (group.IsSystemGroup)
            {
                _currentGroup = group;
                foreach (var g in DocumentGroups)
                {
                    g.IsSelected = (g == group);
                }
                if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
                {
                    _isSelectingGroupProgrammatically = true;
                    try { lvDocumentGroups.SelectedItem = group; }
                    finally { _isSelectingGroupProgrammatically = false; }
                }

                await LoadGroupDocumentsAsync(null, selectAndPreviewFirstDoc: false);

                if (switchSessionIfNeeded)
                {
                    if (_currentSession == null || !string.IsNullOrEmpty(_currentSession.BindGroupId))
                    {
                        var targetSession = ChatSessions
                            .Where(s => string.IsNullOrEmpty(s.BindGroupId) || s.BindGroupId == LibraryPersistenceService.StandaloneGroupId)
                            .OrderByDescending(s => s.LastActiveAt)
                            .FirstOrDefault();

                        if (targetSession != null)
                        {
                            SelectSession(targetSession);
                        }
                        else
                        {
                            btnNewChat_Click(this, new RoutedEventArgs());
                        }
                    }
                }
                return;
            }

            _currentGroup = group;
            foreach (var g in DocumentGroups)
            {
                g.IsSelected = (g == group);
            }

            if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
            {
                _isSelectingGroupProgrammatically = true;
                try
                {
                    lvDocumentGroups.SelectedItem = group;
                }
                finally
                {
                    _isSelectingGroupProgrammatically = false;
                }
            }

            await LoadGroupDocumentsAsync(group, selectAndPreviewFirstDoc: true);

            if (switchSessionIfNeeded)
            {
                if (_currentSession == null || _currentSession.BindGroupId != group.GroupId)
                {
                    var targetSession = ChatSessions
                        .Where(s => s.BindGroupId == group.GroupId)
                        .OrderByDescending(s => s.LastActiveAt)
                        .FirstOrDefault();

                    if (targetSession != null)
                    {
                        SelectSession(targetSession);
                    }
                    else
                    {
                        await CreateNewSessionForGroupAsync(group);
                    }
                }
            }
        }

        private async void lvDocumentGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSelectingGroupProgrammatically) return;

            if (lvDocumentGroups.SelectedItem is DocumentGroup group)
            {
                await SelectDocumentGroupAsync(group, switchSessionIfNeeded: true);
            }
        }

        private async void btnRenameGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DocumentGroup group)
            {
                if (group.IsSystemGroup)
                {
                    await ShowMessageDialogAsync("系统提示", "【独立自由对话】为系统内置专区，不可重命名。");
                    return;
                }
                string? newName = await ShowInputTextDialogAsync("重命名文档组", group.GroupName, "请输入新名称");
                if (!string.IsNullOrWhiteSpace(newName) && newName != group.GroupName)
                {
                    group.GroupName = newName;
                    group.LastActiveAt = DateTime.Now;
                    await LibraryPersistenceService.Instance.SaveGroupAsync(group);

                    foreach (var session in ChatSessions.Where(s => s.BindGroupId == group.GroupId))
                    {
                        session.BindGroupName = newName;
                        await LibraryPersistenceService.Instance.SaveSessionAsync(session);
                    }
                    SyncGroupSessions();
                }
            }
        }

        private async void btnDeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DocumentGroup group)
            {
                if (group.IsSystemGroup)
                {
                    await ShowMessageDialogAsync("系统提示", "【独立自由对话】为系统内置专区，不可删除。");
                    return;
                }

                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "删除文档组",
                        Content = new TextBlock
                        {
                            Text = $"确定要删除文档组【{group.GroupName}】吗？\n\n将执行彻底物理清理：\n• 销毁该文档组容器目录及其名下所有历史对话记录\n• 清理该组专属运行沙盒与图表缓存\n\n（安全提示：您电脑上的本地原始数据文件完好保留，绝不删除）",
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 10, 0, 0)
                        },
                        PrimaryButtonText = "确认删除",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = this.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result != ContentDialogResult.Primary) return;

                    await LibraryPersistenceService.Instance.DeleteGroupAsync(group.GroupId);

                    foreach (var docId in group.DocumentIds.ToList())
                    {
                        bool isShared = DocumentGroups.Any(g => g.GroupId != group.GroupId && g.DocumentIds.Contains(docId));
                        if (!isShared)
                        {
                            LibraryPersistenceService.Instance.DeleteSchemaProfileCache(docId);
                        }
                    }

                    DocumentGroups.Remove(group);

                    var sessionsToDelete = ChatSessions.Where(s => s.BindGroupId == group.GroupId).ToList();
                    bool currentSessionDeleted = false;
                    foreach (var session in sessionsToDelete)
                    {
                        ChatSessions.Remove(session);
                        if (_currentSession?.Id == session.Id)
                        {
                            currentSessionDeleted = true;
                        }
                    }

                    if (_currentGroup == group)
                    {
                        _currentGroup = DocumentGroups.FirstOrDefault();
                        if (_currentGroup != null)
                        {
                            _isSelectingGroupProgrammatically = true;
                            try
                            {
                                lvDocumentGroups.SelectedItem = _currentGroup;
                            }
                            finally
                            {
                                _isSelectingGroupProgrammatically = false;
                            }
                            await SelectDocumentGroupAsync(_currentGroup, switchSessionIfNeeded: true);
                        }
                        else
                        {
                            Documents.Clear();
                            _currentDoc = null;
                            DisplayDocument(null);
                            ChatMessages.Clear();
                            ClearPlotVisualizationDashboard();
                            btnNewChat_Click(this, new RoutedEventArgs());
                        }
                    }
                    else if (currentSessionDeleted || _currentSession == null)
                    {
                        _currentSession = _currentGroup != null
                            ? ChatSessions.Where(s => s.BindGroupId == _currentGroup.GroupId).OrderByDescending(s => s.LastActiveAt).FirstOrDefault()
                            : ChatSessions.FirstOrDefault();

                        if (_currentSession != null)
                        {
                            SelectSession(_currentSession);
                        }
                        else
                        {
                            btnNewChat_Click(this, new RoutedEventArgs());
                        }
                    }

                    UpdateDocCountDisplay();
                    UpdateHistoryCountDisplay();
                    SyncGroupSessions();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除文档组异常: {ex.Message}");
                }
            }
        }

        private async Task ReloadCurrentGroupDocsAsync()
        {
            await LoadGroupDocumentsAsync(_currentGroup, selectAndPreviewFirstDoc: true);
        }

        private void SyncGroupSessions()
        {
            var standaloneGroup = DocumentGroups.FirstOrDefault(g => g.IsSystemGroup);
            if (standaloneGroup == null)
            {
                standaloneGroup = new DocumentGroup
                {
                    GroupId = LibraryPersistenceService.StandaloneGroupId,
                    GroupName = "独立自由对话",
                    CreatedAt = DateTime.MinValue,
                    LastActiveAt = DateTime.Now,
                    IsExpanded = true
                };
                DocumentGroups.Add(standaloneGroup);
            }
            else
            {
                standaloneGroup.GroupName = "独立自由对话";
            }

            foreach (var group in DocumentGroups)
            {
                group.AssociatedSessions.Clear();
            }

            foreach (var session in ChatSessions.OrderByDescending(s => s.LastActiveAt))
            {
                if (string.IsNullOrEmpty(session.BindGroupId) ||
                    string.Equals(session.BindGroupId, LibraryPersistenceService.StandaloneGroupId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!standaloneGroup.AssociatedSessions.Contains(session))
                    {
                        standaloneGroup.AssociatedSessions.Add(session);
                    }
                    continue;
                }

                var matchedGroup = DocumentGroups.FirstOrDefault(g => g.GroupId == session.BindGroupId);
                if (matchedGroup != null)
                {
                    if (!matchedGroup.AssociatedSessions.Contains(session))
                    {
                        matchedGroup.AssociatedSessions.Add(session);
                    }
                }
                else
                {
                    // 孤儿会话防丢失安全降级：绑定的组已被物理删除，安全重置为自由会话并收归专区
                    session.BindGroupId = null;
                    session.BindGroupName = string.Empty;
                    if (!standaloneGroup.AssociatedSessions.Contains(session))
                    {
                        standaloneGroup.AssociatedSessions.Add(session);
                    }
                }
            }
        }

        private async void DocGroupHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (point.Properties.IsLeftButtonPressed)
            {
                var group = (sender as FrameworkElement)?.Tag as DocumentGroup
                            ?? (sender as FrameworkElement)?.DataContext as DocumentGroup;
                if (group != null)
                {
                    await SelectDocumentGroupAsync(group, switchSessionIfNeeded: true);
                    e.Handled = true;
                }
            }
        }

        private async void DocGroupItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var group = (sender as FrameworkElement)?.Tag as DocumentGroup
                        ?? (sender as FrameworkElement)?.DataContext as DocumentGroup;
            if (group != null)
            {
                await SelectDocumentGroupAsync(group, switchSessionIfNeeded: true);
                btnViewAllDocs_Click(this, new RoutedEventArgs());
                btnTabDocPreview_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void btnGroupMoreMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var group = fe.Tag as DocumentGroup ?? fe.DataContext as DocumentGroup;
                if (group == null) return;

                var flyout = new MenuFlyout();

                var itemRename = new MenuFlyoutItem { Text = "重命名文档组" };
                itemRename.Click += (s, args) => btnRenameGroup_Click(new Button { Tag = group }, args);

                var itemDelete = new MenuFlyoutItem { Text = "删除文档组" };
                itemDelete.Click += (s, args) => btnDeleteGroup_Click(new Button { Tag = group }, args);

                flyout.Items.Add(itemRename);
                flyout.Items.Add(itemDelete);

                flyout.ShowAt(fe);
            }
        }

        private async void btnQuickNewSessionInGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var group = fe.Tag as DocumentGroup ?? fe.DataContext as DocumentGroup;
                if (group != null)
                {
                    await CreateNewSessionForGroupAsync(group);
                }
            }
        }

        private void btnToggleGroupExpand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var group = fe.Tag as DocumentGroup ?? fe.DataContext as DocumentGroup;
                if (group != null)
                {
                    group.IsExpanded = !group.IsExpanded;
                }
            }
        }

        private async void menuNewSessionFromGroup_Click(object sender, RoutedEventArgs e)
        {
            DocumentGroup? group = null;
            if (sender is FrameworkElement fe)
            {
                group = fe.Tag as DocumentGroup ?? fe.DataContext as DocumentGroup;
            }
            group ??= _currentGroup;

            if (group != null)
            {
                await CreateNewSessionForGroupAsync(group);
            }
        }

        private async Task CreateNewSessionForGroupAsync(DocumentGroup group)
        {
            if (group == null) return;

            if (group.IsSystemGroup)
            {
                btnNewChat_Click(this, new RoutedEventArgs());
                return;
            }

            int existingCount = ChatSessions.Count(s => s.BindGroupId == group.GroupId);
            string sessionTitle = existingCount == 0 ? group.GroupName : $"{group.GroupName} ({existingCount + 1})";

            var newSession = new ChatSessionItem
            {
                Title = sessionTitle,
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
                BindGroupId = group.GroupId,
                BindGroupName = group.GroupName,
                IsSelected = true
            };

            foreach (var s in ChatSessions)
            {
                s.IsSelected = false;
            }

            ChatSessions.Insert(0, newSession);
            _currentSession = newSession;
            if (lvHistorySessions != null) lvHistorySessions.SelectedItem = newSession;

            group.IsExpanded = true;
            SyncGroupSessions();

            await LibraryPersistenceService.Instance.SaveSessionAsync(newSession);

            _currentGroup = group;
            foreach (var grp in DocumentGroups)
            {
                grp.IsSelected = (grp == group);
            }
            if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
            {
                _isSelectingGroupProgrammatically = true;
                try
                {
                    lvDocumentGroups.SelectedItem = group;
                }
                finally
                {
                    _isSelectingGroupProgrammatically = false;
                }
            }

            await LoadGroupDocumentsAsync(group, selectAndPreviewFirstDoc: true);
            UpdateHistoryCountDisplay();
            btnTabDocPreview_Click(this, new RoutedEventArgs());

            RemoveActiveSkill();
            ChatMessages.Clear();
            ClearPlotVisualizationDashboard();
            tbChatRunStatus.Text = "";
            tbQuestion.Focus(FocusState.Programmatic);
        }

        private void menuRenameGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = (sender as FrameworkElement)?.Tag as DocumentGroup
                        ?? (sender as FrameworkElement)?.DataContext as DocumentGroup;
            if (group != null)
            {
                btnRenameGroup_Click(new Button { Tag = group }, e);
            }
        }

        private void menuDeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = (sender as FrameworkElement)?.Tag as DocumentGroup
                        ?? (sender as FrameworkElement)?.DataContext as DocumentGroup;
            if (group != null)
            {
                btnDeleteGroup_Click(new Button { Tag = group }, e);
            }
        }

        #endregion

        #region 历史会话管理 (左侧弹出抽屉)

        private void UpdateHistoryCountDisplay()
        {
            if (tbHistoryCount != null)
            {
                tbHistoryCount.Text = $"{ChatSessions.Count} 个会话";
            }
        }

        private async void btnNewChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGroup != null && !_currentGroup.IsSystemGroup)
            {
                await CreateNewSessionForGroupAsync(_currentGroup);
                return;
            }

            var newSession = new ChatSessionItem
            {
                Title = $"新对话 {DateTime.Now:MM-dd HH:mm}",
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
                BindGroupId = null,
                BindGroupName = string.Empty,
                IsSelected = true
            };

            foreach (var s in ChatSessions)
            {
                s.IsSelected = false;
            }

            ChatSessions.Insert(0, newSession);
            _currentSession = newSession;
            if (lvHistorySessions != null) lvHistorySessions.SelectedItem = newSession;

            await LibraryPersistenceService.Instance.SaveSessionAsync(newSession);

            SyncGroupSessions();
            UpdateHistoryCountDisplay();

            RemoveActiveSkill();
            ChatMessages.Clear();
            ClearPlotVisualizationDashboard();
            InitializeWelcomeChatMessage();
        }

        private void SessionItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (point.Properties.IsLeftButtonPressed)
            {
                var session = (sender as FrameworkElement)?.Tag as ChatSessionItem
                              ?? (sender as FrameworkElement)?.DataContext as ChatSessionItem;
                if (session != null)
                {
                    SelectSession(session);
                    e.Handled = true;
                }
            }
        }

        private void SelectSession(ChatSessionItem session)
        {
            if (session == null) return;

            _currentSession = session;
            foreach (var s in ChatSessions)
            {
                s.IsSelected = (s == session);
            }
            if (lvHistorySessions != null) lvHistorySessions.SelectedItem = session;

            if (!string.IsNullOrEmpty(session.BindGroupId) && (_currentGroup == null || _currentGroup.GroupId != session.BindGroupId))
            {
                var targetGroup = DocumentGroups.FirstOrDefault(g => g.GroupId == session.BindGroupId);
                if (targetGroup != null)
                {
                    _currentGroup = targetGroup;
                    foreach (var g in DocumentGroups)
                    {
                        g.IsSelected = (g == targetGroup);
                    }
                    if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != targetGroup)
                    {
                        _isSelectingGroupProgrammatically = true;
                        try { lvDocumentGroups.SelectedItem = targetGroup; }
                        finally { _isSelectingGroupProgrammatically = false; }
                    }
                    _ = LoadGroupDocumentsAsync(targetGroup, selectAndPreviewFirstDoc: true);
                }
            }
            else if (string.IsNullOrEmpty(session.BindGroupId) && _currentGroup != null)
            {
                _currentGroup = null;
                foreach (var g in DocumentGroups) g.IsSelected = false;
                if (lvDocumentGroups != null) lvDocumentGroups.SelectedItem = null;
                _ = LoadGroupDocumentsAsync(null, selectAndPreviewFirstDoc: false);
            }

            LoadSessionChat(session);
        }

        private void lvHistorySessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lvHistorySessions?.SelectedItem is ChatSessionItem session)
            {
                SelectSession(session);
            }
        }

        private async void LoadSessionChat(ChatSessionItem session)
        {
            if (session == null) return;

            _currentSession = session;

            var persisted = await LibraryPersistenceService.Instance.LoadSessionAsync(session.Id, session.BindGroupId);
            var messagesToLoad = persisted?.Messages ?? session.Messages;

            ChatMessages.Clear();
            ClearPlotVisualizationDashboard();

            if (messagesToLoad != null && messagesToLoad.Count > 0)
            {
                foreach (var msg in messagesToLoad)
                {
                    msg.IsThinking = false;
                    msg.IsReasoningExpanded = false;
                    ChatMessages.Add(msg);

                    if (msg.GeneratedPlots != null && msg.GeneratedPlots.Count > 0)
                    {
                        string scratchDir = PythonExecutionService.LocateOrInitScratchDirectory(session.BindGroupId ?? _currentGroup?.GroupId);
                        string scratchPrefix = scratchDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        var invalidPlots = msg.GeneratedPlots.Where(p =>
                            (p.FilePath.StartsWith(scratchPrefix, StringComparison.OrdinalIgnoreCase) &&
                            !PythonExecutionService.IsInSandboxDirectory(p.FilePath, session.BindGroupId ?? _currentGroup?.GroupId)) ||
                            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(p.FilePath), @"^chart_\d{10,}_\d+$")).ToList();
                        foreach (var inv in invalidPlots)
                        {
                            msg.GeneratedPlots.Remove(inv);
                        }

                        foreach (var plot in msg.GeneratedPlots)
                        {
                            if (plot.IsFileExisting)
                            {
                                AddPlotToGallery(plot, switchToVis: false);
                            }
                        }
                    }
                }

                if (PlotGallery.Count > 0)
                {
                    _activeDashboardPlot = PlotGallery.FirstOrDefault();
                    UpdateVisualizationDashboardUI();
                }
                else
                {
                    ClearPlotVisualizationDashboard();
                }
            }
            else
            {
                ClearPlotVisualizationDashboard();
                InitializeWelcomeChatMessage();
            }

            if (!string.IsNullOrEmpty(session.BindGroupId))
            {
                var group = DocumentGroups.FirstOrDefault(g => g.GroupId == session.BindGroupId);
                if (group == null)
                {
                    group = await LibraryPersistenceService.Instance.LoadGroupAsync(session.BindGroupId);
                    if (group != null)
                    {
                        DocumentGroups.Add(group);
                    }
                }

                if (group != null)
                {
                    _currentGroup = group;
                    foreach (var g in DocumentGroups)
                    {
                        g.IsSelected = (g == group);
                    }
                    if (lvDocumentGroups != null && lvDocumentGroups.SelectedItem != group)
                    {
                        _isSelectingGroupProgrammatically = true;
                        try
                        {
                            lvDocumentGroups.SelectedItem = group;
                        }
                        finally
                        {
                            _isSelectingGroupProgrammatically = false;
                        }
                    }

                    await LoadGroupDocumentsAsync(group, selectAndPreviewFirstDoc: true);
                }
            }
            else
            {
                _currentGroup = null;
                foreach (var g in DocumentGroups)
                {
                    g.IsSelected = false;
                }
            }

            UpdateDocCountDisplay();

            svChatList?.ChangeView(null, svChatList.ScrollableHeight, null);
        }

        private async void menuRenameSession_Click(object sender, RoutedEventArgs e)
        {
            var session = (sender as FrameworkElement)?.Tag as ChatSessionItem
                          ?? (sender as FrameworkElement)?.DataContext as ChatSessionItem
                          ?? _currentSession;

            if (session == null) return;

            string? newTitle = await ShowInputTextDialogAsync("重命名会话", session.Title, "请输入新的会话标题");
            if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != session.Title)
            {
                session.Title = newTitle.Trim();
                session.LastActiveAt = DateTime.Now;
                await LibraryPersistenceService.Instance.SaveSessionAsync(session);
                SyncGroupSessions();
            }
        }

        private void btnRenameSession_Click(object sender, RoutedEventArgs e)
        {
            menuRenameSession_Click(sender, e);
        }

        private async void menuDeleteSession_Click(object sender, RoutedEventArgs e)
        {
            var session = (sender as FrameworkElement)?.Tag as ChatSessionItem
                          ?? (sender as FrameworkElement)?.DataContext as ChatSessionItem
                          ?? _currentSession;

            if (session == null) return;

            bool confirm = await ShowConfirmDialogAsync("删除会话", $"确定要删除会话【{session.Title}】吗？删除后不可恢复。");
            if (confirm)
            {
                await LibraryPersistenceService.Instance.DeleteSessionAsync(session.Id, session.BindGroupId);
                ChatSessions.Remove(session);

                if (_currentSession == session)
                {
                    _currentSession = ChatSessions.FirstOrDefault();
                    if (_currentSession != null)
                    {
                        SelectSession(_currentSession);
                    }
                    else
                    {
                        btnNewChat_Click(this, new RoutedEventArgs());
                    }
                }

                SyncGroupSessions();
                UpdateHistoryCountDisplay();
            }
        }

        private void btnDeleteSession_Click(object sender, RoutedEventArgs e)
        {
            menuDeleteSession_Click(sender, e);
        }

        #endregion

        #region 通用轻量原生弹窗辅助封装

        private async Task<string?> ShowInputTextDialogAsync(string title, string defaultText, string placeholder = "")
        {
            try
            {
                var tb = new TextBox
                {
                    Text = defaultText,
                    PlaceholderText = placeholder,
                    Margin = new Thickness(0, 10, 0, 0),
                    Width = 320
                };
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = tb,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                var res = await dialog.ShowAsync();
                return res == ContentDialogResult.Primary ? tb.Text.Trim() : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowInputTextDialogAsync 异常: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> ShowConfirmDialogAsync(string title, string content)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) },
                    PrimaryButtonText = "确定删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowConfirmDialogAsync 异常: {ex.Message}");
                return false;
            }
        }

        private async Task ShowMessageDialogAsync(string title, string content)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) },
                    CloseButtonText = "知道了",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowMessageDialogAsync 异常: {ex.Message}");
            }
        }

        #endregion

        #region 全局浮动胶囊 Toast 提示 (类似手机顶部通知)

        private DispatcherTimer? _toastTimer;
        private Storyboard? _toastFadeOutStoryboard;

        public void ShowGlobalToast(string message = "已复制到剪贴板", int durationMs = 1800)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (borderGlobalToast == null || tbGlobalToastText == null) return;

                tbGlobalToastText.Text = message;
                borderGlobalToast.Visibility = Visibility.Visible;

                // 停止之前的计时器与淡出动画，防止多次快速点击冲突
                _toastTimer?.Stop();
                _toastFadeOutStoryboard?.Stop();

                var animFadeIn = new DoubleAnimation
                {
                    From = borderGlobalToast.Opacity,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180))
                };
                Storyboard.SetTarget(animFadeIn, borderGlobalToast);
                Storyboard.SetTargetProperty(animFadeIn, "Opacity");

                var sbIn = new Storyboard();
                sbIn.Children.Add(animFadeIn);

                if (transGlobalToast != null)
                {
                    var animSlideIn = new DoubleAnimation
                    {
                        From = transGlobalToast.Y,
                        To = 0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    Storyboard.SetTarget(animSlideIn, transGlobalToast);
                    Storyboard.SetTargetProperty(animSlideIn, "Y");
                    sbIn.Children.Add(animSlideIn);
                }

                sbIn.Begin();

                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
                _toastTimer.Tick += (s, e) =>
                {
                    _toastTimer.Stop();

                    var animFadeOut = new DoubleAnimation
                    {
                        From = borderGlobalToast.Opacity,
                        To = 0.0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(220))
                    };
                    Storyboard.SetTarget(animFadeOut, borderGlobalToast);
                    Storyboard.SetTargetProperty(animFadeOut, "Opacity");

                    _toastFadeOutStoryboard = new Storyboard();
                    _toastFadeOutStoryboard.Children.Add(animFadeOut);

                    if (transGlobalToast != null)
                    {
                        var animSlideOut = new DoubleAnimation
                        {
                            From = transGlobalToast.Y,
                            To = -10,
                            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                        };
                        Storyboard.SetTarget(animSlideOut, transGlobalToast);
                        Storyboard.SetTargetProperty(animSlideOut, "Y");
                        _toastFadeOutStoryboard.Children.Add(animSlideOut);
                    }

                    _toastFadeOutStoryboard.Completed += (s2, e2) =>
                    {
                        if (borderGlobalToast.Opacity <= 0.05)
                        {
                            borderGlobalToast.Visibility = Visibility.Collapsed;
                        }
                    };
                    _toastFadeOutStoryboard.Begin();
                };
                _toastTimer.Start();
            });
        }

        #endregion

        #region 中栏与右栏自由拖拽调宽手柄 (GridSplitter)

        private bool _isDraggingSplitter = false;
        private double _startDragX = 0;
        private double _startRightColWidth = 0;

        private void Splitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (borderSplitterBar != null)
            {
                borderSplitterBar.Background = BrushAccentWarm;
            }
        }

        private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSplitter)
            {
                if (borderSplitterBar != null)
                {
                    borderSplitterBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD6, 0xCD, 0xBF));
                }
            }
        }

        private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement el)
            {
                el.CapturePointer(e.Pointer);
                _isDraggingSplitter = true;
                _startDragX = e.GetCurrentPoint(this.Content).Position.X;
                _startRightColWidth = colRight.ActualWidth;
                if (borderSplitterBar != null)
                {
                    borderSplitterBar.Background = BrushAccentWarm;
                }
                e.Handled = true;
            }
        }

        private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingSplitter)
            {
                double currentX = e.GetCurrentPoint(this.Content).Position.X;
                double deltaX = currentX - _startDragX;

                double targetWidth = _startRightColWidth - deltaX;

                // 约束右栏最小宽度 280px
                double minRight = 280;
                // 约束右栏最大宽度（保证中栏至少保留 300px 宽度）
                double totalAvailable = gridMainContentColumns != null ? gridMainContentColumns.ActualWidth : 1200;
                double maxRight = Math.Max(minRight, totalAvailable - 300 - 14);

                double clampedWidth = Math.Clamp(targetWidth, minRight, maxRight);
                colRight.Width = new GridLength(clampedWidth, GridUnitType.Pixel);
                e.Handled = true;
            }
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            ClampRightColumnWidth(e.Size.Width);
        }

        private void ClampRightColumnWidth(double windowWidth)
        {
            if (colRight == null || colMiddle == null || gridMainContentColumns == null) return;

            double availableWidth = windowWidth - 56 - 22 - 14;
            if (availableWidth <= 0) return;

            double minMiddleWidth = 300;
            double minRightWidth = 280;

            double maxRightWidth = Math.Max(minRightWidth, availableWidth - minMiddleWidth);

            double currentRightWidth = colRight.Width.GridUnitType == GridUnitType.Pixel
                ? colRight.Width.Value
                : colRight.ActualWidth;

            if (currentRightWidth <= 0) currentRightWidth = 420;

            if (currentRightWidth > maxRightWidth)
            {
                colRight.Width = new GridLength(maxRightWidth, GridUnitType.Pixel);
            }
            else if (currentRightWidth < minRightWidth)
            {
                colRight.Width = new GridLength(minRightWidth, GridUnitType.Pixel);
            }
        }

        private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingSplitter)
            {
                if (sender is UIElement el)
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                _isDraggingSplitter = false;
                if (borderSplitterBar != null)
                {
                    borderSplitterBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD6, 0xCD, 0xBF));
                }
                e.Handled = true;
            }
        }

        private void InitSplitterCursor()
        {
            try
            {
                var cursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
                var prop = typeof(UIElement).GetProperty("ProtectedCursor",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (prop != null && borderSplitterContainer != null)
                {
                    prop.SetValue(borderSplitterContainer, cursor);
                }
                var diagCursorRight = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast);
                if (prop != null && borderViewerResizeGripRight != null)
                {
                    prop.SetValue(borderViewerResizeGripRight, diagCursorRight);
                }
                var diagCursorLeft = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest);
                if (prop != null && borderViewerResizeGripLeft != null)
                {
                    prop.SetValue(borderViewerResizeGripLeft, diagCursorLeft);
                }
                var handCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
                if (prop != null && gridViewerImageCanvas != null)
                {
                    prop.SetValue(gridViewerImageCanvas, handCursor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitSplitterCursor 异常: {ex.Message}");
            }
        }

        #endregion
    }
}
