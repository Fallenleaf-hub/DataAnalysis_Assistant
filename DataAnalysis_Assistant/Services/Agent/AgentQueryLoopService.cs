using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services;
using DataAnalysis_Assistant.Services.Tools;

namespace DataAnalysis_Assistant.Services.Agent
{
    /// <summary>
    /// Agent 多步循环状态进度更新包
    /// </summary>
    public class AgentProgressUpdate
    {
        public int CurrentIteration { get; set; }
        public int MaxIterations { get; set; }
        public string Phase { get; set; } = "thinking"; // thinking, executing_tool, evaluating, reflecting, complete
        public string StatusMessage { get; set; } = string.Empty;
        public string? IntermediateReasoning { get; set; }
        public string? IntermediateContent { get; set; }
        public string? ActiveToolName { get; set; }
        public List<string> GeneratedArtifactsSoFar { get; set; } = new();
    }

    /// <summary>
    /// Agent 主循环最终执行成果
    /// </summary>
    public class AgentLoopResult
    {
        public bool Success { get; set; }
        public string FinalContent { get; set; } = string.Empty;
        public string ReasoningContent { get; set; } = string.Empty;
        public int TotalIterations { get; set; }
        public List<string> GeneratedArtifactPaths { get; set; } = new();
        public List<ChatTurn> CompleteTurnHistory { get; set; } = new();
        public List<ToolCallRecordItem> ExecutedToolCalls { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public AgentTerminationReason TerminationReason { get; set; } = AgentTerminationReason.None;
    }

    /// <summary>
    /// ReAct / CodeAct 自主 Agent 核心状态机调度服务 (AgentQueryLoop)
    /// 机制：思考 -> 决策工具 -> 执行工具 -> 评估结果 -> 自主反思自愈 -> 终轮收敛交付
    /// </summary>
    public class AgentQueryLoopService
    {
        private const int PerToolMaxRetry = 2;
        private const int MaxConsecutiveFailures = 3;

        private readonly LlmChatService _chatService;
        private readonly List<IAgentTool> _tools;

        public AgentQueryLoopService(
            LlmChatService? chatService = null,
            IEnumerable<IAgentTool>? tools = null)
        {
            _chatService = chatService ?? new LlmChatService();
            _tools = tools != null ? new List<IAgentTool>(tools) : new List<IAgentTool>();
        }

        /// <summary>
        /// 注册新工具
        /// </summary>
        public void RegisterTool(IAgentTool tool)
        {
            if (tool != null && !_tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _tools.Add(tool);
            }
        }

