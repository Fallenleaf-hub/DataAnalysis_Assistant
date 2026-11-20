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
    public class ChatTurn
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;

        public string? ToolCallId { get; set; }

        public List<ToolCallItem>? ToolCalls { get; set; }
    }

    public enum StreamChunkType
    {
        Reasoning,
        Content,
        ToolCall
    }

    public record StreamChunk(StreamChunkType Type, string Text, ToolCallItem? ToolCall = null);

    public class LlmChatService
    {
        // 复用单例 HttpClient，避免套接字耗尽
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private static readonly ContextCompactionService _compactionService = new();

        public static List<object> BuildMessagesPayload(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            string? activeSkillInstruction = null)
        {
            var messages = new List<object>();

            var sbSystem = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                sbSystem.AppendLine(settings.SystemPrompt.Trim());
            }

            string chartThemePrompt = ThemePresetCatalog.BuildChartThemePrompt(settings);
            if (!string.IsNullOrWhiteSpace(chartThemePrompt))
            {
                if (sbSystem.Length > 0) sbSystem.AppendLine();
                sbSystem.AppendLine(chartThemePrompt);
            }

            if (!string.IsNullOrWhiteSpace(activeSkillInstruction))
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine(activeSkillInstruction.Trim());
            }

            // 深度注入思考与工具下推纪律规范 (防止 Reasoning 吞噬 Token 上限导致截断)
            sbSystem.AppendLine();
            sbSystem.AppendLine("================ 思考与工具推演纪律 (Reasoning Discipline) ================");
            sbSystem.AppendLine("1. 【思考短小精炼】：严禁在思考链路（Reasoning / Thinking）中编写完整的代码实现或长篇大论草拟脚本；思考过程仅用于梳理逻辑大纲、核验条件与算法参数，必须保持精炼紧凑。");
            sbSystem.AppendLine("2. 【代码严格下推工具】：所有用于数据探查、清洗、统计建模与 Matplotlib 绘图的可执行代码，必须且只能作为参数输出到具体的 tool_calls (如 run_python) 中执行，严禁在思考链或未执行的正文中冗余展开。");
            sbSystem.AppendLine("3. 【成果导向高效推进】：获得工具执行结果后，立即评估数值指标与图表产物，快速推进下一阶段分析或直接输出最终结构化业务研报，杜绝多轮重复探查相同结构。");
            sbSystem.AppendLine("4. 【专业纯文本研报规范】：最终输出的业务研报与回答正文必须采用严谨克制的纯文本 Markdown，严禁使用任何装饰性 Emoji 表情符号（如图表、放大镜、警告、勾选等图标），且严禁复述任何系统内部上下文历史注记。");
            sbSystem.AppendLine("============================================================================");

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

            List<ChatTurn> effectiveHistory = new();
            if (history != null)
            {
                // 先对单条超长的 tool 输出做就地保真折叠（防止单次打印巨幅数组撑爆 Payload）
                foreach (var turn in history)
                {
                    if (turn.Role == "tool" && !string.IsNullOrEmpty(turn.Content) && turn.Content.Length > 2500)
                    {
                        string c = turn.Content;
                        string trimmed = $"{c[..800]}\n\n... [中间过渡性数据输出过长已自动折叠，共省略 {c.Length - 1400} 字符] ...\n\n{c[^600..]}";
                        effectiveHistory.Add(new ChatTurn
                        {
                            Role = turn.Role,
                            Content = trimmed,
                            ToolCallId = turn.ToolCallId,
                            ToolCalls = turn.ToolCalls
                        });
                    }
                    else
                    {
                        effectiveHistory.Add(turn);
                    }
                }
            }

            int sysTokens = ContextCompactionService.EstimateTokens(sbSystem.ToString());
            int docTokens = ContextCompactionService.EstimateTokens(documentContext ?? string.Empty);
            int qTokens = ContextCompactionService.EstimateTokens(userQuestion ?? string.Empty);
            int histTokens = 0;
            foreach (var t in effectiveHistory)
            {
                histTokens += ContextCompactionService.EstimateTokens(t.Content ?? string.Empty);
                if (t.ToolCalls != null)
                {
                    foreach (var tc in t.ToolCalls)
                    {
                        histTokens += ContextCompactionService.EstimateTokens(tc.Function?.Arguments ?? string.Empty) + 20;
                    }
                }
            }

            int totalTokens = sysTokens + docTokens + qTokens + histTokens;
            int triggerLimit = (int)(_compactionService.MaxTokenBudget * _compactionService.TriggerThresholdRatio);

            if (totalTokens > triggerLimit && effectiveHistory.Count > 4)
            {
                int keepRecent = Math.Min(4, effectiveHistory.Count);
                int splitIndex = effectiveHistory.Count - keepRecent;

                var olderTurns = effectiveHistory.Take(splitIndex).ToList();
                var recentTurns = effectiveHistory.Skip(splitIndex).ToList();

                var compactedList = new List<ChatTurn>();

                for (int i = 0; i < olderTurns.Count; i++)
                {
                    var turn = olderTurns[i];
                    string role = turn.Role?.ToLowerInvariant() ?? string.Empty;

                    if (role == "assistant" && turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                    {
                        // 必须严格与紧随其后的 tool 回合成对保留，维护协议结构完整
                        compactedList.Add(turn);
                    }
                    else if (role == "tool")
                    {
                        string c = turn.Content ?? string.Empty;
                        if (c.Length > 400 && !ContextCompactionService.IsGroundTruthAnchor(c))
                        {
                            string summary = $"{c[..200]}...\n[早期过渡性工具输出已安全压缩]";
                            compactedList.Add(new ChatTurn
                            {
                                Role = "tool",
                                ToolCallId = turn.ToolCallId,
                                Content = summary
                            });
                        }
                        else
                        {
                            compactedList.Add(turn);
                        }
                    }
                    else if (role == "user")
                    {
                        compactedList.Add(turn);
                    }
                    else if (role == "assistant")
                    {
                        if (ContextCompactionService.IsGroundTruthAnchor(turn.Content))
                        {
                            compactedList.Add(turn);
                        }
                        else
                        {
                            string summary = ContextCompactionService.SummarizeText(turn.Content, 200);
                            compactedList.Add(new ChatTurn { Role = "assistant", Content = summary });
                        }
                    }
                }

                compactedList.AddRange(recentTurns);
                effectiveHistory = compactedList;
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

            if (!string.IsNullOrWhiteSpace(userQuestion))
            {
                messages.Add(new { role = "user", content = userQuestion });
            }

            return messages;
        }

        public async Task<ChatToolResponse> SendChatWithToolsAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            IEnumerable<ToolDefinition>? tools = null,
            string toolChoice = "auto",
            string? activeSkillInstruction = null,
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

            var messages = BuildMessagesPayload(settings, history, documentContext, userQuestion, activeSkillInstruction);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(settings.ModelName) ? "gpt-4o" : settings.ModelName,
                ["messages"] = messages,
                ["temperature"] = settings.Temperature,
                ["max_tokens"] = 8192,
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

                    if (messageElem.TryGetProperty("content", out var contentElem) &&
                        contentElem.ValueKind == JsonValueKind.String)
                    {
                        string content = contentElem.GetString() ?? string.Empty;

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
                            if (string.IsNullOrWhiteSpace(toolCallItem.Id))
                            {
                                toolCallItem.Id = $"call_{Guid.NewGuid():N}";
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

        public async IAsyncEnumerable<StreamChunk> StreamChatCompletionAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            string? activeSkillInstruction = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in StreamChatCompletionWithToolsAsync(
                settings,
                history,
                documentContext,
                userQuestion,
                tools: null,
                toolChoice: "auto",
                activeSkillInstruction: activeSkillInstruction,
                cancellationToken: cancellationToken))
            {
                yield return chunk;
            }
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatCompletionWithToolsAsync(
            AppSettings settings,
            List<ChatTurn>? history,
            string? documentContext,
            string? userQuestion,
            IEnumerable<ToolDefinition>? tools = null,
            string toolChoice = "auto",
            string? activeSkillInstruction = null,
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

            var messages = BuildMessagesPayload(settings, history, documentContext, userQuestion, activeSkillInstruction);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(settings.ModelName) ? "gpt-4o" : settings.ModelName,
                ["messages"] = messages,
                ["temperature"] = settings.Temperature,
                ["max_tokens"] = 8192,
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
                        foreach (var kvp in accumulatedToolCalls.OrderBy(k => k.Key))
                        {
                            string finalId = string.IsNullOrWhiteSpace(kvp.Value.id)
                                ? $"call_{Guid.NewGuid():N}"
                                : kvp.Value.id;

                            var item = new ToolCallItem
                            {
                                Id = finalId,
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

                                if (delta.TryGetProperty("content", out var contentElem) &&
                                    contentElem.ValueKind == JsonValueKind.String)
                                {
                                    contentDelta = contentElem.GetString();
                                }

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

                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        yield return new StreamChunk(StreamChunkType.Reasoning, reasoningDelta);
                    }

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

            foreach (var kvp in accumulatedToolCalls.OrderBy(k => k.Key))
            {
                string finalId = string.IsNullOrWhiteSpace(kvp.Value.id)
                    ? $"call_{Guid.NewGuid():N}"
                    : kvp.Value.id;

                var item = new ToolCallItem
                {
                    Id = finalId,
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
