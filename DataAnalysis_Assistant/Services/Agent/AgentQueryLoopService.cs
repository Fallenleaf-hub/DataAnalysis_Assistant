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
    public class AgentProgressUpdate
    {
        public int CurrentIteration { get; set; }
        public int MaxIterations { get; set; }
        public string Phase { get; set; } = "thinking";
        public string StatusMessage { get; set; } = string.Empty;
        public string? IntermediateReasoning { get; set; }
        public string? IntermediateContent { get; set; }
        public string? ActiveToolName { get; set; }
        public string? SafetyBlockReason { get; set; }
        public List<string> GeneratedArtifactsSoFar { get; set; } = new();
    }

    public class AgentLoopResult
    {
        public bool Success { get; set; }
        public string FinalContent { get; set; } = string.Empty;
        public string ReasoningContent { get; set; } = string.Empty;
        public int TotalIterations { get; set; }
        public List<string> GeneratedArtifactPaths { get; set; } = new();
        public List<ChatTurn> CompleteTurnHistory { get; set; } = new();
        public List<ToolCallRecordItem> ExecutedToolCalls { get; set; } = new();
        public List<string> SafetyBlockNotices { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public AgentTerminationReason TerminationReason { get; set; } = AgentTerminationReason.None;
        public bool IsStreamTruncated { get; set; } = false;
    }

    public class AgentQueryLoopService
    {
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

        public void RegisterTool(IAgentTool tool)
        {
            if (tool != null && !_tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _tools.Add(tool);
            }
        }

        public async Task<AgentLoopResult> RunLoopAsync(
            AppSettings settings,
            List<ChatTurn> initialHistory,
            string documentContext,
            string userQuestion,
            int maxIterations = 12,
            string? activeSkillInstruction = null,
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
            int currentToolRetryCount = 0;
            int totalSelfHealingRetries = 0;
            const int MaxGlobalSelfHealingRetries = 6;
            bool isLoopStreamTruncated = false;
            int safetyBlockCount = 0;
            var safetyBlockNotices = new List<string>();

            while (iteration < maxIterations)
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
                        ExecutedToolCalls = executedToolCalls,
                        SafetyBlockNotices = new List<string>(safetyBlockNotices),
                        IsStreamTruncated = isLoopStreamTruncated
                    };
                }

                if (currentToolRetryCount == 0)
                {
                    iteration++;
                }

                int artifactCountStart = allGeneratedArtifacts.Count;

                progress?.Report(new AgentProgressUpdate
                {
                    CurrentIteration = iteration,
                    MaxIterations = maxIterations,
                    Phase = "thinking",
                    StatusMessage = iteration == 1
                        ? "正在拆解任务并规划分析路径..."
                        : currentToolRetryCount > 0
                            ? $"第 {iteration}/{maxIterations} 轮 (自愈重试中)：正在根据报错调整代码与参数..."
                            : $"第 {iteration}/{maxIterations} 轮：正在评估工具执行结果并推导下一步...",
                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                });

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
                        activeSkillInstruction,
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
                            ExecutedToolCalls = executedToolCalls,
                            SafetyBlockNotices = new List<string>(safetyBlockNotices)
                        };
                    }

                    return new AgentLoopResult
                    {
                        Success = false,
                        TerminationReason = AgentTerminationReason.None,
                        ErrorMessage = $"大模型请求失败: {ex.Message}",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory,
                        ExecutedToolCalls = executedToolCalls,
                        SafetyBlockNotices = new List<string>(safetyBlockNotices)
                    };
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
                        ExecutedToolCalls = executedToolCalls,
                        SafetyBlockNotices = new List<string>(safetyBlockNotices)
                    };
                }

                if (response.IsStreamTruncated)
                {
                    isLoopStreamTruncated = true;
                }

                if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
                {
                    if (accumulatedReasoning.Length > 0) accumulatedReasoning.AppendLine("\n--- 轮次推导 ---\n");
                    accumulatedReasoning.Append(response.ReasoningContent);
                }

                if (response.HasToolCalls)
                {
                    loopHistory.Add(new ChatTurn
                    {
                        Role = "assistant",
                        Content = response.Content,
                        ToolCalls = response.ToolCalls
                    });

                    bool anyToolFailed = false;

                    var turnErrors = new List<string>();
                    foreach (var tc in response.ToolCalls)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        string fnName = tc.Function.Name;
                        string fnArgs = tc.Function.Arguments;

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
                            if (!string.IsNullOrWhiteSpace(execResult.ErrorMessage))
                            {
                                turnErrors.Add(execResult.ErrorMessage);
                            }
                            string truncatedErr = TruncateErrorForPrompt(execResult.ErrorMessage);
                            failedToolSummaries.Add($"[{fnName}] {truncatedErr}");

                            if (execResult.IsSafetyBlocked)
                            {
                                safetyBlockCount++;
                                string blockReason = !string.IsNullOrWhiteSpace(execResult.SafetyBlockReason)
                                    ? execResult.SafetyBlockReason!
                                    : (execResult.ErrorMessage ?? "检测到高危系统操作，已拒绝执行该段 Python 代码。");
                                if (!safetyBlockNotices.Contains(blockReason))
                                {
                                    safetyBlockNotices.Add(blockReason);
                                }

                                progress?.Report(new AgentProgressUpdate
                                {
                                    CurrentIteration = iteration,
                                    MaxIterations = maxIterations,
                                    Phase = "safety_blocked",
                                    ActiveToolName = fnName,
                                    SafetyBlockReason = blockReason,
                                    StatusMessage = $"第 {iteration}/{maxIterations} 轮：已拦截高危 Python 代码",
                                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                                    IntermediateReasoning = accumulatedReasoning.ToString()
                                });
                            }
                        }

                        string toolResultContent;
                        if (execResult.Success)
                        {
                            string baseOutput = string.IsNullOrWhiteSpace(execResult.Output) ? "工具执行成功，但无文本输出。" : execResult.Output;
                            if (execResult.GeneratedArtifacts.Count > 0)
                            {
                                string artList = string.Join(", ", execResult.GeneratedArtifacts.Select(Path.GetFileName));
                                toolResultContent = $"{baseOutput}\n\n[ARTIFACT_CONFIRMED]: 分析图表 ({artList}) 已成功生成并由系统前端画廊与卡片安全接管展示。该可视化目标已 100% 达成，严禁因微小排版偏好推翻重绘相同图表，请直接结合已有图表成果与计算指标撰写最终研报结论。";
                            }
                            else
                            {
                                toolResultContent = baseOutput;
                            }
                        }
                        else if (execResult.IsSafetyBlocked)
                        {
                            toolResultContent = $"[安全策略硬拦截 - 拒绝执行]\n{execResult.ErrorMessage}\n指令约束：系统禁止执行系统命令行、外部子进程、删除文件/目录、注册表修改或底层 Socket 等高危代码。如果用户需求可以通过纯数据分析与绘图（pandas/numpy/matplotlib）安全完成，请立即改写为不含高危操作的安全代码重新尝试；如果用户需求本身要求执行上述高危系统操作，请勿再次调用工具，直接用清晰的文字向用户解释被拦截的原因。";
                        }
                        else
                        {
                            toolResultContent = $"[工具执行异常 Traceback/Error]\n{execResult.ErrorMessage}\n部分输出: {execResult.Output}\n提示：请结合报错信息自主反思代码或参数问题，修正后再次尝试。";
                        }

                        string effectiveCallId = string.IsNullOrWhiteSpace(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id;

                        loopHistory.Add(new ChatTurn
                        {
                            Role = "tool",
                            ToolCallId = effectiveCallId,
                            Content = toolResultContent
                        });

                        var (rawPath, mirrorPath) = ExtractPathInfo(fnName, fnArgs, execResult.GeneratedArtifacts);

                        executedToolCalls.Add(new ToolCallRecordItem
                        {
                            CallId = effectiveCallId,
                            ToolName = fnName,
                            Arguments = fnArgs,
                            Output = execResult.Output,
                            RawOutputPath = rawPath,
                            MirroredPath = mirrorPath,
                            Success = execResult.Success,
                            RetryCount = currentToolRetryCount,
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
                            ExecutedToolCalls = executedToolCalls,
                            SafetyBlockNotices = new List<string>(safetyBlockNotices),
                            IsStreamTruncated = isLoopStreamTruncated
                        };
                    }

                    bool producedNewArtifacts = allGeneratedArtifacts.Count > artifactCountStart;

                    if (anyToolFailed)
                    {
                        if (safetyBlockCount >= 2)
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
                                AgentTerminationReason.ConsecutiveErrorsTripped,
                                activeSkillInstruction,
                                progress,
                                cancellationToken,
                                safetyBlockNotices);
                        }

                        int allowedRetryQuota = turnErrors.Count > 0 ? turnErrors.Min(GetErrorRetryQuota) : 2;

                        if (!producedNewArtifacts &&
                            currentToolRetryCount < allowedRetryQuota &&
                            totalSelfHealingRetries < MaxGlobalSelfHealingRetries)
                        {
                            currentToolRetryCount++;
                            totalSelfHealingRetries++;

                            progress?.Report(new AgentProgressUpdate
                            {
                                CurrentIteration = iteration,
                                MaxIterations = maxIterations,
                                Phase = "reflecting",
                                StatusMessage = $"第 {iteration}/{maxIterations} 轮 (自愈微调 {currentToolRetryCount}/{allowedRetryQuota}，全局预算 {totalSelfHealingRetries}/{MaxGlobalSelfHealingRetries})：检测到可自愈异常，Agent 正在调整代码...",
                                GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                                IntermediateReasoning = accumulatedReasoning.ToString()
                            });
                            continue;
                        }

                        currentToolRetryCount = 0;

                        if (!producedNewArtifacts)
                        {
                            consecutiveFailures++;
                        }

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "reflecting",
                            StatusMessage = $"第 {iteration}/{maxIterations} 轮：执行遇阻 ({consecutiveFailures}/{MaxConsecutiveFailures})，Agent 正在汇聚阶段性结论...",
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
                                activeSkillInstruction,
                                progress,
                                cancellationToken,
                                safetyBlockNotices);
                        }
                    }
                    else
                    {
                        currentToolRetryCount = 0;
                        consecutiveFailures = 0;
                    }

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
                            activeSkillInstruction,
                            progress,
                            cancellationToken,
                            safetyBlockNotices);
                    }

                    continue;
                }
                else
                {
                    progress?.Report(new AgentProgressUpdate
                    {
                        CurrentIteration = iteration,
                        MaxIterations = maxIterations,
                        Phase = "complete",
                        StatusMessage = "分析完毕，已生成综合业务研报。",
                        IntermediateContent = response.Content,
                        GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                    });

                    string cleanedContent = SanitizeMarkdownImageLinks(response.Content);

                    return new AgentLoopResult
                    {
                        Success = true,
                        TerminationReason = AgentTerminationReason.NormalCompleted,
                        FinalContent = cleanedContent,
                        ReasoningContent = accumulatedReasoning.ToString(),
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory,
                        ExecutedToolCalls = executedToolCalls,
                        SafetyBlockNotices = new List<string>(safetyBlockNotices),
                        IsStreamTruncated = isLoopStreamTruncated
                    };
                }
            }

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
                activeSkillInstruction,
                progress,
                cancellationToken,
                safetyBlockNotices);
        }

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
            string? activeSkillInstruction,
            IProgress<AgentProgressUpdate>? progress,
            CancellationToken cancellationToken,
            List<string>? safetyBlockNotices = null)
        {
            var resolvedSafetyNotices = safetyBlockNotices != null
                ? new List<string>(safetyBlockNotices)
                : new List<string>();

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
                    ExecutedToolCalls = executedToolCalls,
                    SafetyBlockNotices = resolvedSafetyNotices
                };
            }

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
                    ExecutedToolCalls = executedToolCalls,
                    SafetyBlockNotices = resolvedSafetyNotices
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

            string synthesisPrompt = BuildFinalSynthesisPrompt(failedToolSummaries.Count > 0, failedToolSummaries, allGeneratedArtifacts);
            var synthesisTurns = new List<ChatTurn>(loopHistory)
            {
                new ChatTurn
                {
                    Role = "system",
                    Content = synthesisPrompt
                }
            };

            ChatToolResponse finalResponse;
            try
            {
                finalResponse = await RequestChatTurnAsync(
                    settings,
                    synthesisTurns,
                    documentContext,
                    userQuestion,
                    toolDefs: Enumerable.Empty<ToolDefinition>(),
                    toolChoice: "none",
                    iteration,
                    maxIterations,
                    allGeneratedArtifacts,
                    accumulatedReasoning,
                    activeSkillInstruction,
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
                        ExecutedToolCalls = executedToolCalls,
                        SafetyBlockNotices = resolvedSafetyNotices
                    };
                }

                string fallbackText = $"### 业务分析与演算成果汇报 (兜底汇总)\n\n本次分析已到达最大规划决策轮次（共 {iteration} 轮）。已成功生成并捕获 **{allGeneratedArtifacts.Count}** 幅分析图表至右侧画廊与消息卡片，请参考上方图表与计算指标展开业务决策。\n\n*(注：终轮合成请求出现暂态网络波动: {ex.Message})*";
                return new AgentLoopResult
                {
                    Success = true,
                    TerminationReason = terminationReason,
                    FinalContent = fallbackText,
                    ReasoningContent = accumulatedReasoning.ToString(),
                    TotalIterations = iteration,
                    GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                    CompleteTurnHistory = loopHistory,
                    ExecutedToolCalls = executedToolCalls,
                    SafetyBlockNotices = resolvedSafetyNotices
                };
            }

            if (finalResponse.HasToolCalls)
            {
                finalResponse.ToolCalls.Clear();
            }

            if (!string.IsNullOrWhiteSpace(finalResponse.ReasoningContent))
            {
                if (accumulatedReasoning.Length > 0) accumulatedReasoning.AppendLine("\n--- 终轮收敛推导 ---\n");
                accumulatedReasoning.Append(finalResponse.ReasoningContent);
            }

            string finalMarkdown = SanitizeMarkdownImageLinks(finalResponse.Content);
            if (string.IsNullOrWhiteSpace(finalMarkdown))
            {
                finalMarkdown = $"### 业务数据分析综合研报\n\n本次多步分析推导已全部完成（共执行 {iteration} 轮 Agent 决策）。\n系统已全量采集并挂接 **{allGeneratedArtifacts.Count}** 组图表成果至右侧画廊与卡片，所有指标与明细已在上方计算完毕，请结合图表展开决策。";
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
                ExecutedToolCalls = executedToolCalls,
                SafetyBlockNotices = resolvedSafetyNotices
            };
        }

        private static string BuildFinalSynthesisPrompt(bool hasFailedTools, List<string> failedToolSummaries, List<string> generatedArtifacts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【系统终轮强制收敛指令 (Final Synthesis)】：");
            sb.AppendLine("当前任务已到达最终汇报决策轮次。严禁再次调用任何工具或运行代码！");

            if (hasFailedTools && failedToolSummaries.Count > 0)
            {
                sb.AppendLine("说明：在探索演算过程中，以下部分步骤遭遇客观异常：");
                foreach (var err in failedToolSummaries.TakeLast(3))
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
                sb.AppendLine("4. 对上述遇到异常或超时的未决项，如实向用户客观说明困难并给出后续建议；");
                sb.AppendLine("5. 严禁在 Markdown 正文中输出形如 ![...](xxx.png) 的相对路径图片语法（所有生成图表已由前端系统画廊与卡片高清呈现，正文引用相对路径会导致裂图）。");
            }
            else
            {
                sb.AppendLine("请务必：");
                sb.AppendLine("1. 严禁再次发起工具调用，直接输出条理清晰、专业深刻的最终 Markdown 业务研报；");
                sb.AppendLine("2. 汇总整理全部分析成果，呈现计算出的 Ground-Truth 核心指标；");
                if (generatedArtifacts.Count > 0)
                {
                    sb.AppendLine($"3. 结合已成功生成的全部分析图表进行深入洞察与结论阐述；");
                }
                sb.AppendLine("4. 严禁在 Markdown 正文中输出形如 ![...](xxx.png) 的相对路径图片语法（所有生成图表已由前端系统画廊与卡片高清呈现，正文引用相对路径会导致裂图）。");
            }
            return sb.ToString();
        }

        private static int GetErrorRetryQuota(string? fullErrorMessage)
        {
            if (string.IsNullOrWhiteSpace(fullErrorMessage)) return 2;

            // 0 次自愈：环境缺依赖或高危安全硬拦截，不计入静默自愈微调计数
            if (fullErrorMessage.Contains("[ERR_TYPE:ModuleNotFound]") ||
                fullErrorMessage.Contains("ModuleNotFoundError") ||
                fullErrorMessage.Contains("[ERR_TYPE:SafetyBlocked]"))
            {
                return 0;
            }

            if (fullErrorMessage.Contains("[ERR_TYPE:SyntaxError]") ||
                fullErrorMessage.Contains("[ERR_TYPE:FileNotFound]") ||
                fullErrorMessage.Contains("[ERR_TYPE:KeyError]") ||
                fullErrorMessage.Contains("FileNotFoundError") ||
                fullErrorMessage.Contains("KeyError") ||
                fullErrorMessage.Contains("no such file or directory") ||
                System.Text.RegularExpressions.Regex.IsMatch(fullErrorMessage, @"^\s*(Syntax|Indentation)Error:", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                return 1;
            }

            return 2;
        }

        /// 精简错误摘要（防止超长 Traceback 撑爆收敛 Prompt）
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
            string? activeSkillInstruction,
            IProgress<AgentProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var curTurnContent = new StringBuilder();
            var curTurnReasoning = new StringBuilder();
            var toolCalls = new List<ToolCallItem>();
            bool streamSucceeded = false;
            string baseReasoning = accumulatedReasoning.ToString();
            var lastReportTime = DateTime.UtcNow;

            try
            {
                await foreach (var chunk in _chatService.StreamChatCompletionWithToolsAsync(
                    settings,
                    loopHistory,
                    documentContext,
                    userQuestion,
                    toolDefs,
                    toolChoice: toolChoice,
                    activeSkillInstruction: activeSkillInstruction,
                    cancellationToken: cancellationToken))
                {
                    if (chunk.Type == StreamChunkType.Reasoning)
                    {
                        curTurnReasoning.Append(chunk.Text);

                        var now = DateTime.UtcNow;
                        if ((now - lastReportTime).TotalMilliseconds >= 80)
                        {
                            lastReportTime = now;
                            string combinedReasoning = baseReasoning;
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
                    }
                    else if (chunk.Type == StreamChunkType.Content)
                    {
                        curTurnContent.Append(chunk.Text);

                        var now = DateTime.UtcNow;
                        if ((now - lastReportTime).TotalMilliseconds >= 80)
                        {
                            lastReportTime = now;
                            string combinedReasoning = baseReasoning;
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
                    }
                    else if (chunk.Type == StreamChunkType.ToolCall && chunk.ToolCall != null)
                    {
                        toolCalls.Add(chunk.ToolCall);
                    }
                }

                string finalCombined = baseReasoning;
                if (curTurnReasoning.Length > 0)
                {
                    finalCombined += (finalCombined.Length > 0 ? "\n" : "") + curTurnReasoning.ToString();
                }
                progress?.Report(new AgentProgressUpdate
                {
                    CurrentIteration = iteration,
                    MaxIterations = maxIterations,
                    Phase = toolCalls.Count > 0 ? "executing_tool" : "streaming",
                    StatusMessage = toolCalls.Count > 0 ? $"第 {iteration}/{maxIterations} 轮：已发起工具调用决策..." : "正在完成数据洞察...",
                    IntermediateReasoning = finalCombined,
                    IntermediateContent = curTurnContent.ToString(),
                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                });

                streamSucceeded = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogException("StreamChatError", ex);

                bool hasSubstantiveContent = curTurnContent.Length > 0;
                if (hasSubstantiveContent)
                {
                    // 已接收到实质性正文，保留现有输出并安全收敛，不再盲目重试导致闪退或重复 token 消耗
                    toolCalls.Clear();
                    progress?.Report(new AgentProgressUpdate
                    {
                        CurrentIteration = iteration,
                        MaxIterations = maxIterations,
                        Phase = "warning",
                        StatusMessage = "[网络流式中断] 接收推流时连接中断，已保留已生成内容并结束本轮输出。",
                        IntermediateReasoning = accumulatedReasoning.ToString(),
                        IntermediateContent = curTurnContent.ToString(),
                        GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                    });

                    return new ChatToolResponse
                    {
                        Content = curTurnContent.ToString(),
                        ReasoningContent = curTurnReasoning.Length > 0 ? curTurnReasoning.ToString() : string.Empty,
                        ToolCalls = toolCalls,
                        IsStreamTruncated = true
                    };
                }

                streamSucceeded = false;
                progress?.Report(new AgentProgressUpdate
                {
                    CurrentIteration = iteration,
                    MaxIterations = maxIterations,
                    Phase = "warning",
                    StatusMessage = $"[流式连接暂态波动] 检测到推流中断 ({ex.Message})，正在自动切换至阻塞请求以继续任务...",
                    IntermediateReasoning = accumulatedReasoning.ToString(),
                    GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts)
                });
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

            return await _chatService.SendChatWithToolsAsync(
                settings,
                loopHistory,
                documentContext,
                userQuestion,
                toolDefs,
                toolChoice: toolChoice,
                activeSkillInstruction: activeSkillInstruction,
                cancellationToken: cancellationToken);
        }

        private static (string? rawPath, string? mirrorPath) ExtractPathInfo(string toolName, string arguments, List<string> artifacts)
        {
            string? mirror = artifacts != null && artifacts.Count > 0 ? artifacts[0] : null;
            string? raw = null;

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    arguments,
                    @"savefig\s*\(\s*(?:fname\s*=\s*)?['""]([^'""]+)['""]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    raw = match.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrEmpty(raw) && !string.IsNullOrEmpty(mirror))
            {
                string fname = Path.GetFileName(mirror);
                int lastDot = fname.LastIndexOf('.');
                if (lastDot > 0)
                {
                    string nameNoExt = fname[..lastDot];
                    string ext = fname[lastDot..];

                    int lastUnder = nameNoExt.LastIndexOf('_');
                    if (lastUnder > 0)
                    {
                        int secondLastUnder = nameNoExt.LastIndexOf('_', lastUnder - 1);
                        if (secondLastUnder > 0)
                        {
                            raw = nameNoExt[..secondLastUnder] + ext;
                        }
                    }
                }

                if (string.IsNullOrEmpty(raw))
                {
                    raw = fname;
                }
            }

            return (raw, mirror);
        }

        private static string SanitizeMarkdownImageLinks(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

            return System.Text.RegularExpressions.Regex.Replace(
                markdown,
                @"!\[([^\]]*)\]\((?!(?:https?:\/\/|\/|data:))([^\)]+)\)",
                match =>
                {
                    string altText = match.Groups[1].Value.Trim();
                    string fileName = Path.GetFileName(match.Groups[2].Value.Trim());
                    string label = string.IsNullOrEmpty(altText) ? fileName : $"{altText} ({fileName})";
                    return $"> **[可视化图表：{label}]** *(已由系统自动挂载至上方消息卡片与右侧画廊，请查阅对应高清图件)*";
                });
        }

        private static void LogException(string tag, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataAnalysis_Assistant", "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, $"agent_{DateTime.Now:yyyyMMdd}.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
                File.AppendAllText(logFile, entry, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