        /// <summary>
        /// 运行完整的多步自主 Agent 决策与执行主循环
        /// </summary>
        public async Task<AgentLoopResult> RunLoopAsync(
            AppSettings settings,
            List<ChatTurn> initialHistory,
            string documentContext,
            string userQuestion,
            int maxIterations = 12,
            IProgress<AgentProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var loopHistory = new List<ChatTurn>(initialHistory ?? new());
            var allGeneratedArtifacts = new List<string>();
            var accumulatedReasoning = new StringBuilder();
            var failedToolSummaries = new List<string>();
            var executedToolCalls = new List<ToolCallRecordItem>();

            var toolDefs = _tools.Select(t => t.Definition).ToList();

            int iteration = 0;
            int consecutiveFailures = 0;

            while (iteration < maxIterations)
            {
                // 最高优先级：用户手动打断检测
                if (cancellationToken.IsCancellationRequested)
                {
                    return new AgentLoopResult
                    {
                        Success = false,
                        TerminationReason = AgentTerminationReason.UserCancelled,
                        ErrorMessage = "任务已由用户手动停止。",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory,
                        ExecutedToolCalls = executedToolCalls
                    };
                }

                iteration++;

                // 1. 阶段汇报：开始规划与决策
                progress?.Report(new AgentProgressUpdate
                {
                    CurrentIteration = iteration,
                    MaxIterations = maxIterations,
                    Phase = "thinking",
                    StatusMessage = iteration == 1
                        ? "正在拆解任务并规划分析路径..."
                        : $"第 {iteration}/{maxIterations} 轮：正在评估工具执行结果并推导下一步...",
                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                });

                // 2. 调用大模型发起 Tools 请求
                ChatToolResponse response;
                try
                {
                    response = await RequestChatTurnAsync(
                        settings,
                        loopHistory,
                        documentContext,
                        userQuestion,
                        toolDefs,
                        toolChoice: "auto",
                        iteration,
                        maxIterations,
                        allGeneratedArtifacts,
                        accumulatedReasoning,
                        progress,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new AgentLoopResult
                        {
                            Success = false,
                            TerminationReason = AgentTerminationReason.UserCancelled,
                            ErrorMessage = "任务已由用户手动停止。",
                            TotalIterations = iteration,
                            GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                            CompleteTurnHistory = loopHistory
                        };
                    }

                    return new AgentLoopResult
                    {
                        Success = false,
                        TerminationReason = AgentTerminationReason.None,
                        ErrorMessage = $"大模型请求失败: {ex.Message}",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory
                    };
                }

                // 取消令牌二次探测
                if (cancellationToken.IsCancellationRequested)
                {
                    return new AgentLoopResult
                    {
                        Success = false,
                        TerminationReason = AgentTerminationReason.UserCancelled,
                        ErrorMessage = "任务已由用户手动停止。",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory
                    };
                }

                // 累计思考链路
                if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
                {
                    if (accumulatedReasoning.Length > 0) accumulatedReasoning.AppendLine("\n--- 轮次推导 ---\n");
                    accumulatedReasoning.Append(response.ReasoningContent);
                }

                // 3. 检查是否有工具调用
                if (response.HasToolCalls)
                {
                    // 记录 assistant 发起的 tool_calls 回合
                    loopHistory.Add(new ChatTurn
                    {
                        Role = "assistant",
                        Content = response.Content,
                        ToolCalls = response.ToolCalls
                    });

                    bool anyToolFailed = false;

                    // 逐一受控执行工具
                    foreach (var tc in response.ToolCalls)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        string fnName = tc.Function.Name;
                        string fnArgs = tc.Function.Arguments;

                        accumulatedReasoning.AppendLine($"\n> 🛠️ **[Agent 工具决策]** 轮次 {iteration}/{maxIterations}：调用 `{fnName}`");

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "executing_tool",
                            ActiveToolName = fnName,
                            StatusMessage = $"第 {iteration}/{maxIterations} 轮：正在执行工具 [{fnName}]...",
                            GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                            IntermediateReasoning = accumulatedReasoning.ToString()
                        });

                        var toolInstance = _tools.FirstOrDefault(t => t.Name.Equals(fnName, StringComparison.OrdinalIgnoreCase));
                        ToolExecutionResult execResult;

                        if (toolInstance != null)
                        {
                            try
                            {
                                execResult = await toolInstance.ExecuteAsync(fnArgs, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                execResult = ToolExecutionResult.Failed($"工具执行抛出未处理异常: {ex.Message}");
                            }
                        }
                        else
                        {
                            execResult = ToolExecutionResult.Failed($"未找到名为 '{fnName}' 的工具。当前支持的工具: {string.Join(", ", _tools.Select(t => t.Name))}");
                        }

                        if (cancellationToken.IsCancellationRequested) break;

                        // 收集生成的产物 (图表等)
                        if (execResult.GeneratedArtifacts.Count > 0)
                        {
                            foreach (var art in execResult.GeneratedArtifacts)
                            {
                                if (!allGeneratedArtifacts.Contains(art))
                                {
                                    allGeneratedArtifacts.Add(art);
                                }
                            }
                        }

                        if (!execResult.Success)
                        {
                            anyToolFailed = true;
                            string truncatedErr = TruncateErrorForPrompt(execResult.ErrorMessage);
                            failedToolSummaries.Add($"[{fnName}] {truncatedErr}");

                            bool isNonRetryable = IsNonRetryableError(execResult.ErrorMessage);
                            if (isNonRetryable)
                            {
                                accumulatedReasoning.AppendLine($"> ⚠️ **[工具执行异常 · 语法/路径硬伤]**：{execResult.ErrorMessage} (不可重试，已记录未决项)");
                            }
                            else
                            {
                                accumulatedReasoning.AppendLine($"> ⚠️ **[工具执行异常]**：{execResult.ErrorMessage} (触发受控反思自愈)");
                            }
                        }
                        else
                        {
                            string artNote = execResult.GeneratedArtifacts.Count > 0 ? $" (生成 {execResult.GeneratedArtifacts.Count} 幅图表)" : string.Empty;
                            accumulatedReasoning.AppendLine($"> ✅ **[工具执行成功]**{artNote}");
                        }

                        // 格式化工具输出并回填至上下文 (包含保真事实标记)
                        string toolResultContent = execResult.Success
                            ? (string.IsNullOrWhiteSpace(execResult.Output) ? "工具执行成功，但无文本输出。" : execResult.Output)
                            : $"[工具执行异常 Traceback/Error]\n{execResult.ErrorMessage}\n部分输出: {execResult.Output}\n提示：请结合报错信息自主反思代码或参数问题，修正后再次尝试。";

                        loopHistory.Add(new ChatTurn
                        {
                            Role = "tool",
                            ToolCallId = tc.Id,
                            Content = toolResultContent
                        });

                        // 结构化落盘记录
                        executedToolCalls.Add(new ToolCallRecordItem
                        {
                            CallId = tc.Id,
                            ToolName = fnName,
                            Arguments = fnArgs,
                            Output = execResult.Output,
                            Success = execResult.Success,
                            ArtifactPaths = new List<string>(execResult.GeneratedArtifacts),
                            Timestamp = DateTime.Now
                        });
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new AgentLoopResult
                        {
                            Success = false,
                            TerminationReason = AgentTerminationReason.UserCancelled,
                            ErrorMessage = "任务已由用户手动停止。",
                            TotalIterations = iteration,
                            GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                            CompleteTurnHistory = loopHistory,
                            ExecutedToolCalls = executedToolCalls
                        };
                    }

                    // 4. 自主反思与错误计数
                    if (anyToolFailed)
                    {
                        consecutiveFailures++;
                        accumulatedReasoning.AppendLine($"> 🔄 **[自主反思中]** 正在调整代码与参数并规划自愈路径 (失败重试 {consecutiveFailures}/{MaxConsecutiveFailures})...");

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "reflecting",
                            StatusMessage = $"第 {iteration}/{maxIterations} 轮：检测到执行异常，Agent 正在自主反思调整代码...",
                            GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                            IntermediateReasoning = accumulatedReasoning.ToString()
                        });

