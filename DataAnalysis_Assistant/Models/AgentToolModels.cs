using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataAnalysis_Assistant.Models
{
    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionDefinition Function { get; set; } = new();

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

    public class ToolCallItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ToolCallFunction Function { get; set; } = new();

        [JsonIgnore]
        public string FunctionName => Function?.Name ?? string.Empty;

        [JsonIgnore]
        public string Arguments => Function?.Arguments ?? string.Empty;
    }

    public class ToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }

    public class ToolExecutionResult
    {
        public bool Success { get; set; } = true;

        public string Output { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }

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

    public class ChatToolResponse
    {
        public string ReasoningContent { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public List<ToolCallItem> ToolCalls { get; set; } = new();

        public string FinishReason { get; set; } = string.Empty;

        public bool IsStreamTruncated { get; set; } = false;

        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;
    }

    public enum AgentTerminationReason
    {
        None = 0,
        NormalCompleted = 1,
        MaxIterationsReached = 2,
        UserCancelled = 3,
        ConsecutiveErrorsTripped = 4  // 触发连续报错安全熔断
    }

    public class ToolCallRecordItem
    {
        public string CallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string? RawOutputPath { get; set; }
        public string? MirroredPath { get; set; }
        public bool Success { get; set; } = true;
        public List<string> ArtifactPaths { get; set; } = new();
        public int RetryCount { get; set; } = 0;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
