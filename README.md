# Data Analysis Assistant (AI 数据分析助手)

<p align="center">
  <img src="DataAnalysis_Assistant/Assets/Square150x150Logo.scale-200.png" alt="Logo" width="100" height="100"/>
</p>

<p align="center">
  <b>基于 .NET 8 与 WinUI 3 打造的具备自主 Agentic 推演决策、本地安全代码沙盒与多格式文档深度解析的现代 Windows 桌面智能数据分析看板</b>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download/dotnet/8.0"><img src="https://img.shields.io/badge/.NET-8.0-512BD4.svg?style=flat-square&logo=dotnet" alt=".NET 8"/></a>
  <a href="https://learn.microsoft.com/windows/apps/windows-app-sdk/"><img src="https://img.shields.io/badge/Windows_App_SDK-2.5.1-0078D4.svg?style=flat-square&logo=windows" alt="Windows App SDK"/></a>
  <a href="https://learn.microsoft.com/windows/apps/winui/winui3/"><img src="https://img.shields.io/badge/WinUI-3-00A4EF.svg?style=flat-square" alt="WinUI 3"/></a>
  <a href="https://github.com/Fallenleaf-hub/Data_Analysis_Assistant/releases"><img src="https://img.shields.io/github/v/release/Fallenleaf-hub/Data_Analysis_Assistant?style=flat-square&color=orange" alt="Latest Release"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg?style=flat-square" alt="License"/></a>
</p>

---

## 📖 简介

**Data Analysis Assistant** 是一款专为 Windows 10/11 平台打造的本地桌面自主 Agentic 智能数据分析看板。系统深度融合了专业的本地多格式文档解析技术（支持 PDF、Word、Excel 等主流格式）、OpenAI 标准兼容的 LLM 流式对话引擎、以及**全自动自主代码推演与沙盒执行闭环（ReAct / CodeAct）**。大模型可自主编写、调试并执行 Python 科学计算与可视化代码，实时捕获分析图表并沉淀至大屏看板，同时支持多任务批次隔离、终轮强制收敛综合研报与自适应响应式布局，帮助用户高效应对海量数据洞察、数学建模竞赛与复杂报告提炼。

---

## ✨ 核心特性

### 1. 🧠 自主 Agentic 决策推演与代码执行闭环 (CodeAct Engine)
- **多步自主决策循环**：支持模型自主制定推演路径，多步调用本地 Python 执行引擎进行数据清洗、统计分析、相关性计算与图表绘制；
- **终轮强制收敛机制 (Forced Synthesis)**：轮次耗尽或触发熔断时自动切换为终轮汇总状态，强制汇聚全部阶段性成果输出结构化业务研报，绝不半途中断；
- **分级动态超时与重试预算**：基础超时 60s，重型任务自动放宽至 120s；重试独立计数，不可重试硬伤即刻自愈反思，防止死循环耗尽 Token；
- **产物全链路防重与镜像拦截**：解决 Windows 正反斜杠去重缺陷，原地智能保存与外部路径镜像双保险，保证画廊呈现的每幅图表纯净高清。

### 2. 🛡️ 任务批次隔离与长上下文动态净化
- **任务批次标识 (`AgentTaskId`)**：单次提问衍生的所有推理和工具调用统一归属独立批次，避免长历史记忆污染；
- **追问动态净化与图表锚点**：用户追问时自动剥离历史任务中的错误堆栈与调试杂音，保留最终结论并注入 `📊 [已生成图表产物记录]` 事实锚点，避免模型重复探查或重绘；
- **结构化工具审计**：完整持久化每次工具调用的 ToolCallId、参数、执行耗时、成功状态与返回值摘要。

### 3. 📑 多格式文档全自动解析引擎
- **PDF 文档解析**：采用 `PdfPig` 高性能库逐页提取原生版面文字；
- **Word 文档解析**：集成 `DocX` 解析 `.docx` 与 `.doc` 文本段落与结构；
- **Excel 表格提取**：基于 `EPPlus 8` 逐行转录电子表格矩阵数据；
- **纯文本/代码轻量支持**：原生异步解析 `.txt`、`.md`、`.csv`；
- **智能计量与审计**：自动统计提取文本字符数，并按 `字符数 / 4` 联动预估 Token 上下文占用。

### 4. 🎨 现代化自适应响应式交互
- **窗口缩放自适应保护**：内置动态尺寸钳位保护（`MainWindow_SizeChanged`），全屏缩小还原后右侧对话框（清空按钮、发送按钮）永远 100% 完整可见；
- **标题网格化防遮挡**：长文件名与长图表标题自适应省略号截断（`TextTrimming="CharacterEllipsis"`），杜绝与操作按钮发生重叠；
- **双窄选项卡分析看板**：
  - **文档预览 Tab**：富文本及 Markdown 渲染器，实时查看解析后的原始结构；
  - **可视化分析 Tab**：图表历史画廊、高清大图展示、一键导出 Word/Markdown 研报、另存为及复制；
- **半透明微霜浮动胶囊 Toast**：全局仿手机灵动胶囊通知，复制成功即时优雅反馈，且支持鼠标点击穿透。

