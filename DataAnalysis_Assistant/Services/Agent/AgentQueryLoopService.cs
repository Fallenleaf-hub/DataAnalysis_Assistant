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
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// ReAct / CodeAct 自主 Agent 核心状态机调度服务 (AgentQueryLoop)
    /// 机制：思考 -> 决策工具 -> 执行工具 -> 评估结果 -> 自主反思自愈 -> 终止交付
    /// </summary>
    public class AgentQueryLoopService
    {
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
            int maxIterations = 8,
            IProgress<AgentProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var loopHistory = new List<ChatTurn>(initialHistory ?? new());
            var allGeneratedArtifacts = new List<string>();
            var accumulatedReasoning = new StringBuilder();

            var toolDefs = _tools.Select(t => t.Definition).ToList();

            int iteration = 0;
            int consecutiveFailures = 0;

            while (iteration < maxIterations && !cancellationToken.IsCancellationRequested)
            {
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

                // 2. 调用大模型发起 Tools 请求（默认开启流式推流，支持思考过程与正文实时逐字打字渲染）
                ChatToolResponse response;
                try
                {
                    response = await RequestChatTurnAsync(
                        settings,
                        loopHistory,
                        documentContext,
                        userQuestion,
                        toolDefs,
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
                            ErrorMessage = "任务已由用户手动停止。",
                            TotalIterations = iteration,
                            GeneratedArtifactPaths = allGeneratedArtifacts
                        };
                    }

                    return new AgentLoopResult
                    {
                        Success = false,
                        ErrorMessage = $"大模型请求失败: {ex.Message}",
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts
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
                            accumulatedReasoning.AppendLine($"> ⚠️ **[工具执行异常]**：{execResult.ErrorMessage} (触发反思自愈)");
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
                    }

                    // 4. 自主反思与错误自愈机制 (Self-Reflection & Healing)
                    if (anyToolFailed)
                    {
                        consecutiveFailures++;
                        accumulatedReasoning.AppendLine($"> 🔄 **[自主反思中]** 正在调整代码与参数并规划自愈路径 (失败重试 {consecutiveFailures}/3)...");

                        progress?.Report(new AgentProgressUpdate
                        {
                            CurrentIteration = iteration,
                            MaxIterations = maxIterations,
                            Phase = "reflecting",
                            StatusMessage = $"第 {iteration}/{maxIterations} 轮：检测到执行异常，Agent 正在自主反思调整代码...",
                            GeneratedArtifactsSoFar = new List<string>(allGeneratedArtifacts),
                            IntermediateReasoning = accumulatedReasoning.ToString()
                        });

                        if (consecutiveFailures >= 3)
                        {
                            // 达到 3 次连续报错上限，注入硬性刹车提示，引导模型基于已知信息输出结论
                            loopHistory.Add(new ChatTurn
                            {
                                Role = "system",
                                Content = "【安全刹车熔断】代码或工具已连续 3 次报错。请立即停止工具重试，综合已知数据直接向用户给出分析总结与异常诊断说明。"
                            });
                        }
                    }
                    else
                    {
                        consecutiveFailures = 0;
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
                        FinalContent = response.Content,
                        ReasoningContent = accumulatedReasoning.ToString(),
                        TotalIterations = iteration,
                        GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                        CompleteTurnHistory = loopHistory
                    };
                }
            }

            // 达到最大轮次强制终止
            return new AgentLoopResult
            {
                Success = true,
                FinalContent = "已达到最大决策轮次上限（8 轮）。上述为阶段性分析与演算成果。",
                ReasoningContent = accumulatedReasoning.ToString(),
                TotalIterations = maxIterations,
                GeneratedArtifactPaths = allGeneratedArtifacts.Distinct().ToList(),
                CompleteTurnHistory = loopHistory
            };
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
                    cancellationToken))
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
                toolChoice: "auto",
                cancellationToken: cancellationToken);
        }
    }
}
