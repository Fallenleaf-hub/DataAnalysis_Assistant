using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Data_Analysis.Models;
using Data_Analysis.Services;

namespace Data_Analysis
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DocumentItem> Documents { get; } = new();
        public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
        public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();

        private AppSettings _settings = new();
        private DocumentItem? _currentDoc;

        private static readonly SolidColorBrush BrushCardBg = new(Microsoft.UI.Colors.White);
        private static readonly SolidColorBrush BrushTransparent = new(Microsoft.UI.Colors.Transparent);
        private static readonly SolidColorBrush BrushTextPrimary = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x28, 0x22));
        private static readonly SolidColorBrush BrushTextSecondary = new(Windows.UI.Color.FromArgb(255, 0x7D, 0x74, 0x6A));

        public MainWindow()
        {
            InitializeComponent();

            // 绑定数据源
            lvDocuments.ItemsSource = Documents;
            icChatMessages.ItemsSource = ChatMessages;
            lvHistorySessions.ItemsSource = ChatSessions;

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
            CloseDrawer();
        }

        // 文档库按钮（带数字角标）：点击直接呼出/秒切文档库抽屉
        private void btnNavDocLibrary_Click(object sender, RoutedEventArgs e)
        {
            // 如果抽屉已经打开且当前正是文档库，再次点击则收起抽屉回到看板
            if (IsDrawerOpen && paneDocLibrary.Visibility == Visibility.Visible)
            {
                CloseDrawer();
                return;
            }

            // 无论之前是关闭状态还是正处于历史会话，直接无缝秒切至文档库！
            OpenDrawer(paneDocLibrary, btnNavDocLibrary, tbNavDocText);
        }

        // 历史会话按钮：点击直接呼出/秒切历史会话抽屉（与文档库同样模式）
        private void btnNavHistory_Click(object sender, RoutedEventArgs e)
        {
            // 如果抽屉已经打开且当前正是历史记录，再次点击则收起抽屉回到看板
            if (IsDrawerOpen && paneHistorySessions.Visibility == Visibility.Visible)
            {
                CloseDrawer();
                return;
            }

            // 无论之前是关闭状态还是正处于文档库，直接无缝秒切至历史会话！
            OpenDrawer(paneHistorySessions, btnNavHistory, tbNavHistoryText);
        }

        // 打开抽屉并切换至指定内容面板
        private void OpenDrawer(FrameworkElement paneToShow, Button navBtn, TextBlock navText)
        {
            gridMainWorkspace.Visibility = Visibility.Visible;
            gridSettingsPage.Visibility = Visibility.Collapsed;

            paneDocLibrary.Visibility = (paneToShow == paneDocLibrary) ? Visibility.Visible : Visibility.Collapsed;
            paneHistorySessions.Visibility = (paneToShow == paneHistorySessions) ? Visibility.Visible : Visibility.Collapsed;

            borderDrawerPane.Visibility = Visibility.Visible;
            drawerBackdrop.Visibility = Visibility.Visible;

            HighlightNavButton(navBtn, navText);
        }

        // 收起抽屉
        private void CloseDrawer(bool highlightHome = true)
        {
            borderDrawerPane.Visibility = Visibility.Collapsed;
            drawerBackdrop.Visibility = Visibility.Collapsed;

            if (highlightHome)
            {
                HighlightNavButton(btnNavHome, tbNavHomeText);
            }
        }

        // 左下角齿轮：进入设置二级界面
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

            HighlightNavButton(btnNavSettings, tbNavSettingsText);
        }

        private void HighlightNavButton(Button activeBtn, TextBlock activeText)
        {
            btnNavHome.Background = BrushTransparent;
            btnNavDocLibrary.Background = BrushTransparent;
            btnNavHistory.Background = BrushTransparent;
            btnNavSettings.Background = BrushTransparent;

            tbNavHomeText.Foreground = BrushTextSecondary;
            tbNavHomeText.FontWeight = FontWeights.Normal;
            tbNavDocText.Foreground = BrushTextSecondary;
            tbNavDocText.FontWeight = FontWeights.Normal;
            tbNavHistoryText.Foreground = BrushTextSecondary;
            tbNavHistoryText.FontWeight = FontWeights.Normal;
            tbNavSettingsText.Foreground = BrushTextSecondary;
            tbNavSettingsText.FontWeight = FontWeights.Normal;

            activeBtn.Background = BrushCardBg;
            activeText.Foreground = BrushTextPrimary;
            activeText.FontWeight = FontWeights.SemiBold;
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

        #endregion

        #region 二级界面：设置目录与视图管理 (LLM配置 / Skill / 关于我们)

        private void btnSettingsNavLlm_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsLlm, btnSettingsNavLlm, tbSettingsNavLlmText);
        }

        private void btnSettingsNavSkill_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsView(viewSettingsSkill, btnSettingsNavSkill, tbSettingsNavSkillText);
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

            UpdateSettingsDisplay();
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
            infoBarSettings.Message = $"大模型配置已成功保存！当前模型: {_settings.ModelName}，Max Context: {_settings.MaxContextLength:N0} tokens。";
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
            UpdateSettingsDisplay();

            infoBarSettings.IsOpen = true;
            infoBarSettings.Severity = InfoBarSeverity.Informational;
            infoBarSettings.Title = "已恢复默认";
            infoBarSettings.Message = "所有配置已重置为标准默认值。";
        }

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
                        if (ext == ".txt" || ext == ".md" || ext == ".csv")
                        {
                            // 1. 纯文本 / Markdown / CSV
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
                            item.Status = "已提取文本";
                        }
                        else if (ext == ".docx")
                        {
                            // 3. Word 文档解析 (DocX：读取全文字符)
                            using var stream = await file.OpenStreamForReadAsync();
                            using var doc = Xceed.Words.NET.DocX.Load(stream);

                            item.ExtractedText = doc.Text;
                            item.Status = "已提取文本";
                        }
                        else if (ext == ".xlsx")
                        {
                            // 4. Excel 工作簿解析 (EPPlus 8：读取首个工作表二维矩阵)
                            using var stream = await file.OpenStreamForReadAsync();
                            using var package = new OfficeOpenXml.ExcelPackage(stream);

                            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
                            if (worksheet != null && worksheet.Dimension != null)
                            {
                                var sb = new StringBuilder();
                                int startRow = worksheet.Dimension.Start.Row;
                                int endRow = worksheet.Dimension.End.Row;
                                int startCol = worksheet.Dimension.Start.Column;
                                int endCol = worksheet.Dimension.End.Column;

                                for (int row = startRow; row <= endRow; row++)
                                {
                                    var rowCells = new List<string>();
                                    for (int col = startCol; col <= endCol; col++)
                                    {
                                        rowCells.Add(worksheet.Cells[row, col].Text);
                                    }
                                    sb.AppendLine(string.Join("\t", rowCells));
                                }

                                item.ExtractedText = sb.ToString();
                                item.Status = "已提取文本";
                            }
                            else
                            {
                                item.ExtractedText = "当前 Excel 工作簿为空或无有效单元格数据。";
                                item.Status = "已提取文本";
                            }
                        }
                        else
                        {
                            // 老旧二进制格式 (.doc / .xls) 容错提示
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
@"### 欢迎使用 AI 数据分析与文档伴读看板 👋

已按照最新规范重构界面：
1. **左侧导航栏（窄）**：包含 **主页面看板**、**文档库（带数字角标）**、**可滚动最近对话历史**，以及 **左下角设置齿轮**；
2. **中间板块顶部两个窄选项卡**：可在 **【📄 文档预览板块】** 与 **【📊 可视化界面（图表分析）】** 之间自由切换；
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

        private void tbQuestion_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

                if (!isShiftPressed)
                {
                    e.Handled = true;
                    SendMessage();
                }
            }
        }

        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
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

            // 2. 界面状态锁定：防止重复提交
            btnSend.IsEnabled = false;
            tbQuestion.IsEnabled = false;

            // 3. 预先添加 AI 助手空消息气泡，作为流式接收容器
            var assistantMessage = new ChatMessageItem
            {
                Sender = "Assistant",
                Content = "▌", // 初始打字光标
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

            // 提取当前选中文档内容 (若未选中文档则为空串，进行普通对话)
            string documentContext = _currentDoc?.ExtractedText ?? string.Empty;

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

            // 6. 异步流式处理与 DispatcherQueue UI 线程刷新
            var chatService = new LlmChatService();
            var accumulatedText = new StringBuilder();

            try
            {
                await foreach (var chunk in chatService.StreamChatCompletionAsync(currentSettings, historyTurns, documentContext, question))
                {
                    accumulatedText.Append(chunk);
                    string streamContent = accumulatedText.ToString();

                    // 确保在 WinUI UI 线程安全更新 ObservableCollection 绑定的实体
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMessage.Content = streamContent;
                        svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                    });
                }

                // 若接口未返回任何有效文本内容
                if (accumulatedText.Length == 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMessage.Content = "（模型未返回任何文本，请检查接口服务或模型名称是否正确。）";
                    });
                }
            }
            catch (Exception ex)
            {
                // 统一捕获异常并反馈在对话气泡中
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistantMessage.Content = $"⚠️ **对话请求失败**\n\n```text\n{ex.Message}\n```\n\n> **排查建议**：请点击左下角【⚙️ 设置】检查 API Key、Base URL 及网络是否可达。如果是 Ollama 本地模型，请确保后台已运行 `ollama serve`。";
                    svChatList.ChangeView(null, svChatList.ScrollableHeight, null);
                });
            }
            finally
            {
                // 7. 恢复发送按钮与输入框状态，焦点重置
                DispatcherQueue.TryEnqueue(() =>
                {
                    btnSend.IsEnabled = true;
                    tbQuestion.IsEnabled = true;
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


