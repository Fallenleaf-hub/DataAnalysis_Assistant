using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Tools
{
    /// <summary>
    /// Agent 原生工具抽象接口
    /// </summary>
    public interface IAgentTool
    {
        /// <summary>
        /// 工具函数唯一标识名称（对应大模型 tool_call.function.name）
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 工具详细功能描述与调用指南
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 符合 OpenAI/DeepSeek 规范的 Schema 定义
        /// </summary>
        ToolDefinition Definition { get; }

        /// <summary>
        /// 是否需要安全刹车层进行用户交互式二次确认
        /// </summary>
        bool RequiresUserApproval { get; }

        /// <summary>
        /// 执行该工具的具体逻辑
        /// </summary>
        /// <param name="argumentsJson">大模型给出的参数 JSON 字符串</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>工具执行结果</returns>
        Task<ToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);
    }
}

