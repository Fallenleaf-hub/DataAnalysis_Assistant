using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Tools
{
    public interface IAgentTool
    {
        string Name { get; }

        string Description { get; }

        ToolDefinition Definition { get; }

        /// 是否需要安全刹车层进行用户交互式二次确认
        bool RequiresUserApproval { get; }

        Task<ToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);
    }
}
