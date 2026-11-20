using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services.Python;

namespace DataAnalysis_Assistant.Services.Tools
{
    public class RunPythonTool : IAgentTool
    {
        private readonly PythonExecutionService _execService;
        private readonly SafetyGateService _safetyGate;
        private readonly Func<AppSettings> _getSettings;
        private readonly Func<string?>? _getActiveGroupId;

        public RunPythonTool(
            Func<AppSettings> getSettings,
            PythonExecutionService? execService = null,
            SafetyGateService? safetyGate = null,
            Func<string?>? getActiveGroupId = null)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _execService = execService ?? new PythonExecutionService();
            _safetyGate = safetyGate ?? new SafetyGateService();
            _getActiveGroupId = getActiveGroupId;
        }

        public string Name => "run_python";

        public string Description => "在本地受控沙盒中执行 Python 数据分析与绘图代码。支持 pandas、numpy、matplotlib 等常用数据科学库。通过 print(...) 打印聚合指标结论；绘图请直接调用 plt.savefig('xxx.png') 或 fig.savefig('xxx.png')。系统已挂载全局自动捕获代理，所有生成的图表均会自动在界面画廊中展示，严禁在后续的 Markdown 正文回答中以 ![...](xxx.png) 语法引用图片链接，避免客户端相对路径断链。";

        public ToolDefinition Definition => ToolDefinition.CreateFunction(
            Name,
            Description,
            new
            {
                type = "object",
                properties = new
                {
                    code = new
                    {
                        type = "string",
                        description = "要在本地执行的完整 Python 代码。必须自包含 import 语句，并使用 print() 输出计算结果。绘图直接保存为相对路径 png，系统会自动捕获并在图表画廊中渲染，严禁在 Markdown 正文中内嵌图片语法。"
                    },
                    heavy_work = new
                    {
                        type = "boolean",
                        description = "可选。若代码涉及多文件批量读取（如多个大 Excel/CSV）或高密度绘图（10,000+ 数据点）预计耗时较长，请设为 true 以申请扩展超时执行窗口。"
                    }
                },
                required = new[] { "code" }
            });

        public bool RequiresUserApproval
        {
            get
            {
                var settings = _getSettings();
                return settings.PythonSecurityMode == 0;
            }
        }

        /// 判定指定代码是否需要安全审批
        public SafetyCheckResult EvaluateSafety(string code)
        {
            var settings = _getSettings();
            return _safetyGate.InspectCode(code, settings.PythonSecurityMode);
        }

        public async Task<ToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
        {
            string code = string.Empty;
            bool isHeavyWork = false;
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElem))
                {
                    code = codeElem.GetString() ?? string.Empty;
                }
                if (doc.RootElement.TryGetProperty("heavy_work", out var heavyElem) && heavyElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isHeavyWork = heavyElem.GetBoolean();
                }
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.CreateFailure($"解析工具调用参数 JSON 失败: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolExecutionResult.CreateFailure("执行失败：传入的 Python code 为空。");
            }

            var settings = _getSettings();

            // 1. 安全刹车检查
            var safetyCheck = _safetyGate.InspectCode(code, settings.PythonSecurityMode);
            if (!safetyCheck.IsAllowedAutoRun && settings.PythonSecurityMode == 1)
            {
            }

            int baseTimeout = Math.Clamp(settings.PythonTimeoutSeconds, 30, 120);
            int effectiveTimeout = isHeavyWork ? Math.Clamp(Math.Max(baseTimeout, 90), 30, 120) : baseTimeout;

            var options = new PythonExecutionOptions
            {
                TimeoutSeconds = effectiveTimeout,
                IsHeavyWork = isHeavyWork,
                TargetGroupId = _getActiveGroupId?.Invoke()
            };

            var result = await _execService.ExecuteCodeAsync(code, settings.PythonPath, options, cancellationToken);
            if (!result.Success)
            {
                bool isFileNotFound = (result.ErrorMessage != null && result.ErrorMessage.Contains("FileNotFoundError")) ||
                                      (result.Output != null && result.Output.Contains("FileNotFoundError"));
                if (isFileNotFound)
                {
                    string hint = "\n\n【物理文件缺失拦截与恢复提示】：Python 执行时触发了 FileNotFoundError，表明引用的源文件在本地磁盘路径上不存在或已被移动/重命名。请提醒用户在左侧【本地文档库】中点击对应文档的【重新定位】按钮更新文件路径；若无需重新读取物理文件，请改用已由 DocumentReader 缓存的文本或由模型提供推导分析，切勿盲目重复生成相同读取代码。";
                    result.ErrorMessage = (result.ErrorMessage ?? "") + hint;
                }

                // 4. 依赖缺失终端命令引导（基于真实 PythonPath）
                bool isModuleNotFound = (result.ErrorMessage != null && result.ErrorMessage.Contains("ModuleNotFoundError")) ||
                                        (result.Output != null && result.Output.Contains("ModuleNotFoundError"));
                if (isModuleNotFound)
                {
                    string pythonInterpreter = string.IsNullOrWhiteSpace(settings.PythonPath) ? "python" : settings.PythonPath;
                    string pkgHint = $"\n\n【Python 模块依赖缺失】：当前执行环境缺少第三方依赖包。请引导用户打开本机终端执行以下命令进行安装：\n`\"{pythonInterpreter}\" -m pip install <缺失包名>`\n若存在同类替代方案（如使用标准库），请优先调整代码使用现有环境已安装的包。";
                    result.ErrorMessage = (result.ErrorMessage ?? "") + pkgHint;
                }
            }

            return result;
        }
    }
}