### 5. 🔒 隐私保护与免安装绿色架构
- **未打包独立部署 (Unpackaged Win32)**：摆脱 Windows UWP/MSIX 沙盒约束，支持单可执行文件独立自包含运行；
- **密钥本地绝对隔离**：API Key 等敏感配置持久化于系统本地 `%APPDATA%\DataAnalysis_Assistant\settings.json`，**绝不上云、绝不包含在 Git 仓库中**；
- **全局参数自主调节**：支持在设置中心自由调节 Agent 决策轮次上限（6~20）、终轮收敛开关以及 Python 执行基础超时时间（30~300s）。

---

## 🛠️ 技术栈与依赖库

| 维度 | 技术选型 | 说明 |
| :--- | :--- | :--- |
| **运行时目标** | `.NET 8.0` (`net8.0-windows10.0.19041.0`) | 现代高性能跨平台 runtime |
| **UI 框架** | `Windows App SDK 2.5.1` + `WinUI 3` | Fluent Design 现代桌面界面体系 |
| **PDF 解析** | `PdfPig 0.1.16` | 跨平台高性能 PDF 文本提取 |
| **Office 解析** | `DocX 5.2.0` / `EPPlus 8.7.1` | Word 文档与 Excel 电子表格高保真读取 |
| **网络与通信** | `System.Net.Http` + `System.Text.Json` | 复用 HttpClient，兼容 OpenAI SSE 流解析 |
| **构建与 CI/CD** | `GitHub Actions` (`windows-latest`) | 自动化编译、打包 Zip 并发布到 Releases |

---

## 🚀 快速开始

### 方式一：直接下载免安装绿色版（推荐）
1. 前往项目 [Releases 页面](https://github.com/Fallenleaf-hub/Data_Analysis_Assistant/releases)；
2. 下载最新版本的 `DataAnalysis_Assistant-vX.Y.Z-win-x64.zip`；
3. 解压到任意文件夹，双击运行 **`DataAnalysis_Assistant.exe`** 即可使用（已自包含运行库，无需预先安装 .NET 运行时）。

### 方式二：从源码编译调试

#### 环境准备
- Windows 10 (版本 1809 及以上) 或 Windows 11
- [Visual Studio 2022](https://visualstudio.microsoft.com/) (版本 17.8 及以上)，并勾选：
  - **.NET 桌面开发**
  - **通用 Windows 平台开发** / **Windows 应用 SDK C# 模板**

#### 克隆与编译
```bash
# 1. 克隆本仓库
git clone https://github.com/Fallenleaf-hub/Data_Analysis_Assistant.git
cd Data_Analysis_Assistant

# 2. 还原 NuGet 依赖
dotnet restore

# 3. 编译未打包 x64 版本
dotnet build -p:Platform=x64
```
在 Visual Studio 中打开 `DataAnalysis_Assistant.slnx`，确保顶部启动项选择 **`DataAnalysis_Assistant (Unpackaged)`**，按 `F5` 即可开始调试。

---

## ⚙️ 模型接入配置示例

在左侧导航栏点击 **「设置」** 即可配置 LLM 连接参数：

### 1. DeepSeek 官方
- **API Key**：`sk-...` (从 DeepSeek 开放平台获取)
- **Base URL**：`https://api.deepseek.com/v1`
- **默认模型**：`deepseek-chat` 或 `deepseek-reasoner`

### 2. OpenAI 官方
- **API Key**：`sk-...`
- **Base URL**：`https://api.openai.com/v1`
- **默认模型**：`gpt-4o`、`gpt-4o-mini`

### 3. 本地 Ollama 离线模型
- **API Key**：任意填写（如 `ollama`）
- **Base URL**：`http://localhost:11434/v1`
- **默认模型**：点击 **「获取」** 按钮即可自动拉取您本地已安装的模型（如 `qwen2.5:7b`、`llama3.1` 等）

---

## 📦 自动化发布流程 (CI/CD)

本项目配置了完整的 GitHub Actions 自动化工作流（[`.github/workflows/release.yml`](.github/workflows/release.yml)）。

当向主分支推送新的语义化版本标签时，云端流水线将全自动完成编译与发布：
```bash
# 提交代码
git add .
git commit -m "feat: 发布新功能"
git push origin master

# 打标签并推送，触发云端全自动构建发布
git tag v1.0.2
git push origin v1.0.2
```
流水线构建完成后，会自动在 GitHub Releases 页面生成对应的发布版本并附带开箱即用的自包含绿色压缩包。

---

## 🗺️ 规划与路线图 (Roadmap)

- [x] **阶段 1**：WinUI 3 极窄导轨布局、抽屉多文档管理与看板框架构建
- [x] **阶段 2**：PDF/Word/Excel/TXT 多格式真实内容解析与 Token 预估
- [x] **阶段 3**：OpenAI/DeepSeek SSE 真实流式对话接入与模型列表在线获取
- [x] **架构优化**：迁移至未打包（Unpackaged）自包含独立部署与 GitHub Actions 自动流水线
- [x] **阶段 4**：本地 Python 科学沙盒执行器，支持大模型多步自主推演、终轮强制收敛与动态分级超时 (CodeAct Engine)
- [x] **阶段 5**：多任务批次隔离 (`AgentTaskId`)、上下文动态净化消歧与全局自适应响应式布局
- [ ] **阶段 6**：多文档本地向量嵌入（RAG）检索与深度长文本知识库切片增强

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源，欢迎自由使用、修改与衍生开发。