                        if (consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            // 达到连续报错上限，安全熔断，立即触发终轮强制合成！
                            return await ExecuteFinalSynthesisTurnAsync(
                                settings,
                                loopHistory,
                                documentContext,
                                userQuestion,
                                iteration,
                                maxIterations,
                                allGeneratedArtifacts,
                                accumulatedReasoning,
                                failedToolSummaries,
                                executedToolCalls,
                                AgentTerminationReason.ConsecutiveErrorsTripped,
                                progress,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        consecutiveFailures = 0;
                    }

                    // 状态 A：工具刚执行完毕触顶！
                    // 核心修复：此时已经跑满了 maxIterations，绝对不能跳出返回死字符串，必须给予模型一次终轮总结的机会！
                    if (iteration >= maxIterations)
                    {
                        return await ExecuteFinalSynthesisTurnAsync(
                            settings,
                            loopHistory,
                            documentContext,
                            userQuestion,
                            iteration,
                            maxIterations,
                            allGeneratedArtifacts,
                            accumulatedReasoning,
                            failedToolSummaries,
                            executedToolCalls,
                            AgentTerminationReason.MaxIterationsReached,
                            progress,
                            cancellationToken);
                    }

                    // 继续下一轮迭代决策
                    continue;
                }
                else
                {
                    // 5. 模型输出最终正文结论（无工具调用），主循环正常终止！
                    progress?.Report(new AgentProgressUpdate
                    {
                        CurrentIteration = iteration,
                        MaxIterations = maxIterations,
                        Phase = "complete",
                        StatusMessage = "分析完毕，已生成综合业务研报。",
                        IntermediateContent = response.Content,
                        GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                    });

                    return new AgentLoopResult
                    {
                        Success = true,
                        TerminationReason = AgentTerminationReason.NormalCompleted,
                        FinalContent = response.Content,
                        ReasoningContent = accumulatedReasoning.ToString(),
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory,
                        ExecutedToolCalls = executedToolCalls
                    };
                }
            }

            // 状态 B：尚未发起工具即触顶（防御兜底）
            return await ExecuteFinalSynthesisTurnAsync(
                settings,
                loopHistory,
                documentContext,
                userQuestion,
                iteration,
                maxIterations,
                allGeneratedArtifacts,
                accumulatedReasoning,
                failedToolSummaries,
                executedToolCalls,
                AgentTerminationReason.MaxIterationsReached,
                progress,
                cancellationToken);
        }

