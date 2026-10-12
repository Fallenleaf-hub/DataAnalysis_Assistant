using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services.Python;

namespace DataAnalysis_Assistant.Services.Tools
{
    /// <summary>
    /// Python 代码执行原生 Agent 工具
    /// 遵循 CodeAct 范式，为模型提供本地数据分析与图表生成执行能力
    /// </summary>
    public class RunPythonTool : IAgentTool
    {
        private readonly PythonExecutionService _execService;
        private readonly SafetyGateService _safetyGate;
        private readonly Func<AppSettings> _getSettings;

        public RunPythonTool(
            Func<AppSettings> getSettings,
            PythonExecutionService? execService = null,
            SafetyGateService? safetyGate = null)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _execService = execService ?? new PythonExecutionService();
            _safetyGate = safetyGate ?? new SafetyGateService();
        }

        public string Name => "run_python";

        public string Description => "在本地受控沙盒中执行 Python 数据分析与绘图代码。支持 pandas、numpy、matplotlib 等常用数据科学库。通过 print(...) 打印聚合指标结论，通过 plt.savefig(...) 生成可视化图表。";

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
                        description = "要在本地执行的完整 Python 代码。必须自包含 import 语句，并使用 print() 输出计算结果。"
                    }
                },
                required = new[] { "code" }
            });

        public bool RequiresUserApproval
        {
            get
            {
                var settings = _getSettings();
                // 若为严格模式(0)，始终需要审批；智能模式(1)则在执行时根据代码内容判定
                return settings.PythonSecurityMode == 0;
            }
        }

        /// <summary>
        /// 判定指定代码是否需要安全审批
        /// </summary>
        public SafetyCheckResult EvaluateSafety(string code)
        {
            var settings = _getSettings();
            return _safetyGate.InspectCode(code, settings.PythonSecurityMode);
        }

        public async Task<ToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
        {
            string code = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElem))
                {
                    code = codeElem.GetString() ?? string.Empty;
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
                // 若处于智能拦截模式且命中危险代码，在未通过交互许可时阻断
                // (后续在 AgentRuntime 主循环中可在此处挂起并触发 UI 审批弹窗)
            }

            // 2. 调用受控执行引擎执行
            var options = new PythonExecutionOptions
            {
                TimeoutSeconds = settings.PythonTimeoutSeconds > 0 ? settings.PythonTimeoutSeconds : 30
            };

            var result = await _execService.ExecuteCodeAsync(code, settings.PythonPath, options, cancellationToken);
            if (!result.Success)
            {
                bool isFileNotFound = (result.ErrorMessage != null && result.ErrorMessage.Contains("FileNotFoundError")) ||
                                      (result.Output != null && result.Output.Contains("FileNotFoundError"));
                if (isFileNotFound)
                {
                    string hint = "\n\n⚠️【物理文件缺失拦截与恢复提示】：Python 执行时触发了 FileNotFoundError，表明引用的源文件在本地磁盘路径上不存在或已被移动/重命名。请提醒用户在左侧【本地文档库】中点击对应文档的【重新定位 🔄】按钮更新文件路径；若无需重新读取物理文件，请改用已由 DocumentReader 缓存的文本或由模型提供推导分析，切勿盲目重复生成相同读取代码。";
                    result.ErrorMessage = (result.ErrorMessage ?? "") + hint;
                }
            }

            return result;
        }
    }
}

