using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AppleMusicProcessManager
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cancellationTokenSource;
        private Process _wrapperProcess;
        private Process _amdProcess;

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
            string baseDirectory = Environment.CurrentDirectory;
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

        #region UI Update Helpers

        private void LogToWrapper(string message)
        {
            // 第一次检查，可以避免在大多数情况下进入 try-catch 块，略微提高性能
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    WrapperLogTextBox.AppendText(message + Environment.NewLine);
                    WrapperLogTextBox.ScrollToEnd();
                });
            }
            catch (TaskCanceledException)
            {
                // 当 Dispatcher 在 Invoke 调用期间关闭时，会发生此异常。
                // 这是正常的关闭行为，可以安全地忽略。
            }
        }

        private void LogToAmd(string message)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    AmdLogTextBox.AppendText(message + Environment.NewLine);
                    AmdLogTextBox.ScrollToEnd();
                });
            }
            catch (TaskCanceledException)
            {
                // 忽略在关闭期间发生的预期异常
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
                // 忽略在关闭期间发生的预期异常
            }
        }

        private void ClearLogs()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    WrapperLogTextBox.Clear();
                    AmdLogTextBox.Clear();
                });
            }
            catch (TaskCanceledException)
            {
                // 忽略在关闭期间发生的预期异常
            }
        }

        #endregion
    }
}