        /// <summary>
        /// 终轮强制合成：彻底杜绝丢弃已执行工具结果，统一汇聚图表与数据输出专业研报
        /// </summary>
        private async Task<AgentLoopResult> ExecuteFinalSynthesisTurnAsync(
            AppSettings settings,
            List<ChatTurn> loopHistory,
            string? documentContext,
            string? userQuestion,
            int iteration,
            int maxIterations,
            List<string> allGeneratedArtifacts,
            StringBuilder accumulatedReasoning,
            List<string> failedToolSummaries,
            List<ToolCallRecordItem> executedToolCalls,
            AgentTerminationReason terminationReason,
            IProgress<AgentProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new AgentLoopResult
                {
                    Success = false,
                    TerminationReason = AgentTerminationReason.UserCancelled,
                    ErrorMessage = "任务已由用户手动停止。",
                    TotalIterations = iteration,
                    GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                    CompleteTurnHistory = loopHistory,
                    ExecutedToolCalls = executedToolCalls
                };
            }

            // 用户预算保护开关判定
            if (!settings.EnableAutoFinalSynthesis)
            {
                string budgetOffMsg = $"已达到最大决策轮次上限（共 {iteration} 轮）。已完成阶段性演算与分析。(终轮自动汇总研报已在设置中关闭)";
                progress?.Report(new AgentProgressUpdate
                {
                    CurrentIteration = iteration,
                    MaxIterations = maxIterations,
                    Phase = "complete",
                    StatusMessage = budgetOffMsg,
                    IntermediateContent = budgetOffMsg,
                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                });

                return new AgentLoopResult
                {
                    Success = true,
                    TerminationReason = terminationReason,
                    FinalContent = budgetOffMsg,
                    ReasoningContent = accumulatedReasoning.ToString(),
                    TotalIterations = iteration,
                    GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                    CompleteTurnHistory = loopHistory,
                    ExecutedToolCalls = executedToolCalls
                };
            }

            progress?.Report(new AgentProgressUpdate
            {
                CurrentIteration = iteration,
                MaxIterations = maxIterations,
                Phase = "thinking",
                StatusMessage = terminationReason == AgentTerminationReason.ConsecutiveErrorsTripped
                    ? "触发安全熔断保护，正在汇总已知阶段性数据与图表成果..."
                    : $"已达决策上限 ({iteration}/{maxIterations} 轮)，正在汇总研报并解读全部图表...",
                GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                IntermediateReasoning = accumulatedReasoning.ToString()
            });

            // 注入终轮强制收敛指令
            string synthesisPrompt = BuildFinalSynthesisPrompt(failedToolSummaries.Count > 0, failedToolSummaries, allGeneratedArtifacts);
            loopHistory.Add(new ChatTurn
            {
                Role = "system",
                Content = synthesisPrompt
            });

            ChatToolResponse finalResponse;
            try
            {
                // 终轮强制设定 toolChoice = "none"，且工具列表置空
                finalResponse = await RequestChatTurnAsync(
                    settings,
                    loopHistory,
                    documentContext,
                    userQuestion,
                    toolDefs: Enumerable.Empty<ToolDefinition>(),
                    toolChoice: "none",
                    iteration,
                    maxIterations,
                    allGeneratedArtifacts,
                    accumulatedReasoning,
                    progress,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new AgentLoopResult
                    {
                        Success = false,
                        TerminationReason = AgentTerminationReason.UserCancelled,
                        ErrorMessage = "任务已由用户手动停止。",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory,
                        ExecutedToolCalls = executedToolCalls
                    };
                }

                // 若终轮大模型请求因网络抖动等原因失败，给出高质量保真兜底
                string fallbackText = $"### 📊 业务分析与演算成果汇报 (兜底汇总)\n\n本次分析已到达最大规划决策轮次（共 {iteration} 轮）。已成功生成并捕获 **{allGeneratedArtifacts.Count}** 幅分析图表至右侧画廊与消息卡片，请参考上方图表与计算指标展开业务决策。\n\n*(注：终轮合成请求出现暂态网络波动: {ex.Message})*";
                return new AgentLoopResult
                {
                    Success = true,
                    TerminationReason = terminationReason,
                    FinalContent = fallbackText,
                    ReasoningContent = accumulatedReasoning.ToString(),
                    TotalIterations = iteration,
                    GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                    CompleteTurnHistory = loopHistory,
                    ExecutedToolCalls = executedToolCalls
                };
            }

