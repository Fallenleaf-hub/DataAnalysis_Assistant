using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Data_Analysis.Models;

namespace Data_Analysis.Services
{
    /// <summary>
    /// 对话轮次模型 (用于组装 LLM messages 历史)
    /// </summary>
    public class ChatTurn
    {
        public string Role { get; set; } = string.Empty; // "system", "user", "assistant"
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// OpenAI 兼容 LLM SSE 流式对话服务
    /// 支持 OpenAI、DeepSeek、Ollama 等标准接口
    /// </summary>
    public class LlmChatService
    {
        // 复用单例 HttpClient，避免套接字耗尽
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        /// <summary>
        /// 发起兼容 OpenAI SSE 标准的流式对话补全
        /// </summary>
        public async IAsyncEnumerable<string> StreamChatCompletionAsync(
            AppSettings settings,
            List<ChatTurn> history,
            string documentContext,
            string userQuestion,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings), "配置项 AppSettings 不能为空。");
            }

            // 1. 构建端点 URL (自动容错与补齐)
            string endpoint = (settings.BaseUrl ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/chat/completions";
            }

            // 2. 组装 messages 消息列表
            var messages = new List<object>();

            // 2.1 组装 System 消息 (系统提示词 + 可选文档上下文)
            var sbSystem = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                sbSystem.AppendLine(settings.SystemPrompt.Trim());
            }

            if (!string.IsNullOrWhiteSpace(documentContext))
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine("================ 当前参考文档内容 ================");
                sbSystem.AppendLine(documentContext.Trim());
                sbSystem.AppendLine("==================================================");
                sbSystem.AppendLine("提示：请优先依据上述提供的文档内容回答用户提问；若文档未提及，请明确说明。");
            }

            if (sbSystem.Length > 0)
            {
                messages.Add(new { role = "system", content = sbSystem.ToString() });
            }

            // 2.2 组装历史对话
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (!string.IsNullOrWhiteSpace(turn.Content))
                    {
                        messages.Add(new { role = turn.Role.ToLowerInvariant(), content = turn.Content });
                    }
                }
            }

            // 2.3 组装当前用户问题
            messages.Add(new { role = "user", content = userQuestion });

            // 3. 构建请求载荷
            var requestPayload = new
            {
                model = string.IsNullOrWhiteSpace(settings.ModelName) ? "gpt-4o" : settings.ModelName,
                messages,
                temperature = settings.Temperature,
                max_tokens = settings.MaxContextLength > 0 ? settings.MaxContextLength : 4096,
                stream = true
            };

            string jsonBody = JsonSerializer.Serialize(requestPayload);

            // 4. 发送 HTTP POST 请求
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            // 设置 Authorization 头 (Ollama 可留空或任意字符)
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

            // 5. 校验响应状态
            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    // 尝试解析 OpenAI 标准错误响应结构 {"error": {"message": "..."}}
                    using var doc = JsonDocument.Parse(errorDetail);
                    if (doc.RootElement.TryGetProperty("error", out var errorElem) &&
                        errorElem.TryGetProperty("message", out var msgElem))
                    {
                        errorDetail = msgElem.GetString() ?? errorDetail;
                    }
                }
                catch
                {
                    // 忽略 JSON 解析异常，直接抛出原文
                }

                throw new Exception($"大模型接口响应异常 [{(int)response.StatusCode} {response.ReasonPhrase}]：{errorDetail}");
            }

            // 6. 解析 SSE 流式数据
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                string? line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                line = line.Trim();

                // 处理 SSE "data:" 协议行
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    string data = line.Substring(5).Trim();

                    // 流结束标志
                    if (data == "[DONE]")
                    {
                        yield break;
                    }

                    string? contentDelta = null;
                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(data);
                        var root = jsonDoc.RootElement;

                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("delta", out var delta) &&
                                delta.TryGetProperty("content", out var contentElem) &&
                                contentElem.ValueKind == JsonValueKind.String)
                            {
                                contentDelta = contentElem.GetString();
                            }
                        }
                    }
                    catch
                    {
                        // 忽略格式不规范的 SSE 中间数据帧
                    }

                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        yield return contentDelta;
                    }
                }
            }
        }
    }
}
