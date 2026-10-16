using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services.Context;

namespace DataAnalysis_Assistant.Services
{
    /// <summary>
    /// 对话轮次模型 (用于组装 LLM messages 历史)
    /// </summary>
    public class ChatTurn
    {
        public string Role { get; set; } = string.Empty; // "system", "user", "assistant", "tool"
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 当 Role 为 "tool" 时，记录被调用的工具 call_id
        /// </summary>
        public string? ToolCallId { get; set; }

        /// <summary>
        /// 当 Role 为 "assistant" 且发起工具调用时记录工具调用列表
        /// </summary>
        public List<ToolCallItem>? ToolCalls { get; set; }
    }

    /// <summary>
    /// 流式分块类型 (思考过程 vs 正式回答 vs 工具调用)
    /// </summary>
    public enum StreamChunkType
    {
        Reasoning,
        Content,
        ToolCall
    }

    /// <summary>
    /// 流式分块数据包
    /// </summary>
    public record StreamChunk(StreamChunkType Type, string Text, ToolCallItem? ToolCall = null);

    /// <summary>
    /// OpenAI 兼容 LLM 对话服务 (支持 SSE 流式推流与 Function Calling / Tools 标准协议)
    /// 全面兼容 OpenAI、DeepSeek、Ollama 等标准接口
    /// </summary>
    public class LlmChatService
    {
        // 复用单例 HttpClient，避免套接字耗尽
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private static readonly ContextCompactionService _compactionService = new();

        /// <summary>
        /// 统一组装 OpenAI 标准 messages 载荷 (支持 system, user, assistant 含 tool_calls, tool 结果，并具备保真上下文压缩)
        /// </summary>
        public static List<object> BuildMessagesPayload(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion)
        {
            var messages = new List<object>();

            // 1. 组装 System 消息 (系统提示词 + 可选参考文档/Schema 上下文)
            var sbSystem = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                sbSystem.AppendLine(settings.SystemPrompt.Trim());
            }

            if (!string.IsNullOrWhiteSpace(documentContext))
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine("================ 当前参考文档与数据骨架 (Code-to-Data) ================");
                sbSystem.AppendLine(documentContext.Trim());
                sbSystem.AppendLine("========================================================================");
                sbSystem.AppendLine("数据分析与问答规范：");
                sbSystem.AppendLine("1. 【数据不动代码动】：若上方为表格骨架（Schema），全量数据完整保留在本地磁盘，严禁臆测数据或要求用户复制贴表。");
                sbSystem.AppendLine("2. 【代码下推全量聚合】：请基于字段名与数据类型，编写 Python Pandas/Matplotlib 脚本在本地全量计算并输出指标。");
                sbSystem.AppendLine("3. 【业务事实保真】：对文档中的政策口径与条款，严格按照原文对齐，确保结论 100% 真实准确。");
            }

            if (sbSystem.Length > 0)
            {
                messages.Add(new { role = "system", content = sbSystem.ToString() });
            }

            // 2. 组装历史对话（引入 ContextCompactionService 施加保真边界保护）
            List<ChatTurn> effectiveHistory = history ?? new();
            if (history != null && history.Count > 3)
            {
                var rolePairs = history
                    .Where(t => t.Role != "tool" && (t.ToolCalls == null || t.ToolCalls.Count == 0))
                    .Select(t => (t.Role, t.Content))
                    .ToList();

                var compactionResult = _compactionService.CompactContext(
                    sbSystem.ToString(),
                    documentContext ?? string.Empty,
                    rolePairs,
                    userQuestion ?? string.Empty);

                if (compactionResult.WasCompacted)
                {
                    var newHistory = new List<ChatTurn>();
                    foreach (var (r, c) in compactionResult.CompactedHistory)
                    {
                        newHistory.Add(new ChatTurn { Role = r, Content = c });
                    }
                    // 保留原有 tool 相关回合
                    foreach (var t in history.Where(t => t.Role == "tool" || (t.ToolCalls != null && t.ToolCalls.Count > 0)))
                    {
                        newHistory.Add(t);
                    }
                    effectiveHistory = newHistory;
                }
            }

