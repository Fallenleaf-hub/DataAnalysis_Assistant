using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// 符合 OpenAI / DeepSeek 标准的工具定义模型
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionDefinition Function { get; set; } = new();

        /// <summary>
        /// 便捷工厂方法：创建函数类型的工具定义
        /// </summary>
        public static ToolDefinition CreateFunction(
            string name,
            string description,
            object? parametersSchema = null)
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = name,
                    Description = description,
                    Parameters = parametersSchema ?? new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>(),
                        required = Array.Empty<string>()
                    }
                }
            };
        }
    }

    /// <summary>
    /// 函数定义结构
    /// </summary>
    public class FunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public object Parameters { get; set; } = new
        {
            type = "object",
            properties = new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// 模型发起的单次工具调用项
    /// </summary>
    public class ToolCallItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ToolCallFunction Function { get; set; } = new();

        // 快捷访问属性
        [JsonIgnore]
        public string FunctionName => Function?.Name ?? string.Empty;

        [JsonIgnore]
        public string Arguments => Function?.Arguments ?? string.Empty;
    }

    /// <summary>
    /// 工具调用的具体函数名称与参数字符串
    /// </summary>
    public class ToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }

    /// <summary>
    /// 本地工具执行产物结果
    /// </summary>
    public class ToolExecutionResult
    {
        /// <summary>
        /// 执行是否成功
        /// </summary>
        public bool Success { get; set; } = true;

        /// <summary>
        /// 输出文本（stdout / 返回数据 / 截断摘要）
        /// </summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>
        /// 错误详情（stderr / 异常栈）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 生成的本地资产（图表、导出文档等本地绝对路径）
        /// </summary>
        public List<string> GeneratedArtifacts { get; set; } = new();

        public static ToolExecutionResult CreateSuccess(string output, List<string>? artifacts = null)
        {
            return new ToolExecutionResult
            {
                Success = true,
                Output = output,
                GeneratedArtifacts = artifacts ?? new()
            };
        }

        public static ToolExecutionResult CreateFailure(string errorMessage, string? partialOutput = null)
        {
            return new ToolExecutionResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                Output = partialOutput ?? string.Empty
            };
        }

        public static ToolExecutionResult Ok(string output, List<string>? artifacts = null) => CreateSuccess(output, artifacts);
        public static ToolExecutionResult Failed(string errorMessage, string? partialOutput = null) => CreateFailure(errorMessage, partialOutput);
    }

    /// <summary>
    /// 包含工具调用的非流式/最终响应模型
    /// </summary>
    public class ChatToolResponse
    {
        /// <summary>
        /// 模型思考/推理过程文本 (DeepSeek reasoning_content)
        /// </summary>
        public string ReasoningContent { get; set; } = string.Empty;

        /// <summary>
        /// 模型输出的正式文本结论
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 模型发起的工具调用列表（若无则为空列表）
        /// </summary>
        public List<ToolCallItem> ToolCalls { get; set; } = new();

        /// <summary>
        /// 结束原因，如 "tool_calls", "stop", "length"
        /// </summary>
        public string FinishReason { get; set; } = string.Empty;

        /// <summary>
        /// 是否包含工具调用请求
        /// </summary>
        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;
    }
}

