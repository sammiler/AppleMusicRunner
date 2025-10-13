using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public record ArtistDetails(string Name, string Url, string Region, string Genres);

    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _wrapperProcess;
        private Process? _amdProcess;
        private string? _baseDirectory;
        private string? _dbBaseDirectory;
        private bool _needCleanAll;
        private int _totalAlbumsScrapedThisSession;

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
            _dbBaseDirectory = Environment.GetEnvironmentVariable("AMData");

            if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(_dbBaseDirectory))
            {
                MessageBox.Show("错误：'AMD-V2' 或 'AMData' 环境变量未设置。", "环境配置错误", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                InfoPathTextBlock.Text = "错误：环境变量配置不完整！";
                return;
            }

            if (Directory.Exists(Path.Combine(_baseDirectory, "wsl1")) &&
                Directory.Exists(Path.Combine(_baseDirectory, "AppleMusicDecrypt")))
            {
                StartButton.IsEnabled = true;
                InfoPathTextBlock.Text = $"工作目录: {_baseDirectory} | DB: {_dbBaseDirectory}";
                UpdateStatus($"环境变量加载成功。");
            }
            else
            {
                MessageBox.Show($"错误：'AMD-V2' 指向的路径 '{_baseDirectory}' 无效。", "路径无效", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                InfoPathTextBlock.Text = $"路径无效: {_baseDirectory}";
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _needCleanAll = true;
            KillProcessesAndCleanupWSL();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StartButton.Content = "处理中...";

            _totalAlbumsScrapedThisSession = 0;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await RunWorkflow(_cancellationTokenSource.Token);
                UpdateStatus("所有任务已处理完毕或已达到上限。");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("处理过程被用户取消。");
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
                _needCleanAll = true;
                KillProcessesAndCleanupWSL();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                LogToWrapper("用户请求终止操作...");
                LogToAmd("用户请求终止操作...");
                _needCleanAll = true;
                _cancellationTokenSource.Cancel();
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(_dbBaseDirectory))
                {
                    UpdateStatus("错误: 工作目录或数据库目录未设置。");
                    return;
                }

                string targetArtistsFile = Path.Combine(_baseDirectory, "AppleMusicDecrypt", "artists.txt");
                string artistsNameDbPath = Path.Combine(_dbBaseDirectory, "artistNames.db");
                string metadataDbPath = Path.Combine(_dbBaseDirectory, "am_metadata.sqlite");
                string progressDbPath = Path.Combine(_dbBaseDirectory, "process_artists.db");

                var artistsToProcess = GetArtistsToProcess(artistsNameDbPath, progressDbPath);
                if (artistsToProcess == null || !artistsToProcess.Any())
                {
                    LogToAmd("[调度中心] 所有艺人均已处理。", Brushes.Green);
                    return; // 所有任务都完成了，正常退出
                }

                UpdateStatus($"发现 {artistsToProcess.Count} 个新艺人待处理。");
                LogToAmd($"[调度中心] 新一轮开始，发现 {artistsToProcess.Count} 个新艺人待处理。", Brushes.Cyan);

                bool sessionFailed = false;

                for (int i = 0; i < artistsToProcess.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        sessionFailed = true; // 用户取消也算会话失败
                        break;
                    }

                    string artistId = artistsToProcess[i];

                    ClearLogs();
                    UpdateStatus($"正在处理第 {i + 1}/{artistsToProcess.Count} 位艺术家: {artistId}");

                    await File.WriteAllTextAsync(targetArtistsFile, artistId, token);
                    LogToAmd($"[调度中心] 已将任务 '{artistId}' 写入 artists.txt。");

                    bool success = await RunAndMonitorProcesses(_baseDirectory, token);

                    if (!success)
                    {
                        // 如果单次运行失败
                        UpdateStatus($"艺术家 '{artistId}' 的任务失败，将终止当前会话并准备重启...");
                        LogToAmd($"[严重失败] 艺术家 '{artistId}' 的任务失败，会话将重启。", Brushes.Red);
                        await Task.Delay(3000, token); // 等待3秒让用户看到信息
                        sessionFailed = true;
                        break; // 跳出 for 循环
                    }

                    // 如果成功
                    MarkArtistAsProcessed(progressDbPath, artistId);
                    var details = GetArtistDetails(metadataDbPath, artistId);
                    int albumCount = GetAlbumCountForArtist(metadataDbPath, artistId);
                    _totalAlbumsScrapedThisSession += albumCount;

                    string reportMessage = albumCount > 0
                        ? $"[成功] 艺人: {details.Name} (ID: {artistId})。抓取到 {albumCount} 张专辑。"
                        : $"[注意] 艺人: {details.Name} (ID: {artistId})。未找到任何有效专辑。";
                    LogToWrapper(reportMessage, albumCount > 0 ? Brushes.Green : Brushes.IndianRed);

                    LogToWrapper($"[进度] 本次会话已累计抓取 {_totalAlbumsScrapedThisSession} 张专辑。", Brushes.Black);

                    if (_totalAlbumsScrapedThisSession > 500)
                    {
                        LogToWrapper("[会话停止] 累计抓取专辑数已超过500张上限！", Brushes.Red);
                        UpdateStatus("专辑总数超过500上限，会话已停止。");
                        sessionFailed = true; // 达到上限也算会话结束
                        break;
                    }

                    if (i < artistsToProcess.Count - 1)
                    {
                        UpdateStatus($"等待 10 秒后处理下一个艺术家...");
                        await Task.Delay(10000, token);
                    }
                }

                // 如果会话是因为失败、取消或达到上限而中断的，就再次循环，实现你的“递归”效果
                if (sessionFailed)
                {
                    // 在重启前，需要清理进程
                    _needCleanAll = true;
                    KillProcessesAndCleanupWSL();
                    UpdateStatus("会话已中断，等待重启...");
                    await Task.Delay(5000, token); // 重启前的等待
                }
                else
                {
                    break; // 跳出外层 while 循环
                }
            }
        }

        #region Database Helper Methods

        private List<string>? GetArtistsToProcess(string sourceDbPath, string progressDbPath)
        {
            if (!File.Exists(sourceDbPath))
            {
                MessageBox.Show($"任务源数据库 'artistNames.db' 不存在。", "数据库错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            try
            {
                var totalArtistIds = new List<string>();
                using (var con = new SQLiteConnection($"Data Source={sourceDbPath};Version=3;"))
                {
                    con.Open();
                    using var cmd =
                        new SQLiteCommand("SELECT DISTINCT artist_id FROM search_results WHERE artist_id IS NOT NULL",
                            con);
                    using SQLiteDataReader rdr = cmd.ExecuteReader();
                    while (rdr.Read()) totalArtistIds.Add(rdr.GetString(0));
                }

                var processedArtistIds = new HashSet<string>();
                using (var con = new SQLiteConnection($"Data Source={progressDbPath};Version=3;"))
                {
                    con.Open();
                    using var cmd =
                        new SQLiteCommand(
                            "CREATE TABLE IF NOT EXISTS processed_ids (artist_id TEXT PRIMARY KEY, processed_at TEXT)",
                            con);
                    cmd.ExecuteNonQuery();
                    using var cmd2 = new SQLiteCommand("SELECT artist_id FROM processed_ids", con);
                    using SQLiteDataReader rdr = cmd2.ExecuteReader();
                    while (rdr.Read()) processedArtistIds.Add(rdr.GetString(0));
                }

                return totalArtistIds.Except(processedArtistIds).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取数据库时出错: {ex.Message}", "数据库错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private void MarkArtistAsProcessed(string progressDbPath, string artistId)
        {
            try
            {
                using var con = new SQLiteConnection($"Data Source={progressDbPath};Version=3;");
                con.Open();
                using var cmd =
                    new SQLiteCommand(
                        "INSERT OR IGNORE INTO processed_ids (artist_id, processed_at) VALUES (@id, @time)", con);
                cmd.Parameters.AddWithValue("@id", artistId);
                cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("s"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                LogToAmd($"[错误] 无法标记艺人 {artistId}: {ex.Message}", Brushes.Red);
            }
        }

        private ArtistDetails GetArtistDetails(string metadataDbPath, string artistId)
        {
            if (!File.Exists(metadataDbPath)) return new ArtistDetails("Unknown", "N/A", "N/A", "N/A");
            try
            {
                using var con = new SQLiteConnection($"Data Source={metadataDbPath};Version=3;");
                con.Open();
                using var cmd = new SQLiteCommand("SELECT name, url, region, genres FROM artists WHERE id = @id", con);
                cmd.Parameters.AddWithValue("@id", artistId);
                using SQLiteDataReader rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    return new ArtistDetails(
                        rdr.IsDBNull(0) ? "N/A" : rdr.GetString(0),
                        rdr.IsDBNull(1) ? "N/A" : rdr.GetString(1),
                        rdr.IsDBNull(2) ? "N/A" : rdr.GetString(2),
                        rdr.IsDBNull(3) ? "N/A" : rdr.GetString(3)
                    );
                }
            }
            catch (Exception ex)
            {
                LogToAmd($"[错误] 无法获取艺人 {artistId} 详情: {ex.Message}", Brushes.Red);
            }

            return new ArtistDetails("Unknown", "N/A", "N/A", "N/A");
        }

        private int GetAlbumCountForArtist(string metadataDbPath, string artistId)
        {
            if (!File.Exists(metadataDbPath)) return 0;
            try
            {
                using var con = new SQLiteConnection($"Data Source={metadataDbPath};Version=3;");
                con.Open();
                using var cmd = new SQLiteCommand("SELECT COUNT(*) FROM album_artists WHERE artist_id = @id", con);
                cmd.Parameters.AddWithValue("@id", artistId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                LogToAmd($"[错误] 无法计算艺人 {artistId} 专辑数: {ex.Message}", Brushes.Red);
                return 0;
            }
        }

        #endregion

        private async Task<bool> RunAndMonitorProcesses(string baseDirectory, CancellationToken token)
        {
            _needCleanAll = false;

            if (_wrapperProcess == null || _wrapperProcess.HasExited)
            {
                LogToWrapper("Wrapper-Manager 未运行，正在启动...");
                var wrapperReadyTcs = new TaskCompletionSource<bool>();
                string wrapperBatScript = Path.Combine(baseDirectory, "1. Run WrapperManager.bat");
                if (!File.Exists(wrapperBatScript))
                {
                    MessageBox.Show("错误：找不到 '1. Run WrapperManager.bat'", "文件未找到", MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                var wrapperStartInfo = new ProcessStartInfo
                {
                    FileName = wrapperBatScript,
                    WorkingDirectory = baseDirectory,
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
                    if (data.Contains("Wrapper ready", StringComparison.OrdinalIgnoreCase))
                        wrapperReadyTcs.TrySetResult(true);
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

            bool success;
            string amdBatScript = Path.Combine(baseDirectory, "2. Run AMD-V2.bat");
            if (!File.Exists(amdBatScript))
            {
                MessageBox.Show("错误：找不到 '2. Run AMD-V2.bat' 启动脚本。", "文件未找到", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            var amdStartInfo = new ProcessStartInfo
            {
                FileName = amdBatScript,
                WorkingDirectory = baseDirectory,
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
                        outputTcs.TrySetResult(true);
                    else if (data.Contains("CRITICAL ERROR", StringComparison.OrdinalIgnoreCase))
                        outputTcs.TrySetResult(false);
                }

                _amdProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) HandleAmdOutput(e.Data);
                };
                _amdProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) HandleAmdOutput(e.Data);
                };

                _amdProcess.Start();
                _amdProcess.BeginOutputReadLine();
                _amdProcess.BeginErrorReadLine();
                LogToAmd($"AMD-V2 进程已启动 (PID: {_amdProcess.Id}).");

                Task processExitTask = _amdProcess.WaitForExitAsync(token);
                Task completedTask = await Task.WhenAny(outputTcs.Task, processExitTask);

                if (completedTask == outputTcs.Task)
                {
                    LogToAmd("检测到来自脚本输出的明确信号。");
                    success = await outputTcs.Task;
                }
                else
                {
                    LogToAmd($"检测到 AMD-V2 进程已退出，退出码: {_amdProcess.ExitCode}。");
                    success = _amdProcess.ExitCode == 0;
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
                if (!process.HasExited) process.Kill(true);
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
                string cleanupCommand = _needCleanAll
                    ? "pkill -f 'wrapper-manager'; pkill -f 'python3 main.py'"
                    : "pkill -f 'python3 main.py'";
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
            if (upperLine.Contains("WARNING") || upperLine.Contains("YELLOW"))
                return new LogEntry(line, LogLevel.WARNING, Brushes.DimGray);
            if (upperLine.Contains("DEBUG")) return new LogEntry(line, LogLevel.DEBUG, Brushes.Gray);
            if (upperLine.Contains("INFO") || upperLine.Contains("GREEN") || upperLine.Contains("CYAN"))
                return new LogEntry(line, LogLevel.INFO, Brushes.DodgerBlue);
            return new LogEntry(line, LogLevel.OTHER, SystemColors.WindowTextBrush);
        }

        private void LogToWrapper(string message, Brush? color = null)
        {
            var logEntry = ParseLogLine(message);
            if (color != null) logEntry = logEntry with { Color = color };
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _wrapperLogEntries.Add(logEntry);
                    if (ShouldDisplay(logEntry, GetWrapperFilterState())) AppendToRichTextBox(WrapperLogRtb, logEntry);
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void LogToAmd(string message, Brush? color = null)
        {
            var logEntry = ParseLogLine(message);
            if (color != null) logEntry = logEntry with { Color = color };
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _amdLogEntries.Add(logEntry);
                    if (ShouldDisplay(logEntry, GetAmdFilterState())) AppendToRichTextBox(AmdLogRtb, logEntry);
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
                !entry.Message.Contains(filter.keyword, StringComparison.OrdinalIgnoreCase)) return false;
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
                Dispatcher.BeginInvoke(() => { AmdLogRtb.Document.Blocks.Clear(); });
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