# Data Analysis Assistant (AI 数据分析助手)

<p align="center">
  <img src="Data_Analysis/Assets/Square150x150Logo.scale-200.png" alt="Logo" width="100" height="100"/>
</p>

<p align="center">
  <b>基于 .NET 8 与 WinUI 3 打造的现代 Windows 本地多格式文档解析与大语言模型伴读分析客户端</b>
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

**Data Analysis Assistant** 是一款专为 Windows 10/11 平台打造的本地桌面伴读与智能数据分析看板。系统融合了专业的本地多格式文档解析技术（支持 PDF、Word、Excel 等主流格式）与 OpenAI 标准兼容的 LLM 流式对话引擎，帮助用户在本地直观预览文件内容、提炼关键指标、自动统计 Token 消耗，并通过大语言模型对复杂数据进行深度剖析、结构化总结与多维可视化。

---

## ✨ 核心特性

### 1. 📑 多格式文档全自动解析引擎
- **PDF 文档解析**：采用 `PdfPig` 高性能库逐页提取原生版面文字；
- **Word 文档解析**：集成 `DocX` 解析 `.docx` 与 `.doc` 文本段落与结构；
- **Excel 表格提取**：基于 `EPPlus 8` 逐行转录电子表格矩阵数据；
- **纯文本/代码轻量支持**：原生异步解析 `.txt`、`.md`、`.csv`；
- **智能计量与审计**：自动统计提取文本字符数，并按 `字符数 / 4` 联动预估 Token 上下文占用。

### 2. ⚡ 兼容 OpenAI 协议的流式对话服务
- **标准化 SSE 流式渲染**：采用异步流（`IAsyncEnumerable<string>`）实现实时打字机逐字渲染；
- **全服务商兼容**：原生兼容 **OpenAI** (GPT-4o/o1)、**DeepSeek** (Chat/Reasoner)、**Claude**、**Ollama**（本地离线模型）及各类兼容代理中转；
- **文档上下文精准注入**：自动提取当前选中文档内容，作为 System Context 无缝注入提示词对话链；
- **在线模型智能探测**：支持一键向 Base URL 的 `/models` 端点拉取当前 API Key 支持的可用模型列表，并支持自定义手工输入。

### 3. 🎨 现代化交互与视觉设计
- **64px 极窄侧边导航轨**：提供纯净视觉，支持秒级切换主工作看板、历史记录、文档管理与系统设置；
- **左侧非模态抽屉栏**：会话列表与多文档库秒级滑出，支持多文档增删查改与状态跟踪；
- **双窄选项卡分析看板**：
  - **文档预览 Tab**：富文本及 Markdown 渲染器，实时查看解析后的原始结构；
  - **可视化分析 Tab**：条形图/柱状图动态渲染，数据指标对比一目了然；
- **ChatGPT 风格分栏设置中心**：LLM 接口配置、Skill 技能插件库、关于与版本检查模块化分离。

### 4. 🔒 隐私保护与免安装绿色架构
- **未打包独立部署 (Unpackaged Win32)**：摆脱 Windows UWP/MSIX 沙盒约束，支持单可执行文件独立自包含运行；
- **密钥本地绝对隔离**：API Key 等敏感配置持久化于系统本地 `%APPDATA%\Data_Analysis\settings.json`，**绝不上云、绝不包含在 Git 仓库中**。

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
2. 下载最新版本的 `Data_Analysis-vX.Y.Z-win-x64.zip`；
3. 解压到任意文件夹，双击运行 **`Data_Analysis.exe`** 即可使用（已自包含运行库，无需预先安装 .NET 运行时）。

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
在 Visual Studio 中打开 `Data_Analysis.slnx`，确保顶部启动项选择 **`Data_Analysis (Unpackaged)`**，按 `F5` 即可开始调试。

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
- [ ] **阶段 4**：本地 Python 沙盒执行器，支持模型自主运行 Pandas/Matplotlib 生成动态图表
- [ ] **阶段 5**：多文档向量嵌入（RAG）检索与大文档长文本切片知识库

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源，欢迎自由使用、修改与衍生开发。
