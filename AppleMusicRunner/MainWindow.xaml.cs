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
// NEW: Add this for the folder dialog
using Microsoft.Win32;

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
        // =============================================================
        // MODIFICATION: Fix nullable warnings by adding '?'
        // =============================================================
        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _wrapperProcess;
        private Process? _amdProcess;

        // =============================================================
        // NEW: Add a field to store the selected directory
        // =============================================================
        private string? _baseDirectory;

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

        // =============================================================
        // NEW: Click event handler for the "Select Directory" button
        // =============================================================
        private void SelectDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog
            {
                Title = "请选择 AMD-V2-WSL1 项目的根目录",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (openFolderDialog.ShowDialog() == true)
            {
                string selectedPath = openFolderDialog.FolderName;

                // Validate the selected directory
                if (Directory.Exists(Path.Combine(selectedPath, "wsl1")) &&
                    Directory.Exists(Path.Combine(selectedPath, "AppleMusicDecrypt")) &&
                    File.Exists(Path.Combine(selectedPath, "artists.txt")))
                {
                    _baseDirectory = selectedPath;
                    DirectoryPathTextBox.Text = _baseDirectory;
                    StartButton.IsEnabled = true; // Enable the start button
                    UpdateStatus($"工作目录已设置为: {_baseDirectory}");
                }
                else
                {
                    MessageBox.Show(
                        "选择的目录无效。\n\n请确保所选目录中包含 'wsl1' 文件夹、'AppleMusicDecrypt' 文件夹和 'artists.txt' 文件。",
                        "目录错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    StartButton.IsEnabled = false;
                }
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Disable buttons during processing
            StartButton.IsEnabled = false;
            SelectDirectoryButton.IsEnabled = false;
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
                UpdateStatus($"发生错误，详情请见弹窗。");
                MessageBox.Show(ex.ToString(), "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Restore button states
                StartButton.IsEnabled = true;
                SelectDirectoryButton.IsEnabled = true;
                StartButton.Content = "开始处理";
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            // =============================================================
            // MODIFICATION: Use the _baseDirectory field
            // =============================================================
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

                UpdateStatus($"正在处理第 {i + 1}/{artists.Length} 位艺术家: {artist}");

                bool success = false;
                while (!success && !token.IsCancellationRequested)
                {
                    await File.WriteAllTextAsync(targetArtistsFile, artist, token);
                    LogToWrapper($"已将 '{artist}' 写入目标 artists.txt。");
                    LogToAmd($"准备处理 '{artist}'。");

                    success = await RunAndMonitorProcesses(_baseDirectory, token);

                    if (!success)
                    {
                        UpdateStatus($"艺术家 '{artist}' 的任务失败，将在 5 秒后重试...");
                        await Task.Delay(5000, token);
                        ClearLogs();
                    }
                }

                if (success)
                {
                    UpdateStatus($"艺术家 '{artist}' 的任务已完成。等待 60 秒...");
                    await Task.Delay(60000, token);

                    await File.WriteAllTextAsync(targetArtistsFile, string.Empty, token);
                    LogToWrapper($"已清空目标 artists.txt 为下一次运行做准备。");
                    LogToAmd($"准备处理下一位艺术家。");
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
                LogToWrapper($"WrapperManager 进程已启动 (PID: {_wrapperProcess.Id}).");

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
                LogToAmd($"AMD-V2 进程已启动 (PID: {_amdProcess.Id}).");

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

        private void KillProcess(Process? process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    LogToWrapper($"正在终止进程树 (PID: {process.Id})");
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                /* 进程未启动或已退出，忽略 */
            }
            catch (Exception ex)
            {
                UpdateStatus($"终止进程失败: {ex.Message}");
            }
        }

        #region UI Update Helpers and Log Management

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            RefreshLogs(WrapperLogRtb, _wrapperLogEntries, GetWrapperFilterState());
            RefreshLogs(AmdLogRtb, _amdLogEntries, GetAmdFilterState());
        }

        // =============================================================
        // NEW: Overload for TextBox's TextChanged event
        // =============================================================
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
            if (upperLine.Contains("DEBUG"))
                return new LogEntry(line, LogLevel.DEBUG, Brushes.Gray);
            if (upperLine.Contains("INFO"))
                return new LogEntry(line, LogLevel.INFO, Brushes.DodgerBlue);

            return new LogEntry(line, LogLevel.OTHER, SystemColors.WindowTextBrush);
        }

        private void LogToWrapper(string message)
        {
            var logEntry = ParseLogLine(message);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _wrapperLogEntries.Add(logEntry);
                    var filter = GetWrapperFilterState();
                    if (ShouldDisplay(logEntry, filter))
                    {
                        AppendToRichTextBox(WrapperLogRtb, logEntry);
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
            var logEntry = ParseLogLine(message);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _amdLogEntries.Add(logEntry);
                    var filter = GetAmdFilterState();
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

        private void AppendToRichTextBox(RichTextBox rtb, LogEntry logEntry)
        {
            var paragraph = new Paragraph(new Run(logEntry.Message))
            {
                Foreground = logEntry.Color,
                Margin = new Thickness(0)
            };
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

        private (bool, bool, bool, bool, bool, string) GetWrapperFilterState() =>
            (WrapperShowInfo.IsChecked == true, WrapperShowWarning.IsChecked == true,
                WrapperShowError.IsChecked == true,
                WrapperShowDebug.IsChecked == true, WrapperShowOther.IsChecked == true, WrapperKeywordFilter.Text);

        private (bool, bool, bool, bool, bool, string) GetAmdFilterState() =>
            (AmdShowInfo.IsChecked == true, AmdShowWarning.IsChecked == true, AmdShowError.IsChecked == true,
                AmdShowDebug.IsChecked == true, AmdShowOther.IsChecked == true, AmdKeywordFilter.Text);

        private void ClearLogs()
        {
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