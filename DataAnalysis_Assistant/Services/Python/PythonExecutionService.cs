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
    public class PythonExecutionService
    {
        private readonly PythonEnvironmentService _envService;

        public PythonExecutionService(PythonEnvironmentService? envService = null)
        {
            _envService = envService ?? new PythonEnvironmentService();
        }

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

            string pythonExe = PythonEnvironmentService.ResolvePythonExecutable(customPythonPath);
            if (string.IsNullOrEmpty(pythonExe))
            {
                return ToolExecutionResult.CreateFailure("执行失败：未在系统环境变量 PATH 或指定路径中找到 python.exe。请检查 Python 安装。");
            }

            string scratchDir = LocateOrInitScratchDirectory(options.TargetGroupId);
            string sandboxDir = LocateOrInitSandboxDirectory(options.TargetGroupId);

            string scriptFileName = $"agent_exec_{Guid.NewGuid():N}.py";
            string scriptFilePath = Path.Combine(sandboxDir, scriptFileName);

            var existingFiles = new HashSet<string>(
                Directory.GetFiles(sandboxDir, "*.*", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);

            var existingScratchRootFiles = new HashSet<string>(
                Directory.GetFiles(scratchDir, "*.*", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);

            var (processedCode, headerLineCount) = PreprocessPythonCode(code, sandboxDir);

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

                int effectiveTimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 30, 120);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeoutSeconds));
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
                        string heavyHint = options.IsHeavyWork
                            ? "已开启 heavy_work 扩展窗口但仍然超时，请尝试在脚本中进行数据降采样（如 df.iloc[::5]）或降低图片 dpi。"
                            : "若涉及多文件批处理或大数据高密渲染，请在 run_python 参数中声明 heavy_work=true 或在设置中延长超时时间。";

                        return ToolExecutionResult.CreateFailure(
                            $"[ERR_TYPE:Timeout] 代码执行超时（超过限制 {effectiveTimeoutSeconds} 秒）。{heavyHint}",
                            TruncateOutput(stdoutBuilder.ToString(), options));
                    }

                    return ToolExecutionResult.CreateFailure("用户已主动取消执行。", stdoutBuilder.ToString());
                }
            }
            finally
            {
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

            // 5. 扫描捕获新生成的图表与资产文件（严格限制在 agent_sandbox 目录，确保仅捕获带编号的沙盒成果）
            var generatedArtifacts = new List<string>();
            var seenArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAddArtifact(string? rawPath)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) return;
                try
                {
                    string fullPath = Path.GetFullPath(rawPath);
                    // 关键约束：必须存在物理文件、必须严格位于 agent_sandbox 目录下、且不重复
                    if (File.Exists(fullPath) &&
                        IsInSandboxDirectory(fullPath, options.TargetGroupId) &&
                        seenArtifacts.Add(fullPath))
                    {
                        generatedArtifacts.Add(fullPath);
                    }
                }
                catch
                {
                }
            }

            try
            {
                var currentFiles = Directory.GetFiles(sandboxDir, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in currentFiles)
                {
                    string fullPath = Path.GetFullPath(file);
                    if (!existingFiles.Contains(fullPath))
                    {
                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".svg" or ".csv" or ".xlsx" or ".html")
                        {
                            TryAddArtifact(fullPath);
                        }
                    }
                }
            }
            catch
            {
            }

            string rawStdout = stdoutBuilder.ToString().TrimEnd();
            string rawStderr = stderrBuilder.ToString().TrimEnd();

            try
            {
                var capturedMatches = System.Text.RegularExpressions.Regex.Matches(
                    rawStdout,
                    @"\[ARTIFACT_CAPTURED\]:\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                foreach (System.Text.RegularExpressions.Match match in capturedMatches)
                {
                    string capturedPath = match.Groups[1].Value.Trim().Trim('"', '\'', '\r');
                    TryAddArtifact(capturedPath);
                }
            }
            catch
            {
            }

            try
            {
                var currentScratchRootFiles = Directory.GetFiles(scratchDir, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in currentScratchRootFiles)
                {
                    string fullPath = Path.GetFullPath(file);
                    if (!existingScratchRootFiles.Contains(fullPath))
                    {
                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".svg" or ".pdf" or ".csv" or ".xlsx" or ".html")
                        {
                            string rawName = Path.GetFileNameWithoutExtension(fullPath);
                            string hex6 = Guid.NewGuid().ToString("N")[..6];
                            string uniqueName = $"{rawName}_{DateTimeOffset.Now.ToUnixTimeSeconds()}_{hex6}{ext}";
                            string mirrorPath = Path.Combine(sandboxDir, uniqueName);
                            try
                            {
                                File.Copy(fullPath, mirrorPath, overwrite: true);
                                // 安全清理 scratch 根目录遗留的原文件，保持工作区清爽
                                try { File.Delete(fullPath); } catch { }
                                TryAddArtifact(mirrorPath);
                            }
                            catch
                            {
                                TryAddArtifact(fullPath);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            var warningBuilder = new StringBuilder();
            if (rawStderr.Contains("[ARTIFACT_COPY_WARNING]"))
            {
                var lines = rawStderr.Split('\n');
                foreach (var l in lines)
                {
                    if (l.Contains("[ARTIFACT_COPY_WARNING]"))
                    {
                        warningBuilder.AppendLine(l.Trim());
                    }
                }
            }

            // 6. 执行上下文截断保护 (Compaction Gate: 防止 10 万行 print 打爆 Token)
            string safeStdout = TruncateOutput(rawStdout, options);

            string errorMsg = string.IsNullOrWhiteSpace(rawStderr)
                ? $"Python 进程异常退出 (Exit Code: {exitCode})"
                : rawStderr;

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(rawStderr))
            {
                // 使用行首多行正则识别 SyntaxError/IndentationError，避免 print 误伤
                if (System.Text.RegularExpressions.Regex.IsMatch(rawStderr, @"^\s*(Syntax|Indentation)Error:", System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    string escapedPath = System.Text.RegularExpressions.Regex.Escape(scriptFilePath);
                    string pattern = $@"File ""{escapedPath}"", line (\d+)";

                    string correctedStderr = System.Text.RegularExpressions.Regex.Replace(
                        rawStderr,
                        pattern,
                        m =>
                        {
                            if (int.TryParse(m.Groups[1].Value, out int lineNum) && lineNum > headerLineCount)
                            {
                                return $"File \"{scriptFilePath}\", line {lineNum - headerLineCount} (相对用户代码第 {lineNum - headerLineCount} 行)";
                            }
                            return m.Value;
                        });

                    errorMsg = $"[ERR_TYPE:SyntaxError]\n{correctedStderr}";
                }
            }

            bool hasFatalError = errorMsg.Contains("[ERR_TYPE:SyntaxError]") ||
                                 errorMsg.Contains("[ERR_TYPE:FileNotFound]") ||
                                 errorMsg.Contains("[ERR_TYPE:ModuleNotFound]");

            if (exitCode == 0 || (generatedArtifacts.Count > 0 && !hasFatalError))
            {
                string outputSummary = string.IsNullOrWhiteSpace(safeStdout) ? "[代码执行成功，产物已就绪]" : safeStdout;
                if (exitCode != 0)
                {
                    outputSummary += $"\n\n[警告] 脚本执行超时或非正常终止 (ExitCode: {exitCode})，仅捕获到部分产物（共 {generatedArtifacts.Count} 个），请核对是否已覆盖全部分析目标或考虑降采样/分步执行。";
                }
                if (warningBuilder.Length > 0)
                {
                    outputSummary += "\n\n[产物警告]:\n" + warningBuilder.ToString().Trim();
                }
                return ToolExecutionResult.CreateSuccess(outputSummary, generatedArtifacts);
            }
            else
            {
                return ToolExecutionResult.CreateFailure(errorMsg, safeStdout);
            }
        }

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

        public static string LocateOrInitScratchDirectory(string? groupId = null)
        {
            try
            {
                return Persistence.LibraryPersistenceService.Instance.GetScratchDirectoryForGroup(groupId);
            }
            catch
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string targetId = string.IsNullOrWhiteSpace(groupId) ? Persistence.LibraryPersistenceService.StandaloneGroupId : groupId.Trim();
                string fallback = Path.Combine(appData, "DataAnalysis_Assistant", "document_groups", targetId, "scratch");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        public static string LocateOrInitSandboxDirectory(string? groupId = null)
        {
            try
            {
                return Persistence.LibraryPersistenceService.Instance.GetSandboxDirectoryForGroup(groupId);
            }
            catch
            {
                string scratchDir = LocateOrInitScratchDirectory(groupId);
                string sandboxDir = Path.Combine(scratchDir, "agent_sandbox");
                Directory.CreateDirectory(sandboxDir);
                return sandboxDir;
            }
        }

        public static bool IsInSandboxDirectory(string? filePath, string? targetGroupId = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            try
            {
                string fullPath = Path.GetFullPath(filePath);

                if (!string.IsNullOrWhiteSpace(targetGroupId))
                {
                    string groupSandbox = Path.GetFullPath(LocateOrInitSandboxDirectory(targetGroupId));
                    string prefix = groupSandbox.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                }

                string normalized = fullPath.Replace('/', '\\');
                string sep = Path.DirectorySeparatorChar.ToString();
                return normalized.Contains($"{sep}scratch{sep}agent_sandbox{sep}", StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith($"{sep}scratch{sep}agent_sandbox", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains($"{sep}agent_sandbox{sep}", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static (string FullCode, int HeaderLineCount) PreprocessPythonCode(string rawCode, string outputPlotDir)
        {
            var sb = new StringBuilder();
            string safePlotDir = outputPlotDir.Replace("\\", "/");

            sb.AppendLine("# === [DataAnalysis_Assistant Auto Plot & Font Header] ===");
            sb.AppendLine("import sys");
            sb.AppendLine("def _data_analysis_excepthook(exctype, value, tb):");
            sb.AppendLine("    try:");
            sb.AppendLine("        if issubclass(exctype, FileNotFoundError):");
            sb.AppendLine("            sys.stderr.write('\\n[ERR_TYPE:FileNotFound]\\n')");
            sb.AppendLine("        elif issubclass(exctype, (SyntaxError, IndentationError)):");
            sb.AppendLine("            sys.stderr.write('\\n[ERR_TYPE:SyntaxError]\\n')");
            sb.AppendLine("        elif issubclass(exctype, ModuleNotFoundError):");
            sb.AppendLine("            sys.stderr.write('\\n[ERR_TYPE:ModuleNotFound]\\n')");
            sb.AppendLine("        elif issubclass(exctype, KeyError):");
            sb.AppendLine("            sys.stderr.write('\\n[ERR_TYPE:KeyError]\\n')");
            sb.AppendLine("    except Exception:");
            sb.AppendLine("        pass");
            sb.AppendLine("    sys.__excepthook__(exctype, value, tb)");
            sb.AppendLine("sys.excepthook = _data_analysis_excepthook");
            sb.AppendLine();
            sb.AppendLine("# 基础离线绘图环境与中文字体配置 (高优先级独立保护)");
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
            sb.AppendLine();
            sb.AppendLine("# 图表产物自动镜像捕获代理");
            sb.AppendLine("try:");
            sb.AppendLine("    import os, re, shutil, uuid, time, matplotlib.figure");
            sb.AppendLine($"    _target_scratch = r\"\"\"{safePlotDir}\"\"\"");
            sb.AppendLine("    os.makedirs(_target_scratch, exist_ok=True)");
            sb.AppendLine("    _orig_fig_savefig = matplotlib.figure.Figure.savefig");
            sb.AppendLine("    _orig_plt_savefig = plt.savefig");
            sb.AppendLine("    _in_savefig_interceptor = False");
            sb.AppendLine("    _cjk_pattern = re.compile(r'[\\u4e00-\\u9fff\\u3000-\\u303f\\uff00-\\uffef]')");
            sb.AppendLine("    _cjk_font_list = ['Microsoft YaHei', 'SimHei', 'SimSun', 'Segoe UI', 'DejaVu Sans']");
            sb.AppendLine("    def _ensure_cjk_fonts_before_save(fig_obj):");
            sb.AppendLine("        try:");
            sb.AppendLine("            cur = plt.rcParams.get('font.sans-serif', [])");
            sb.AppendLine("            cur_list = [cur] if isinstance(cur, str) else list(cur)");
            sb.AppendLine("            plt.rcParams['font.sans-serif'] = _cjk_font_list + [f for f in cur_list if f not in _cjk_font_list]");
            sb.AppendLine("            plt.rcParams['axes.unicode_minus'] = False");
            sb.AppendLine("            if fig_obj is not None:");
            sb.AppendLine("                from matplotlib.text import Text");
            sb.AppendLine("                for ax in fig_obj.get_axes():");
            sb.AppendLine("                    for lbl in list(ax.get_xticklabels()) + list(ax.get_yticklabels()):");
            sb.AppendLine("                        lbl.set_fontfamily(_cjk_font_list)");
            sb.AppendLine("                for txt in fig_obj.findobj(Text):");
            sb.AppendLine("                    if _cjk_pattern.search(txt.get_text() or ''):");
            sb.AppendLine("                        txt.set_fontfamily(_cjk_font_list)");
            sb.AppendLine("        except Exception:");
            sb.AppendLine("            pass");
            sb.AppendLine("    def _do_mirror_artifact(fname):");
            sb.AppendLine("        if not fname: return");
            sb.AppendLine("        try:");
            sb.AppendLine("            abs_fname = os.path.abspath(str(fname))");
            sb.AppendLine("            abs_scratch = os.path.abspath(_target_scratch)");
            sb.AppendLine("            raw_name = os.path.basename(abs_fname)");
            sb.AppendLine("            stem, ext = os.path.splitext(raw_name)");
            sb.AppendLine("            if not ext: ext = '.png'");
            sb.AppendLine("            unique_name = f\"{stem}_{int(time.time())}_{uuid.uuid4().hex[:6]}{ext}\"");
            sb.AppendLine("            mirror_path = os.path.join(abs_scratch, unique_name)");
            sb.AppendLine("            if os.path.exists(abs_fname):");
            sb.AppendLine("                shutil.copy2(abs_fname, mirror_path)");
            sb.AppendLine("                # 若原始图片直接生成在 scratch 根目录下，在完成沙盒镜像后安全清理无编号的临时原图");
            sb.AppendLine("                try:");
            sb.AppendLine("                    scratch_root = os.path.dirname(abs_scratch)");
            sb.AppendLine("                    if os.path.dirname(abs_fname) == scratch_root and os.path.exists(abs_fname):");
            sb.AppendLine("                        os.remove(abs_fname)");
            sb.AppendLine("                except Exception:");
            sb.AppendLine("                    pass");
            sb.AppendLine("                print(f'[ARTIFACT_CAPTURED]: {mirror_path}')");
            sb.AppendLine("        except Exception as e:");
            sb.AppendLine("            sys.stderr.write(f'[ARTIFACT_COPY_WARNING]: 产物镜像失败: {str(e)}\\n')");
            sb.AppendLine("    def _safe_intercept_fig_savefig(self, *args, **kwargs):");
            sb.AppendLine("        global _in_savefig_interceptor");
            sb.AppendLine("        was_in = _in_savefig_interceptor");
            sb.AppendLine("        _in_savefig_interceptor = True");
            sb.AppendLine("        try:");
            sb.AppendLine("            if not was_in:");
            sb.AppendLine("                _ensure_cjk_fonts_before_save(self)");
            sb.AppendLine("            res = _orig_fig_savefig(self, *args, **kwargs)");
            sb.AppendLine("            if not was_in:");
            sb.AppendLine("                fname = args[0] if len(args) > 0 else kwargs.get('fname', None)");
            sb.AppendLine("                _do_mirror_artifact(fname)");
            sb.AppendLine("            return res");
            sb.AppendLine("        finally:");
            sb.AppendLine("            _in_savefig_interceptor = was_in");
            sb.AppendLine("    def _safe_intercept_plt_savefig(*args, **kwargs):");
            sb.AppendLine("        global _in_savefig_interceptor");
            sb.AppendLine("        was_in = _in_savefig_interceptor");
            sb.AppendLine("        _in_savefig_interceptor = True");
            sb.AppendLine("        try:");
            sb.AppendLine("            if not was_in:");
            sb.AppendLine("                _ensure_cjk_fonts_before_save(plt.gcf())");
            sb.AppendLine("            res = _orig_plt_savefig(*args, **kwargs)");
            sb.AppendLine("            if not was_in:");
            sb.AppendLine("                fname = args[0] if len(args) > 0 else kwargs.get('fname', None)");
            sb.AppendLine("                _do_mirror_artifact(fname)");
            sb.AppendLine("            return res");
            sb.AppendLine("        finally:");
            sb.AppendLine("            _in_savefig_interceptor = was_in");
            sb.AppendLine("    matplotlib.figure.Figure.savefig = _safe_intercept_fig_savefig");
            sb.AppendLine("    plt.savefig = _safe_intercept_plt_savefig");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    pass");

            string headerText = sb.ToString();
            const string sentinelLine = "# === [User Generated Code Start] ===\n";
            string prefixText = headerText + sentinelLine;
            int headerLineCount = prefixText.Count(c => c == '\n');

            var fullSb = new StringBuilder();
            fullSb.Append(prefixText);
            fullSb.AppendLine(rawCode);
            fullSb.AppendLine();

            return (fullSb.ToString(), headerLineCount);
        }
    }
}
