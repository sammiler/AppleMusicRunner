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
    // 定义日志级别枚举和日志条目记录
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
        private CancellationTokenSource _cancellationTokenSource;
        private Process _wrapperProcess;
        private Process _amdProcess;

        // 用于存储所有日志，以便重新过滤
        private readonly List<LogEntry> _wrapperLogEntries = new();
        private readonly List<LogEntry> _amdLogEntries = new();

        public MainWindow()
        {
            InitializeComponent();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            UpdateStatus("Application closing, terminating all child processes...");
            _cancellationTokenSource?.Cancel();
            KillProcess(_wrapperProcess);
            KillProcess(_amdProcess);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StartButton.Content = "Processing...";
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await RunWorkflow(_cancellationTokenSource.Token);
                UpdateStatus("All artists have been processed successfully.");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Processing cancelled by user or application exit.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"An error occurred. See message box for details.");
                MessageBox.Show(ex.ToString(), "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StartButton.IsEnabled = true;
                StartButton.Content = "Start Processing";
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
#if DEBUG
            string baseDirectory = Environment.CurrentDirectory;

#else
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
#endif
            string mainArtistsFile = Path.Combine(baseDirectory, "artists.txt");
            string targetArtistsFile = Path.Combine(baseDirectory, "AppleMusicDecrypt", "artists.txt");

            if (!File.Exists(mainArtistsFile))
            {
                UpdateStatus($"Error: Main artists file not found at {mainArtistsFile}");
                return;
            }

            var artists = await File.ReadAllLinesAsync(mainArtistsFile, token);
            for (int i = 0; i < artists.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string artist = artists[i];
                if (string.IsNullOrWhiteSpace(artist)) continue;

                UpdateStatus($"Processing artist {i + 1}/{artists.Length}: {artist}");

                bool success = false;
                while (!success && !token.IsCancellationRequested)
                {
                    await File.WriteAllTextAsync(targetArtistsFile, artist, token);
                    LogToWrapper($"Wrote '{artist}' to target artists.txt.");
                    LogToAmd($"Ready to process for '{artist}'.");

                    success = await RunAndMonitorProcesses(baseDirectory, token);

                    if (!success)
                    {
                        UpdateStatus($"Restarting task for artist: {artist} in 5 seconds...");
                        await Task.Delay(5000, token);
                        ClearLogs();
                    }
                }

                if (success)
                {
                    UpdateStatus($"Task for '{artist}' completed. Waiting 60 seconds...");
                    await Task.Delay(60000, token);

                    await File.WriteAllTextAsync(targetArtistsFile, string.Empty, token);
                    LogToWrapper($"Cleared target artists.txt for next run.");
                    LogToAmd($"Ready for next artist.");
                }
            }
        }

        private async Task<bool> RunAndMonitorProcesses(string baseDirectory, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();

            using var ctr = token.Register(() => tcs.TrySetCanceled());

            try
            {
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

                _wrapperProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        LogToWrapper(e.Data);
                        if (e.Data.Contains("Wrapper down", StringComparison.OrdinalIgnoreCase) ||
                            e.Data.Contains("Down", StringComparison.OrdinalIgnoreCase))
                        {
                            tcs.TrySetResult(false);
                        }
                    }
                };
                _wrapperProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) LogToWrapper($"ERROR: {e.Data}");
                };

                _wrapperProcess.Start();
                _wrapperProcess.BeginOutputReadLine();
                _wrapperProcess.BeginErrorReadLine();
                LogToWrapper($"WrapperManager process started (PID: {_wrapperProcess.Id}).");

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

                _amdProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        LogToAmd(e.Data);
                        if (e.Data.Contains("All tasks completed.", StringComparison.OrdinalIgnoreCase))
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                };
                _amdProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) LogToAmd($"ERROR: {e.Data}");
                };

                _amdProcess.Start();
                _amdProcess.BeginOutputReadLine();
                _amdProcess.BeginErrorReadLine();
                LogToAmd($"AMD-V2 process started (PID: {_amdProcess.Id}).");

                return await tcs.Task;
            }
            finally
            {
                KillProcess(_wrapperProcess);
                _wrapperProcess = null;

                KillProcess(_amdProcess);
                _amdProcess = null;
            }
        }

        private void KillProcess(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    LogToWrapper($"Terminating process tree for PID: {process.Id}");
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                /* 进程未启动或已退出，忽略 */
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to kill process: {ex.Message}");
            }
        }

        #region UI Update Helpers and Log Management

        // 主事件处理器，当任何过滤条件改变时调用
        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            RefreshLogs(WrapperLogRtb, _wrapperLogEntries, GetWrapperFilterState());
            RefreshLogs(AmdLogRtb, _amdLogEntries, GetAmdFilterState());
        }

        // 日志解析和颜色确定
        private LogEntry ParseLogLine(string line)
        {
            var upperLine = line.ToUpperInvariant(); // Use InvariantCulture for case-insensitive comparisons
            if (upperLine.Contains("ERROR") || upperLine.Contains("FATAL") || upperLine.Contains("CRITICAL"))
                return new LogEntry(line, LogLevel.ERROR, Brushes.Red);
            if (upperLine.Contains("WARNING") || upperLine.Contains("WARN"))
                return new LogEntry(line, LogLevel.WARNING, Brushes.Orange);
            if (upperLine.Contains("DEBUG"))
                return new LogEntry(line, LogLevel.DEBUG, Brushes.Gray);
            if (upperLine.Contains("INFO"))
                return new LogEntry(line, LogLevel.INFO, Brushes.DodgerBlue);
            return new LogEntry(line, LogLevel.OTHER, SystemColors.WindowTextBrush);
        }


        private void LogToWrapper(string message)
        {
            // 在后台线程上执行无 UI 依赖的解析工作
            var logEntry = ParseLogLine(message);

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                // 将所有 UI 交互和共享集合的修改都放入 UI 线程
                Dispatcher.Invoke(() =>
                {
                    _wrapperLogEntries.Add(logEntry);

                    var filter = GetWrapperFilterState(); // 现在在 UI 线程上读取，安全
                    if (ShouldDisplay(logEntry, filter))
                    {
                        AppendToRichTextBox(WrapperLogRtb, logEntry); // 调用更新 UI 的方法
                    }
                });
            }
            catch (TaskCanceledException)
            {
                /* 忽略关闭时的异常 */
            }
        }

        private void LogToAmd(string message)
        {
            // 在后台线程上执行无 UI 依赖的解析工作
            var logEntry = ParseLogLine(message);

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                // 将所有 UI 交互和共享集合的修改都放入 UI 线程
                Dispatcher.Invoke(() =>
                {
                    _amdLogEntries.Add(logEntry);

                    var filter = GetAmdFilterState(); // 现在在 UI 线程上读取，安全
                    if (ShouldDisplay(logEntry, filter))
                    {
                        AppendToRichTextBox(AmdLogRtb, logEntry);
                    }
                });
            }
            catch (TaskCanceledException)
            {
                /* 忽略关闭时的异常 */
            }
        }

        // 将带颜色的日志附加到 RichTextBox

        private void AppendToRichTextBox(RichTextBox rtb, LogEntry logEntry)
        {
            // 此方法现在假定它总是在 UI 线程上被调用
            var paragraph = new Paragraph(new Run(logEntry.Message))
            {
                Foreground = logEntry.Color,
                Margin = new Thickness(0) // 紧凑显示
            };
            rtb.Document.Blocks.Add(paragraph);
            rtb.ScrollToEnd();
        }

        // 核心过滤逻辑
        private bool ShouldDisplay(LogEntry entry,
            (bool info, bool warn, bool err, bool dbg, bool oth, string keyword) filter)
        {
            // 关键字过滤
            if (!string.IsNullOrEmpty(filter.keyword) &&
                !entry.Message.Contains(filter.keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 级别过滤
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

        // 刷新整个日志视图
        private void RefreshLogs(RichTextBox rtb, List<LogEntry> source,
            (bool info, bool warn, bool err, bool dbg, bool oth, string keyword) filter)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    rtb.Document.Blocks.Clear();
                    foreach (var entry in source)
                    {
                        if (ShouldDisplay(entry, filter))
                        {
                            var paragraph = new Paragraph(new Run(entry.Message))
                            {
                                Foreground = entry.Color,
                                Margin = new Thickness(0)
                            };
                            rtb.Document.Blocks.Add(paragraph);
                        }
                    }

                    rtb.ScrollToEnd();
                });
            }
            catch (TaskCanceledException)
            {
                /* 忽略 */
            }
        }

        // 辅助方法获取当前过滤状态
        private (bool, bool, bool, bool, bool, string) GetWrapperFilterState() =>
            (WrapperShowInfo.IsChecked == true, WrapperShowWarning.IsChecked == true,
                WrapperShowError.IsChecked == true,
                WrapperShowDebug.IsChecked == true, WrapperShowOther.IsChecked == true, WrapperKeywordFilter.Text);

        private (bool, bool, bool, bool, bool, string) GetAmdFilterState() =>
            (AmdShowInfo.IsChecked == true, AmdShowWarning.IsChecked == true, AmdShowError.IsChecked == true,
                AmdShowDebug.IsChecked == true, AmdShowOther.IsChecked == true, AmdKeywordFilter.Text);


        // 清空日志
        private void ClearLogs()
        {
            // 清空数据源和 UI
            _wrapperLogEntries.Clear();
            _amdLogEntries.Clear();

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    WrapperLogRtb.Document.Blocks.Clear();
                    AmdLogRtb.Document.Blocks.Clear();
                });
            }
            catch (TaskCanceledException)
            {
                /* 忽略 */
            }
        }

        private void UpdateStatus(string message)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.Invoke(() => { StatusTextBlock.Text = message; });
            }
            catch (TaskCanceledException)
            {
                /* 忽略 */
            }
        }

        #endregion
    }
}