            foreach (var turn in effectiveHistory)
            {
                string role = (turn.Role ?? string.Empty).Trim().ToLowerInvariant();

                if (role == "tool")
                {
                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = turn.ToolCallId ?? string.Empty,
                        content = turn.Content ?? string.Empty
                    });
                }
                else if (role == "assistant" && turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                {
                    messages.Add(new
                    {
                        role = "assistant",
                        content = string.IsNullOrEmpty(turn.Content) ? null : turn.Content,
                        tool_calls = turn.ToolCalls
                    });
                }
                else if (!string.IsNullOrWhiteSpace(turn.Content))
                {
                    messages.Add(new
                    {
                        role = string.IsNullOrEmpty(role) ? "user" : role,
                        content = turn.Content
                    });
                }
            }

            // 3. 组装当前用户问题
            if (!string.IsNullOrWhiteSpace(userQuestion))
            {
                messages.Add(new { role = "user", content = userQuestion });
            }

            return messages;
        }

        /// <summary>
        /// 发起兼容 OpenAI/DeepSeek 标准的 Tools/Function Calling 对话请求 (非流式精确保真模式)
        /// 适合 Agent 主循环准确获取工具调用与推理决策
        /// </summary>
        public async Task<ChatToolResponse> SendChatWithToolsAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            IEnumerable<ToolDefinition>? tools = null,
            string toolChoice = "auto",
            CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings), "配置项 AppSettings 不能为空。");
            }

            string endpoint = (settings.BaseUrl ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/chat/completions";
            }

            var messages = BuildMessagesPayload(settings, history, documentContext, userQuestion);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(settings.ModelName) ? "gpt-4o" : settings.ModelName,
                ["messages"] = messages,
                ["temperature"] = settings.Temperature,
                ["max_tokens"] = Math.Clamp(settings.MaxContextLength > 0 ? settings.MaxContextLength : 4096, 1024, 8192),
                ["stream"] = false
            };

            if (tools != null && tools.Any())
            {
                payloadDict["tools"] = tools;
                payloadDict["tool_choice"] = string.IsNullOrWhiteSpace(toolChoice) ? "auto" : toolChoice;
            }
            else if (!string.IsNullOrWhiteSpace(toolChoice) && toolChoice.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payloadDict["tool_choice"] = "none";
            }

            string jsonBody = JsonSerializer.Serialize(payloadDict);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"无法连接到模型服务地址 [{endpoint}]：{ex.Message}，请检查网络或代理配置。", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new Exception("请求大模型服务超时或已被取消。", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var doc = JsonDocument.Parse(errorDetail);
                    if (doc.RootElement.TryGetProperty("error", out var errorElem) &&
                        errorElem.TryGetProperty("message", out var msgElem))
                    {
                        errorDetail = msgElem.GetString() ?? errorDetail;
                    }
                }
                catch
                {
                }

                throw new Exception($"大模型接口响应异常 [{(int)response.StatusCode} {response.ReasonPhrase}]：{errorDetail}");
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = new ChatToolResponse();

            using var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("finish_reason", out var finishElem))
                {
                    result.FinishReason = finishElem.GetString() ?? string.Empty;
                }

                if (firstChoice.TryGetProperty("message", out var messageElem))
                {
                    // 1. 提取推理思考内容 (DeepSeek reasoning_content 兼容)
                    if (messageElem.TryGetProperty("reasoning_content", out var rElem) &&
                        rElem.ValueKind == JsonValueKind.String)
                    {
                        result.ReasoningContent = rElem.GetString() ?? string.Empty;
                    }
                    else if (messageElem.TryGetProperty("reasoning", out var rElem2) &&
                             rElem2.ValueKind == JsonValueKind.String)
                    {
                        result.ReasoningContent = rElem2.GetString() ?? string.Empty;
                    }

                    // 2. 提取文本内容
                    if (messageElem.TryGetProperty("content", out var contentElem) &&
                        contentElem.ValueKind == JsonValueKind.String)
                    {
                        string content = contentElem.GetString() ?? string.Empty;

                        // 兼容某些本地模型输出的 <think>...</think> 标签
                        if (string.IsNullOrEmpty(result.ReasoningContent) && content.Contains("<think>"))
                        {
                            int start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                            int end = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                            if (end > start)
                            {
                                result.ReasoningContent = content.Substring(start + 7, end - start - 7).Trim();
                                content = (content.Substring(0, start) + content.Substring(end + 8)).Trim();
                            }
                        }

                        result.Content = content;
                    }

                    // 3. 提取 tool_calls 列表
                    if (messageElem.TryGetProperty("tool_calls", out var toolCallsElem) &&
                        toolCallsElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsElem.EnumerateArray())
                        {
                            var toolCallItem = new ToolCallItem();
                            if (tc.TryGetProperty("id", out var idElem))
                            {
                                toolCallItem.Id = idElem.GetString() ?? string.Empty;
                            }
                            if (tc.TryGetProperty("type", out var typeElem))
                            {
                                toolCallItem.Type = typeElem.GetString() ?? "function";
                            }

                            if (tc.TryGetProperty("function", out var fnElem))
                            {
                                string fnName = string.Empty;
                                string fnArgs = string.Empty;
                                if (fnElem.TryGetProperty("name", out var nElem))
                                {
                                    fnName = nElem.GetString() ?? string.Empty;
                                }
                                if (fnElem.TryGetProperty("arguments", out var aElem))
                                {
                                    fnArgs = aElem.ValueKind == JsonValueKind.String
                                        ? (aElem.GetString() ?? string.Empty)
                                        : aElem.GetRawText();
                                }
                                toolCallItem.Function = new ToolCallFunction { Name = fnName, Arguments = fnArgs };
                            }

                            result.ToolCalls.Add(toolCallItem);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 发起兼容 OpenAI SSE 标准的流式对话补全（支持思考流、正文流双通道及工具调用）
        /// </summary>
        public async IAsyncEnumerable<StreamChunk> StreamChatCompletionAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in StreamChatCompletionWithToolsAsync(
                settings,
                history,
                documentContext,
                userQuestion,
                tools: null,
                toolChoice: "auto",
                cancellationToken: cancellationToken))
            {
                yield return chunk;
            }
        }

        /// <summary>
        /// 发起兼容 OpenAI SSE 标准的流式对话补全（支持带 Tools 定义的流式通信）
        /// </summary>
        public async IAsyncEnumerable<StreamChunk> StreamChatCompletionWithToolsAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            IEnumerable<ToolDefinition>? tools = null,
            string toolChoice = "auto",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings), "配置项 AppSettings 不能为空。");
            }

            string endpoint = (settings.BaseUrl ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/chat/completions";
            }

            var messages = BuildMessagesPayload(settings, history, documentContext, userQuestion);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(settings.ModelName) ? "gpt-4o" : settings.ModelName,
                ["messages"] = messages,
                ["temperature"] = settings.Temperature,
                ["max_tokens"] = Math.Clamp(settings.MaxContextLength > 0 ? settings.MaxContextLength : 4096, 1024, 8192),
                ["stream"] = true
            };

            if (tools != null && tools.Any())
            {
                payloadDict["tools"] = tools;
                payloadDict["tool_choice"] = string.IsNullOrWhiteSpace(toolChoice) ? "auto" : toolChoice;
            }
            else if (!string.IsNullOrWhiteSpace(toolChoice) && toolChoice.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payloadDict["tool_choice"] = "none";
            }

            string jsonBody = JsonSerializer.Serialize(payloadDict);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("X-Accel-Buffering", "no");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"无法连接到模型服务地址 [{endpoint}]：{ex.Message}，请检查网络或代理配置。", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new Exception("请求大模型服务超时或已被取消。", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var doc = JsonDocument.Parse(errorDetail);
                    if (doc.RootElement.TryGetProperty("error", out var errorElem) &&
                        errorElem.TryGetProperty("message", out var msgElem))
                    {
                        errorDetail = msgElem.GetString() ?? errorDetail;
                    }
                }
                catch
                {
                }

                throw new Exception($"大模型接口响应异常 [{(int)response.StatusCode} {response.ReasonPhrase}]：{errorDetail}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            bool inThinkTag = false;
            // 缓存累积流式返回的 tool_calls
            var accumulatedToolCalls = new Dictionary<int, (string id, string type, StringBuilder name, StringBuilder args)>();

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                line = line.Trim();

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    string data = line.Substring(5).Trim();

                    if (data == "[DONE]")
                    {
                        // 流式结束，若有累积的 tool_calls，一次性产出
                        foreach (var kvp in accumulatedToolCalls.OrderBy(k => k.Key))
                        {
                            var item = new ToolCallItem
                            {
                                Id = kvp.Value.id,
                                Type = kvp.Value.type,
                                Function = new ToolCallFunction
                                {
                                    Name = kvp.Value.name.ToString(),
                                    Arguments = kvp.Value.args.ToString()
                                }
                            };
                            yield return new StreamChunk(StreamChunkType.ToolCall, item.FunctionName, item);
                        }
                        yield break;
                    }

                    string? reasoningDelta = null;
                    string? contentDelta = null;

                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(data);
                        var root = jsonDoc.RootElement;

                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("delta", out var delta))
                            {
                                // 1. 优先提取推理思考字段
                                if (delta.TryGetProperty("reasoning_content", out var rElem) &&
                                    rElem.ValueKind == JsonValueKind.String)
                                {
                                    reasoningDelta = rElem.GetString();
                                }
                                else if (delta.TryGetProperty("reasoning", out var rElem2) &&
                                         rElem2.ValueKind == JsonValueKind.String)
                                {
                                    reasoningDelta = rElem2.GetString();
                                }

                                // 2. 提取正式回答字段
                                if (delta.TryGetProperty("content", out var contentElem) &&
                                    contentElem.ValueKind == JsonValueKind.String)
                                {
                                    contentDelta = contentElem.GetString();
                                }

                                // 3. 提取流式 tool_calls delta
                                if (delta.TryGetProperty("tool_calls", out var tcArray) &&
                                    tcArray.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var tcElem in tcArray.EnumerateArray())
                                    {
                                        int index = 0;
                                        if (tcElem.TryGetProperty("index", out var idxElem))
                                        {
                                            index = idxElem.GetInt32();
                                        }

                                        if (!accumulatedToolCalls.ContainsKey(index))
                                        {
                                            accumulatedToolCalls[index] = (string.Empty, "function", new StringBuilder(), new StringBuilder());
                                        }

                                        var cur = accumulatedToolCalls[index];
                                        string curId = cur.id;
                                        string curType = cur.type;

                                        if (tcElem.TryGetProperty("id", out var idElem))
                                        {
                                            curId = idElem.GetString() ?? curId;
                                        }
                                        if (tcElem.TryGetProperty("type", out var typeElem))
                                        {
                                            curType = typeElem.GetString() ?? curType;
                                        }

                                        if (tcElem.TryGetProperty("function", out var fnElem))
                                        {
                                            if (fnElem.TryGetProperty("name", out var nElem) && nElem.GetString() is string fnName)
                                            {
                                                cur.name.Append(fnName);
                                            }
                                            if (fnElem.TryGetProperty("arguments", out var aElem) && aElem.GetString() is string fnArg)
                                            {
                                                cur.args.Append(fnArg);
                                            }
                                        }

                                        accumulatedToolCalls[index] = (curId, curType, cur.name, cur.args);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }

                    // 推送思考流
                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        yield return new StreamChunk(StreamChunkType.Reasoning, reasoningDelta);
                    }

                    // 推送正文流 (含 <think> 标签剥离兼容)
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        if (!inThinkTag)
                        {
                            int thinkStart = contentDelta.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                            if (thinkStart >= 0)
                            {
                                string beforeThink = contentDelta.Substring(0, thinkStart);
                                if (!string.IsNullOrEmpty(beforeThink))
                                {
                                    yield return new StreamChunk(StreamChunkType.Content, beforeThink);
                                }

                                string afterThink = contentDelta.Substring(thinkStart + 7);
                                inThinkTag = true;

                                int thinkEnd = afterThink.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                if (thinkEnd >= 0)
                                {
                                    string thinkText = afterThink.Substring(0, thinkEnd);
                                    if (!string.IsNullOrEmpty(thinkText))
                                    {
                                        yield return new StreamChunk(StreamChunkType.Reasoning, thinkText);
                                    }
                                    inThinkTag = false;

                                    string afterEnd = afterThink.Substring(thinkEnd + 8);
                                    if (!string.IsNullOrEmpty(afterEnd))
                                    {
                                        yield return new StreamChunk(StreamChunkType.Content, afterEnd);
                                    }
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(afterThink))
                                    {
                                        yield return new StreamChunk(StreamChunkType.Reasoning, afterThink);
                                    }
                                }
                            }
                            else
                            {
                                yield return new StreamChunk(StreamChunkType.Content, contentDelta);
                            }
                        }
                        else
                        {
                            int thinkEnd = contentDelta.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                            if (thinkEnd >= 0)
                            {
                                string thinkText = contentDelta.Substring(0, thinkEnd);
                                if (!string.IsNullOrEmpty(thinkText))
                                {
                                    yield return new StreamChunk(StreamChunkType.Reasoning, thinkText);
                                }
                                inThinkTag = false;

                                string afterEnd = contentDelta.Substring(thinkEnd + 8);
                                if (!string.IsNullOrEmpty(afterEnd))
                                {
                                    yield return new StreamChunk(StreamChunkType.Content, afterEnd);
                                }
                            }
                            else
                            {
                                yield return new StreamChunk(StreamChunkType.Reasoning, contentDelta);
                            }
                        }
                    }
                }
            }

            // 流式自然结束兜底：若流已关闭但未显式收到 [DONE]，补齐产出累积的 tool_calls
            foreach (var kvp in accumulatedToolCalls.OrderBy(k => k.Key))
            {
                var item = new ToolCallItem
                {
                    Id = kvp.Value.id,
                    Type = kvp.Value.type,
                    Function = new ToolCallFunction
                    {
                        Name = kvp.Value.name.ToString(),
                        Arguments = kvp.Value.args.ToString()
                    }
                };
                yield return new StreamChunk(StreamChunkType.ToolCall, item.FunctionName, item);
            }
        }
    }
}

