using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Python
{
    /// <summary>
    /// 受控 Python 代码执行引擎
    /// 支持进程树强隔离、超时中断、防 Token 爆炸输出截断与图表产物捕获
    /// </summary>
    public class PythonExecutionService
    {
        private readonly PythonEnvironmentService _envService;

        public PythonExecutionService(PythonEnvironmentService? envService = null)
        {
            _envService = envService ?? new PythonEnvironmentService();
        }

        /// <summary>
        /// 执行给定的 Python 代码并捕获输出与产物
        /// </summary>
        /// <param name="code">Python 代码字符串</param>
        /// <param name="customPythonPath">可选的自定义 Python 解释器路径</param>
        /// <param name="options">执行选项配置</param>
        /// <param name="cancellationToken">取消令牌（支持用户就地打断）</param>
        public async Task<ToolExecutionResult> ExecuteCodeAsync(
            string code,
            string? customPythonPath = null,
            PythonExecutionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolExecutionResult.CreateFailure("执行失败：提供的 Python 代码内容为空。");
            }

            options ??= new PythonExecutionOptions();

            // 1. 寻找可用 Python 解释器
            string pythonExe = PythonEnvironmentService.ResolvePythonExecutable(customPythonPath);
            if (string.IsNullOrEmpty(pythonExe))
            {
                return ToolExecutionResult.CreateFailure("执行失败：未在系统环境变量 PATH 或指定路径中找到 python.exe。请检查 Python 安装。");
            }

            // 2. 准备执行沙盒与产物收容目录 (遵循 scratch 隔离纪律)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 向上寻找工作区根目录下的 scratch/，若未定位则使用 AppData/scratch
            string scratchDir = LocateOrInitScratchDirectory(baseDir);
            string sandboxDir = Path.Combine(scratchDir, "agent_sandbox");
            Directory.CreateDirectory(sandboxDir);

            string scriptFileName = $"agent_exec_{Guid.NewGuid():N}.py";
            string scriptFilePath = Path.Combine(sandboxDir, scriptFileName);

            // 3. 记录执行前 scratch 目录内的已有图表文件，以便精确捕捉新生成的图表资产
            var existingFiles = new HashSet<string>(
                Directory.GetFiles(scratchDir, "*.*", SearchOption.AllDirectories),
                StringComparer.OrdinalIgnoreCase);

            // 4. 对代码进行智能预处理：注入中文字体补丁、Agg离线渲染后端与图表自动落地保护钩子
            string processedCode = PreprocessPythonCode(code, sandboxDir);

            // 5. 将代码写入临时执行脚本 (UTF-8 无 BOM)
            try
            {
                await File.WriteAllTextAsync(scriptFilePath, processedCode, new UTF8Encoding(false), cancellationToken);
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.CreateFailure($"写入临时脚本失败: {ex.Message}");
            }

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            int exitCode = -1;
            bool timedOut = false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    // -u: 无缓冲输出，保证实时性与捕获稳定性
                    Arguments = $"-u \"{scriptFilePath}\"",
                    WorkingDirectory = !string.IsNullOrEmpty(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory)
                        ? options.WorkingDirectory
                        : scratchDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // 设置环境变量，指定 Matplotlib 字体与输出偏好
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["AGENT_SCRATCH_DIR"] = scratchDir;

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) stderrBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    if (timeoutCts.IsCancellationRequested)
                    {
                        timedOut = true;
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }

                    if (timedOut)
                    {
                        return ToolExecutionResult.CreateFailure(
                            $"代码执行超时（超过限制 {options.TimeoutSeconds} 秒）。可能存在死循环或处理耗时过长，进程已终止。",
                            TruncateOutput(stdoutBuilder.ToString(), options));
                    }

                    return ToolExecutionResult.CreateFailure("用户已主动取消执行。", stdoutBuilder.ToString());
                }
            }
            finally
            {
                // 清理临时脚本文件
                try
                {
                    if (File.Exists(scriptFilePath))
                    {
                        File.Delete(scriptFilePath);
                    }
                }
                catch
                {
                }
            }

            // 5. 扫描捕获新生成的图表与资产文件 (.png, .jpg, .svg)
            var generatedArtifacts = new List<string>();
            try
            {
                var currentFiles = Directory.GetFiles(scratchDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in currentFiles)
                {
                    if (!existingFiles.Contains(file))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".svg" or ".csv" or ".xlsx")
                        {
                            generatedArtifacts.Add(file);
                        }
                    }
                }
            }
            catch
            {
            }

            string rawStdout = stdoutBuilder.ToString().TrimEnd();
            string rawStderr = stderrBuilder.ToString().TrimEnd();

            // 6. 执行上下文截断保护 (Compaction Gate: 防止 10 万行 print 打爆 Token)
            string safeStdout = TruncateOutput(rawStdout, options);

            if (exitCode == 0)
            {
                string outputSummary = string.IsNullOrWhiteSpace(safeStdout) ? "[代码执行成功，无标准输出]" : safeStdout;
                return ToolExecutionResult.CreateSuccess(outputSummary, generatedArtifacts);
            }
            else
            {
                string errorMsg = string.IsNullOrWhiteSpace(rawStderr)
                    ? $"Python 进程异常退出 (Exit Code: {exitCode})"
                    : rawStderr;
                return ToolExecutionResult.CreateFailure(errorMsg, safeStdout);
            }
        }

        /// <summary>
        /// 上下文保护截断机制：超过上限时截取头尾并附加提示
        /// </summary>
        public static string TruncateOutput(string output, PythonExecutionOptions options)
        {
            if (string.IsNullOrEmpty(output)) return string.Empty;

            if (output.Length <= options.MaxOutputChars)
            {
                return output;
            }

            string[] lines = output.Split('\n');
            if (lines.Length <= options.MaxOutputLines && output.Length <= options.MaxOutputChars)
            {
                return output;
            }

            // 截取前 40 行与后 15 行
            int headCount = Math.Min(40, lines.Length);
            int tailCount = Math.Min(15, lines.Length - headCount);

            var sb = new StringBuilder();
            for (int i = 0; i < headCount; i++)
            {
                sb.AppendLine(lines[i]);
            }

            sb.AppendLine();
            sb.AppendLine($"... [输出过长已自动截断，总长度 {output.Length} 字符/共 {lines.Length} 行。提示模型：请使用 df.head(), df.info() 或聚合统计指标查看] ...");
            sb.AppendLine();

            if (tailCount > 0)
            {
                for (int i = lines.Length - tailCount; i < lines.Length; i++)
                {
                    sb.AppendLine(lines[i]);
                }
            }

            return sb.ToString();
        }

        private static string LocateOrInitScratchDirectory(string baseDir)
        {
            // 向上查找到包含 .git 或 .slnx 的工程根目录
            var current = new DirectoryInfo(baseDir);
            while (current != null && current.Parent != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, "DataAnalysis_Assistant.slnx")))
                {
                    string targetScratch = Path.Combine(current.FullName, "scratch");
                    Directory.CreateDirectory(targetScratch);
                    return targetScratch;
                }
                current = current.Parent;
            }

            // 备用 fallback: 当前目录下的 scratch
            string fallback = Path.Combine(baseDir, "scratch");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>
        /// 预处理 Python 执行脚本：自动注入中文字体防乱码补丁、Agg离线后端阻断阻塞式 plt.show()，并自动兜底导出未保存的 Figure
        /// </summary>
        public static string PreprocessPythonCode(string rawCode, string outputPlotDir)
        {
            var sb = new StringBuilder();

            // 1. 顶部无感知注入 Matplotlib 中文适配与 show 屏蔽钩子
            sb.AppendLine("# === [DataAnalysis_Assistant Auto Plot & Font Header] ===");
            sb.AppendLine("try:");
            sb.AppendLine("    import matplotlib");
            sb.AppendLine("    matplotlib.use('Agg')  # 强制无 GUI 离线渲染后端，彻底避免弹窗阻塞卡死");
            sb.AppendLine("    import matplotlib.pyplot as plt");
            sb.AppendLine("    plt.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'SimSun', 'Segoe UI', 'DejaVu Sans', 'sans-serif']");
            sb.AppendLine("    plt.rcParams['axes.unicode_minus'] = False");
            sb.AppendLine("    _orig_show = plt.show");
            sb.AppendLine("    plt.show = lambda *args, **kwargs: None");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    pass");
            sb.AppendLine("# === [User Generated Code Start] ===");
            sb.AppendLine(rawCode);
            sb.AppendLine();
            sb.AppendLine("# === [Auto Figure Capture Hook] ===");
            sb.AppendLine("try:");
            sb.AppendLine("    import matplotlib.pyplot as plt");
            sb.AppendLine("    if plt.get_fignums():");
            sb.AppendLine("        import time, os");
            string safePlotDir = outputPlotDir.Replace("\\", "/");
            sb.AppendLine($"        _out_dir = r'{safePlotDir}'");
            sb.AppendLine("        os.makedirs(_out_dir, exist_ok=True)");
            sb.AppendLine("        for _idx in plt.get_fignums():");
            sb.AppendLine("            _fig = plt.figure(_idx)");
            sb.AppendLine("            _plot_name = f'chart_{int(time.time()*1000)}_{_idx}.png'");
            sb.AppendLine("            _out_path = os.path.join(_out_dir, _plot_name)");
            sb.AppendLine("            _fig.savefig(_out_path, dpi=200, bbox_inches='tight')");
            sb.AppendLine("        plt.close('all')");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    pass");

            return sb.ToString();
        }
    }
}

