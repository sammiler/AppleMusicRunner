using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AppleMusicProcessManager
{
    public enum LogLevel
    {
        DEBUG,
        INFO,
        WARNING,
        ERROR,
        OTHER
    }

    public record LogEntry(string Message, LogLevel Level, Brush Color);

    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _wrapperProcess; // 左侧日志，长期运行
        private Process? _amdProcess; // 右侧日志，每个任务启动一次
        private string? _baseDirectory;
        private bool _needCleanAll; // 是否需要清理wrapper的标志

        private readonly List<LogEntry> _wrapperLogEntries = new();
        private readonly List<LogEntry> _amdLogEntries = new();

        public MainWindow()
        {
            InitializeComponent();
            CheckEnvironmentVariable();
        }

        private void CheckEnvironmentVariable()
        {
            _baseDirectory = Environment.GetEnvironmentVariable("AMD-V2");

            if (string.IsNullOrEmpty(_baseDirectory))
            {
                string errorMessage = "错误：未设置 'AMD-V2' 环境变量。\n\n" +
                                      "请为此程序设置一个指向 AMD-V2-WSL1 Python版本项目根目录的环境变量。";
                MessageBox.Show(errorMessage, "环境配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                DirectoryPathTextBlock.Text = "错误：环境变量 'AMD-V2' 未设置！";
                UpdateStatus("准备就绪 - 但需要配置环境变量。");
                return;
            }

            if (Directory.Exists(Path.Combine(_baseDirectory, "wsl1")) &&
                Directory.Exists(Path.Combine(_baseDirectory, "AppleMusicDecrypt")) &&
                File.Exists(Path.Combine(_baseDirectory, "artists.txt")))
            {
                StartButton.IsEnabled = true;
                DirectoryPathTextBlock.Text = $"工作目录: {_baseDirectory}";
                UpdateStatus($"工作目录已从环境变量加载: {_baseDirectory}");
            }
            else
            {
                string errorMessage = $"错误：环境变量 'AMD-V2' 指向的路径无效。\n\n" +
                                      $"路径: '{_baseDirectory}'\n\n" +
                                      "请确保该目录中包含 'wsl1' 文件夹、'AppleMusicDecrypt' 文件夹和 'artists.txt' 文件。";
                MessageBox.Show(errorMessage, "路径无效", MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                DirectoryPathTextBlock.Text = $"路径无效: {_baseDirectory}";
                UpdateStatus("错误：环境变量指向的路径无效。");
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            UpdateStatus("程序关闭中，正在终止所有子进程...");
            _cancellationTokenSource?.Cancel();
            _needCleanAll = true; // 关闭时强制清理所有进程
            KillProcessesAndCleanupWSL();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StartButton.Content = "处理中...";
            _cancellationTokenSource = new CancellationTokenSource();
            try
            {
                await RunWorkflow(_cancellationTokenSource.Token);
                UpdateStatus("所有任务已成功处理完毕。");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("处理过程被用户或程序关闭取消。");
            }
            catch (Exception ex)
            {
                UpdateStatus("发生严重错误，详情请见弹窗。");
                MessageBox.Show(ex.ToString(), "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                StartButton.Content = "开始处理";
                _needCleanAll = true; // 结束后清理所有
                KillProcessesAndCleanupWSL();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                LogToWrapper("用户请求终止操作...");
                LogToAmd("用户请求终止操作...");
                _cancellationTokenSource.Cancel();
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            if (string.IsNullOrEmpty(_baseDirectory))
            {
                UpdateStatus("错误: 工作目录未设置。");
                return;
            }

            string mainArtistsFile = Path.Combine(_baseDirectory, "artists.txt");
            string targetArtistsFile = Path.Combine(_baseDirectory, "AppleMusicDecrypt", "artists.txt");
            if (!File.Exists(mainArtistsFile))
            {
                UpdateStatus($"错误: 在 '{_baseDirectory}' 中找不到 artists.txt 文件。");
                return;
            }

            var artists = await File.ReadAllLinesAsync(mainArtistsFile, token);
            for (int i = 0; i < artists.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string artist = artists[i];
                if (string.IsNullOrWhiteSpace(artist)) continue;

                ClearLogs();
                UpdateStatus($"正在处理第 {i + 1}/{artists.Length} 位艺术家: {artist}");

                bool success = false;
                while (!success && !token.IsCancellationRequested)
                {
                    await File.WriteAllTextAsync(targetArtistsFile, artist, token);
                    LogToWrapper($"已将 '{artist}' 写入目标 artists.txt。");
                    LogToAmd($"准备处理 '{artist}'。");

                    success = await RunAndMonitorProcesses(_baseDirectory, token);

                    if (!success && !token.IsCancellationRequested)
                    {
                        UpdateStatus($"艺术家 '{artist}' 的任务失败，将在 5 秒后重试...");
                        await Task.Delay(5000, token);
                    }
                }

                if (success)
                {
                    UpdateStatus($"艺术家 '{artist}' 的任务已完成。");
                    await File.WriteAllTextAsync(targetArtistsFile, string.Empty, token);
                }
            }
        }

        private async Task<bool> RunAndMonitorProcesses(string baseDirectory, CancellationToken token)
        {
            _needCleanAll = false;

            // --- 步骤 1: 确保 Wrapper-Manager 进程已启动并就绪 ---
            if (_wrapperProcess == null || _wrapperProcess.HasExited)
            {
                LogToWrapper("Wrapper-Manager 未运行，正在启动...");
                var wrapperReadyTcs = new TaskCompletionSource<bool>();

                var wrapperStartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(baseDirectory, "wsl1", "LxRunOffline.exe"),
                    Arguments = "r -n deb-amd -c \"cd /root/wm && ./wrapper-manager -host 0.0.0.0 -port 8080 -debug\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                _wrapperProcess = new Process { StartInfo = wrapperStartInfo, EnableRaisingEvents = true };

                _wrapperProcess.Exited += (s, e) =>
                {
                    Dispatcher.BeginInvoke(() => LogToWrapper("Wrapper-Manager 意外退出。"));
                    _needCleanAll = true;
                    wrapperReadyTcs.TrySetResult(false);
                };

                void HandleWrapperOutput(string data)
                {
                    LogToWrapper(data);
                    if (data.Contains("listening on", StringComparison.OrdinalIgnoreCase))
                    {
                        wrapperReadyTcs.TrySetResult(true);
                    }
                    else if (data.Contains("Wrapper down", StringComparison.OrdinalIgnoreCase))
                    {
                        _needCleanAll = true;
                        wrapperReadyTcs.TrySetResult(false);
                    }
                }

                _wrapperProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) HandleWrapperOutput(e.Data);
                };
                _wrapperProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) HandleWrapperOutput(e.Data);
                };

                _wrapperProcess.Start();
                _wrapperProcess.BeginOutputReadLine();
                _wrapperProcess.BeginErrorReadLine();
                LogToWrapper($"Wrapper-Manager 已启动 (PID: {_wrapperProcess.Id}). 等待就绪...");

                if (!await wrapperReadyTcs.Task)
                {
                    LogToWrapper("Wrapper-Manager 未能进入就绪状态。将清理并重试。");
                    KillProcessesAndCleanupWSL();
                    return false;
                }

                LogToWrapper("Wrapper-Manager 已就绪。");
            }

            // --- 步骤 2: 启动 AMD-V2 Python 进程并使用无死锁的方式监控 ---
            bool success;
            var amdStartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(baseDirectory, "wsl1", "LxRunOffline.exe"),
                Arguments = "r -n deb-amd -c \"/root/.local/bin/poetry run python3 main.py\"",
                WorkingDirectory = Path.Combine(baseDirectory, "AppleMusicDecrypt"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            _amdProcess = new Process { StartInfo = amdStartInfo, EnableRaisingEvents = true };
            try
            {
                var outputTcs = new TaskCompletionSource<bool>();

                void HandleAmdOutput(string data)
                {
                    LogToAmd(data);
                    if (data.Contains("All tasks completed.", StringComparison.OrdinalIgnoreCase))
                    {
                        outputTcs.TrySetResult(true);
                    }
                }

                _amdProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) HandleAmdOutput(e.Data);
                };
                _amdProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) LogToAmd($"ERROR: {e.Data}");
                };

                _amdProcess.Start();
                _amdProcess.BeginOutputReadLine();
                _amdProcess.BeginErrorReadLine();
                LogToAmd($"AMD-V2 进程已启动 (PID: {_amdProcess.Id}).");

                Task processExitTask = _amdProcess.WaitForExitAsync(token);
                Task completedTask = await Task.WhenAny(outputTcs.Task, processExitTask);

                if (completedTask == outputTcs.Task)
                {
                    LogToAmd("检测到任务成功完成的信号。");
                    success = await outputTcs.Task;
                }
                else
                {
                    LogToAmd("检测到 AMD-V2 进程已退出。");
                    success = false;
                }
            }
            finally
            {
                KillProcessesAndCleanupWSL();
            }

            return success;
        }

        private void KillProcess(Process? process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception)
            {
                /* Ignore */
            }
        }

        private void KillProcessesAndCleanupWSL()
        {
            KillProcess(_amdProcess);
            _amdProcess = null;

            if (_needCleanAll)
            {
                KillProcess(_wrapperProcess);
                _wrapperProcess = null;
            }

            if (string.IsNullOrEmpty(_baseDirectory)) return;

            try
            {
                string cleanupCommand;
                if (_needCleanAll)
                {
                    LogToWrapper("正在执行完全清理...");
                    cleanupCommand = "pkill -f 'wrapper-manager'; pkill -f 'python3 main.py'";
                }
                else
                {
                    LogToWrapper("正在执行部分清理 (AMD-V2 only)...");
                    cleanupCommand = "pkill -f 'python3 main.py'";
                }

                var processInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(_baseDirectory, "wsl1", "LxRunOffline.exe"),
                    Arguments = $"r -n deb-amd -c \"{cleanupCommand}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var cleanupProcess = Process.Start(processInfo);
                cleanupProcess?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => UpdateStatus($"WSL cleanup failed: {ex.Message}"));
            }
        }

        #region UI Update Helpers and Log Management

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            RefreshLogs(WrapperLogRtb, _wrapperLogEntries, GetWrapperFilterState());
            RefreshLogs(AmdLogRtb, _amdLogEntries, GetAmdFilterState());
        }

        private void Filter_Changed_TextBox(object sender, TextChangedEventArgs e)
        {
            RefreshLogs(WrapperLogRtb, _wrapperLogEntries, GetWrapperFilterState());
            RefreshLogs(AmdLogRtb, _amdLogEntries, GetAmdFilterState());
        }

        private LogEntry ParseLogLine(string line)
        {
            var upperLine = line.ToUpperInvariant();
            if (upperLine.Contains("ERROR") || upperLine.Contains("FATAL") || upperLine.Contains("CRITICAL"))
                return new LogEntry(line, LogLevel.ERROR, Brushes.Red);
            if (upperLine.Contains("WARNING") || upperLine.Contains("WARN"))
                return new LogEntry(line, LogLevel.WARNING, Brushes.Orange);
            if (upperLine.Contains("DEBUG")) return new LogEntry(line, LogLevel.DEBUG, Brushes.Gray);
            if (upperLine.Contains("INFO")) return new LogEntry(line, LogLevel.INFO, Brushes.DodgerBlue);
            return new LogEntry(line, LogLevel.OTHER, SystemColors.WindowTextBrush);
        }

        private void LogToWrapper(string message)
        {
            var logEntry = ParseLogLine(message);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _wrapperLogEntries.Add(logEntry);
                    if (ShouldDisplay(logEntry, GetWrapperFilterState()))
                    {
                        AppendToRichTextBox(WrapperLogRtb, logEntry);
                    }
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void LogToAmd(string message)
        {
            var logEntry = ParseLogLine(message);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _amdLogEntries.Add(logEntry);
                    if (ShouldDisplay(logEntry, GetAmdFilterState()))
                    {
                        AppendToRichTextBox(AmdLogRtb, logEntry);
                    }
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void AppendToRichTextBox(RichTextBox rtb, LogEntry logEntry)
        {
            var paragraph = new Paragraph(new Run(logEntry.Message))
                { Foreground = logEntry.Color, Margin = new Thickness(0) };
            rtb.Document.Blocks.Add(paragraph);
            rtb.ScrollToEnd();
        }

        private bool ShouldDisplay(LogEntry entry,
            (bool info, bool warn, bool err, bool dbg, bool oth, string keyword) filter)
        {
            if (!string.IsNullOrEmpty(filter.keyword) &&
                !entry.Message.Contains(filter.keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return entry.Level switch
            {
                LogLevel.INFO => filter.info,
                LogLevel.WARNING => filter.warn,
                LogLevel.ERROR => filter.err,
                LogLevel.DEBUG => filter.dbg,
                LogLevel.OTHER => filter.oth,
                _ => true
            };
        }

        private void RefreshLogs(RichTextBox rtb, List<LogEntry> source,
            (bool info, bool warn, bool err, bool dbg, bool oth, string keyword) filter)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    rtb.Document.Blocks.Clear();
                    foreach (var entry in source)
                    {
                        if (ShouldDisplay(entry, filter))
                        {
                            var paragraph = new Paragraph(new Run(entry.Message))
                                { Foreground = entry.Color, Margin = new Thickness(0) };
                            rtb.Document.Blocks.Add(paragraph);
                        }
                    }

                    rtb.ScrollToEnd();
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private (bool, bool, bool, bool, bool, string) GetWrapperFilterState() => (WrapperShowInfo.IsChecked == true,
            WrapperShowWarning.IsChecked == true, WrapperShowError.IsChecked == true,
            WrapperShowDebug.IsChecked == true, WrapperShowOther.IsChecked == true, WrapperKeywordFilter.Text);

        private (bool, bool, bool, bool, bool, string) GetAmdFilterState() => (AmdShowInfo.IsChecked == true,
            AmdShowWarning.IsChecked == true, AmdShowError.IsChecked == true, AmdShowDebug.IsChecked == true,
            AmdShowOther.IsChecked == true, AmdKeywordFilter.Text);

        private void ClearLogs()
        {
            _wrapperLogEntries.Clear();
            _amdLogEntries.Clear();
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    WrapperLogRtb.Document.Blocks.Clear();
                    AmdLogRtb.Document.Blocks.Clear();
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void UpdateStatus(string message)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() => { StatusTextBlock.Text = message; });
            }
            catch (TaskCanceledException)
            {
            }
        }

        #endregion
    }
}