            // 核心代码层防御：坚决丢弃任何残留的 tool_calls
            if (finalResponse.HasToolCalls)
            {
                finalResponse.ToolCalls.Clear();
            }

            if (!string.IsNullOrWhiteSpace(finalResponse.ReasoningContent))
            {
                if (accumulatedReasoning.Length > 0) accumulatedReasoning.AppendLine("\n--- 终轮收敛推导 ---\n");
                accumulatedReasoning.Append(finalResponse.ReasoningContent);
            }

            string finalMarkdown = finalResponse.Content;
            if (string.IsNullOrWhiteSpace(finalMarkdown))
            {
                finalMarkdown = $"### 📊 业务数据分析综合研报\n\n本次多步分析推导已全部完成（共执行 {iteration} 轮 Agent 决策）。\n系统已全量采集并挂接 **{allGeneratedArtifacts.Count}** 组图表成果至右侧画廊与卡片，所有指标与明细已在上方计算完毕，请结合图表展开决策。";
            }

            progress?.Report(new AgentProgressUpdate
            {
                CurrentIteration = iteration,
                MaxIterations = maxIterations,
                Phase = "complete",
                StatusMessage = "分析完毕，已生成综合业务研报。",
                IntermediateContent = finalMarkdown,
                GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
            });

            return new AgentLoopResult
            {
                Success = true,
                TerminationReason = terminationReason,
                FinalContent = finalMarkdown,
                ReasoningContent = accumulatedReasoning.ToString(),
                TotalIterations = iteration,
                GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                CompleteTurnHistory = loopHistory,
                ExecutedToolCalls = executedToolCalls
            };
        }

        /// <summary>
        /// 终轮收敛提示词动态组装
        /// </summary>
        private static string BuildFinalSynthesisPrompt(bool hasFailedTools, List<string> failedToolSummaries, List<string> generatedArtifacts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【系统终轮强制收敛指令 (Final Synthesis)】：");
            sb.AppendLine("当前任务已到达最终汇报决策轮次。严禁再次调用任何工具或运行代码！");

            if (hasFailedTools && failedToolSummaries.Count > 0)
            {
                sb.AppendLine("说明：在探索演算过程中，以下部分步骤遭遇客观异常：");
                foreach (var err in failedToolSummaries.Take(3))
                {
                    sb.AppendLine($"- {err}");
                }
                sb.AppendLine("请务必：");
                sb.AppendLine("1. 严禁再次发起工具调用，直接输出专业完备的 Markdown 业务分析研报；");
                sb.AppendLine("2. 系统性汇总目前已经成功计算出的全部核心事实数据、统计指标与趋势；");
                if (generatedArtifacts.Count > 0)
                {
                    sb.AppendLine($"3. 结合已成功生成的 {generatedArtifacts.Count} 组图表成果进行深入业务解读与决策建议；");
                }
                sb.AppendLine("4. 对上述遇到异常或超时的未决项，如实向用户客观说明困难并给出后续建议。");
            }
            else
            {
                sb.AppendLine("请务必：");
                sb.AppendLine("1. 严禁再次发起工具调用，直接输出条理清晰、专业深刻的最终 Markdown 业务研报；");
                sb.AppendLine("2. 汇总整理全部分析成果，呈现计算出的 Ground-Truth 核心指标；");
                if (generatedArtifacts.Count > 0)
                {
                    sb.AppendLine($"3. 结合已成功生成的全部分析图表进行深入洞察与结论阐述。");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 异常硬伤分类识别（区分可重试与不可重试错误）
        /// </summary>
        private static bool IsNonRetryableError(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage)) return false;

            // 1. 结构化标记识别
            if (errorMessage.Contains("[ERR_TYPE:FileNotFound]") ||
                errorMessage.Contains("[ERR_TYPE:SyntaxError]") ||
                errorMessage.Contains("[ERR_TYPE:ModuleNotFound]") ||
                errorMessage.Contains("[ERR_TYPE:KeyError]"))
            {
                return true;
            }

            // 2. 经典 Python/系统硬性错误特征
            string lower = errorMessage.ToLowerInvariant();
            return lower.Contains("filenotfounderror") ||
                   lower.Contains("modulenotfounderror") ||
                   lower.Contains("syntaxerror") ||
                   lower.Contains("indentationerror") ||
                   lower.Contains("未找到名为") ||
                   lower.Contains("no such file or directory");
        }

        /// <summary>
        /// 精简错误摘要（防止超长 Traceback 撑爆收敛 Prompt）
        /// </summary>
        private static string TruncateErrorForPrompt(string? errorMessage, int maxChars = 150)
        {
            if (string.IsNullOrWhiteSpace(errorMessage)) return "未知错误";
            string trimmed = errorMessage.Trim();
            var lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string firstLine = lines.Length > 0 ? lines[0] : trimmed;
            if (firstLine.Length > maxChars)
            {
                return firstLine.Substring(0, maxChars) + "...";
            }
            return firstLine;
        }

        /// <summary>
        /// 执行单轮大模型请求：默认开启 SSE 流式推流并实时上报打字与思考，同时具备向阻塞式请求的静默降级兜底能力
        /// </summary>
        private async Task<ChatToolResponse> RequestChatTurnAsync(
            AppSettings settings,
            List<ChatTurn> loopHistory,
            string? documentContext,
            string? userQuestion,
            IEnumerable<ToolDefinition> toolDefs,
            string toolChoice,
            int iteration,
            int maxIterations,
            List<string> allGeneratedArtifacts,
            StringBuilder accumulatedReasoning,
            IProgress<AgentProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var curTurnContent = new StringBuilder();
            var curTurnReasoning = new StringBuilder();
            var toolCalls = new List<ToolCallItem>();
            bool streamSucceeded = false;

            try
            {
                await foreach (var chunk in _chatService.StreamChatCompletionWithToolsAsync(
                    settings,
                    loopHistory,
                    documentContext,
                    userQuestion,
                    toolDefs,
                    toolChoice: toolChoice,
                    cancellationToken: cancellationToken))
                {
                    if (chunk.Type == StreamChunkType.Reasoning)
                    {
                        curTurnReasoning.Append(chunk.Text);

                        string combinedReasoning = accumulatedReasoning.ToString();
                        if (curTurnReasoning.Length > 0)
                        {
                            combinedReasoning += (combinedReasoning.Length > 0 ? "\n" : "") + curTurnReasoning.ToString();
                        }

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "thinking",
                            StatusMessage = iteration == 1
                                ? "正在拆解任务并规划分析路径..."
                                : $"第 {iteration}/{maxIterations} 轮：正在评估工具执行结果并推导下一步...",
                            IntermediateReasoning = combinedReasoning,
                            IntermediateContent = curTurnContent.ToString(),
                            GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                        });
                    }
                    else if (chunk.Type == StreamChunkType.Content)
                    {
                        curTurnContent.Append(chunk.Text);

                        string combinedReasoning = accumulatedReasoning.ToString();
                        if (curTurnReasoning.Length > 0)
                        {
                            combinedReasoning += (combinedReasoning.Length > 0 ? "\n" : "") + curTurnReasoning.ToString();
                        }

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "streaming",
                            StatusMessage = "正在生成回答与数据洞察...",
                            IntermediateReasoning = combinedReasoning,
                            IntermediateContent = curTurnContent.ToString(),
                            GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                        });
                    }
                    else if (chunk.Type == StreamChunkType.ToolCall && chunk.ToolCall != null)
                    {
                        toolCalls.Add(chunk.ToolCall);
                    }
                }

                streamSucceeded = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 若流式接口在部分特殊代理或本地模型上遇阻，静默降级回退至非流式请求保障可用性
                streamSucceeded = false;
            }

            if (streamSucceeded)
            {
                return new ChatToolResponse
                {
                    Content = curTurnContent.ToString(),
                    ReasoningContent = curTurnReasoning.Length > 0 ? curTurnReasoning.ToString() : string.Empty,
                    ToolCalls = toolCalls
                };
            }

            // 兜底降级至标准非流式请求
            return await _chatService.SendChatWithToolsAsync(
                settings,
                loopHistory,
                documentContext,
                userQuestion,
                toolDefs,
                toolChoice: toolChoice,
                cancellationToken: cancellationToken);
        }
    }
}
