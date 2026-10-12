using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Python
{
    /// <summary>
    /// Python 本地运行环境诊断与探测服务
    /// </summary>
    public class PythonEnvironmentService
    {
        /// <summary>
        /// 全面探测与评估当前系统 Python 环境与常用数据分析库状态
        /// </summary>
        /// <param name="customPythonPath">用户在设置中自定义的 Python 绝对路径（可选）</param>
        public async Task<PythonEnvironmentStatus> DetectEnvironmentAsync(
            string? customPythonPath = null,
            CancellationToken cancellationToken = default)
        {
            var status = new PythonEnvironmentStatus();

            // 1. 确定 Python 执行路径
            string pythonExe = ResolvePythonExecutable(customPythonPath);
            if (string.IsNullOrEmpty(pythonExe))
            {
                status.IsInstalled = false;
                status.ErrorMessage = "未检测到可用的 Python 运行时。请确保系统已安装 Python 并在 PATH 环境变量中，或在设置中手动指定 python.exe 绝对路径。";
                return status;
            }

            status.PythonPath = pythonExe;

            // 2. 获取 Python 版本信息
            try
            {
                var (exitCode, stdout, stderr) = await RunCommandAsync(pythonExe, "--version", 5, cancellationToken);
                string versionOutput = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(versionOutput))
                {
                    status.IsInstalled = true;
                    status.Version = versionOutput.Trim();
                }
                else
                {
                    status.IsInstalled = false;
                    status.ErrorMessage = $"执行 {pythonExe} --version 失败: {stderr}";
                    return status;
                }
            }
            catch (Exception ex)
            {
                status.IsInstalled = false;
                status.ErrorMessage = $"启动 Python 失败: {ex.Message}";
                return status;
            }

            // 3. 诊断核心数据分析库安装状态 (采用 importlib.util 单行探测，避免嵌套引号与多语句语法错误)
            string checkScript = "import importlib.util as u; mods=['pandas','matplotlib','numpy','seaborn','openpyxl']; print(','.join(['1' if u.find_spec(m) else '0' for m in mods]))";

            try
            {
                var (exitCode, stdout, stderr) = await RunCommandAsync(pythonExe, $"-c \"{checkScript}\"", 10, cancellationToken);
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    string[] parts = stdout.Trim().Split(',');
                    if (parts.Length >= 5)
                    {
                        status.HasPandas = parts[0].Trim() == "1";
                        status.HasMatplotlib = parts[1].Trim() == "1";
                        status.HasNumpy = parts[2].Trim() == "1";
                        status.HasSeaborn = parts[3].Trim() == "1";
                        status.HasOpenPyXl = parts[4].Trim() == "1";
                    }
                }
            }
            catch
            {
                // 忽略探测探测库时的局部异常
            }

            return status;
        }

        /// <summary>
        /// 解析可用的 python 路径
        /// </summary>
        public static string ResolvePythonExecutable(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                return customPath.Trim();
            }

            // 检查系统 PATH
            string[] candidates = new[] { "python.exe", "python3.exe", "py.exe" };
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var candidate in candidates)
            {
                foreach (var dir in paths)
                {
                    try
                    {
                        string fullPath = Path.Combine(dir.Trim(), candidate);
                        if (File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return string.Empty;
        }

        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(
            string fileName,
            string arguments,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
            }
            catch (OperationCanceledException)
            {
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
                throw;
            }
        }
    }
}

