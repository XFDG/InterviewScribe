using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;
using InterviewScribe.Infrastructure.Pipeline;
using Microsoft.Win32;

namespace InterviewScribe.App;

public partial class MainWindow : Window
{
    private const int InstallerAlreadyRunningExitCode = 1618;
    private const int MaximumVisibleLogCharacters = 1_000_000;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".m4v", ".webm", ".wmv", ".mpeg", ".mpg",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma"
    };

    private static readonly SolidColorBrush IdleBackground = CreateBrush(0xF0, 0xF2, 0xF6);
    private static readonly SolidColorBrush IdleForeground = CreateBrush(0x66, 0x70, 0x85);
    private static readonly SolidColorBrush PrimaryBackground = CreateBrush(0xEE, 0xF0, 0xFF);
    private static readonly SolidColorBrush PrimaryForeground = CreateBrush(0x45, 0x48, 0xD2);
    private static readonly SolidColorBrush SuccessBackground = CreateBrush(0xEA, 0xF8, 0xF2);
    private static readonly SolidColorBrush SuccessForeground = CreateBrush(0x15, 0x84, 0x5A);
    private static readonly SolidColorBrush DangerBackground = CreateBrush(0xFF, 0xF0, 0xF2);
    private static readonly SolidColorBrush DangerForeground = CreateBrush(0xC3, 0x3D, 0x51);
    private static readonly SolidColorBrush StageIdleBackground = CreateBrush(0xF5, 0xF6, 0xF9);
    private static readonly SolidColorBrush StageIdleBorder = CreateBrush(0xE5, 0xE9, 0xF2);
    private static readonly SolidColorBrush LocalStatusDot = CreateBrush(0x54, 0xD6, 0xA1);

    private readonly DispatcherTimer _elapsedTimer;
    private readonly ObservableCollection<QueueItem> _queueItems = [];
    private TranscriptionPipeline? _pipeline;
    private CancellationTokenSource? _runCancellation;
    private Stopwatch? _taskStopwatch;
    private DateTimeOffset? _estimatedCompletionAt;
    private int _lastDisplayedPercentage;
    private int? _lastLoggedProgressPercentage;
    private JobState? _lastLoggedProgressState;
    private string? _lastLoggedProgressMessage;
    private string? _sourcePath;
    private QueueItem? _activeQueueItem;
    private string? _lastPrimaryOutputPath;
    private string? _lastOutputDirectory;
    private JobState _currentState = JobState.Idle;
    private bool _isRunning;
    private bool _isInstallingQwen;
    private bool _closeAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;
        QueueListBox.ItemsSource = _queueItems;
        ResetStageVisuals();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        UpdateTranscriptionModeUi();
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isInstallingQwen)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "添加媒体文件到转写队列",
            Filter = "视频与音频|*.mp4;*.mkv;*.mov;*.avi;*.m4v;*.webm;*.wmv;*.mpeg;*.mpg;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_sourcePath) ?? Environment.CurrentDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            QueueSourceFiles(dialog.FileNames);
        }
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        BrowseFile_Click(sender, e);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isInstallingQwen)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择转写结果保存位置",
            Multiselect = false
        };

        var currentPath = OutputFolderTextBox.Text.Trim();
        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }
        else if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_sourcePath) ?? Environment.CurrentDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderTextBox.Text = dialog.FolderName;
            AppendLog($"输出位置：{dialog.FolderName}");
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isRunning && !_isInstallingQwen && TryGetDroppedFiles(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_isRunning && !_isInstallingQwen && TryGetDroppedFiles(e.Data, out var paths))
        {
            QueueSourceFiles(paths);
        }

        e.Handled = true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isInstallingQwen)
        {
            return;
        }

        var queuedItems = _queueItems
            .Where(item => item.State == QueueItemState.Queued)
            .ToList();
        if (queuedItems.Count == 0)
        {
            ShowFriendlyError("请先添加至少一个媒体文件到队列。", "队列为空");
            return;
        }

        var outputDirectory = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            ShowFriendlyError("请先选择转写结果的保存位置。", "缺少输出位置");
            return;
        }

        var languageCodes = GetSelectedLanguageCodes();
        if (languageCodes.Count == 0)
        {
            LanguageValidationText.Visibility = Visibility.Visible;
            ChineseLanguageCheckBox.Focus();
            ShowFriendlyError("请至少选择一种识别语言。", "缺少识别语言");
            return;
        }

        var outputFormats = GetSelectedOutputFormats();
        if (outputFormats == TranscriptOutputFormat.None)
        {
            OutputFormatValidationText.Visibility = Visibility.Visible;
            TxtOutputCheckBox.Focus();
            ShowFriendlyError("请至少选择一种输出格式。", "缺少输出格式");
            return;
        }

        var formattingOptions = new TranscriptFormattingOptions
        {
            IncludeTimestamps = IncludeTimelineCheckBox.IsChecked == true,
            IncludeSpeakers = IncludeSpeakersCheckBox.IsChecked == true
        };
        var transcriptionMode = GetSelectedTranscriptionMode();

        PrepareForRun();
        AppendLog($"识别方案：{GetTranscriptionModeDisplayName(transcriptionMode)}");
        AppendLog($"识别语言：{FormatSelectedLanguages(languageCodes)}");
        AppendLog($"输出格式：{FormatOutputFormats(outputFormats)}");
        AppendLog($"可读文档时间轴：{FormatSwitch(formattingOptions.IncludeTimestamps)}；说话人区分：{FormatSwitch(formattingOptions.IncludeSpeakers)}");
        AppendLog($"队列开始：{queuedItems.Count} 个文件将按顺序处理。");
        _runCancellation = new CancellationTokenSource();
        _pipeline ??= new TranscriptionPipeline();
        var completedCount = 0;
        var failedCount = 0;
        var wasCancelled = false;

        try
        {
            foreach (var queueItem in queuedItems)
            {
                if (_runCancellation.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                if (!File.Exists(queueItem.SourcePath))
                {
                    queueItem.SetState(QueueItemState.Failed, "文件不存在");
                    failedCount++;
                    completedCount++;
                    AppendLog($"跳过队列项：找不到文件 {queueItem.SourcePath}");
                    RefreshQueueUi();
                    continue;
                }

                var completedBeforeCurrent = completedCount;
                PrepareForQueueItem(queueItem, completedBeforeCurrent, queuedItems.Count);
                var progress = new Progress<OperationProgress>(operation =>
                    UpdateQueueProgress(operation, queueItem, completedBeforeCurrent, queuedItems.Count));
                var diagnostics = new Progress<string>(message =>
                    AppendQueueDiagnosticLog(message, queueItem, completedBeforeCurrent, queuedItems.Count));

                try
                {
                    var request = new PipelineRequest(queueItem.SourcePath, outputDirectory)
                    {
                        Mode = transcriptionMode,
                        EnableSpeakerDiarization = IncludeSpeakersCheckBox.IsChecked == true,
                        LanguageCodes = languageCodes,
                        FormattingOptions = formattingOptions,
                        OutputFormats = outputFormats,
                    };
                    var result = await _pipeline.RunAsync(
                        request,
                        progress,
                        _runCancellation.Token,
                        diagnostics);

                    queueItem.SetState(QueueItemState.Completed);
                    completedCount++;
                    ShowResult(result);
                    AppendLog($"队列项完成：{queueItem.FileName}");
                }
                catch (OperationCanceledException)
                {
                    queueItem.SetState(QueueItemState.Cancelled);
                    wasCancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    if (_runCancellation.IsCancellationRequested)
                    {
                        queueItem.SetState(QueueItemState.Cancelled);
                        wasCancelled = true;
                        break;
                    }

                    queueItem.SetState(QueueItemState.Failed, "转写失败");
                    failedCount++;
                    completedCount++;
                    AppendLog($"队列项失败：{queueItem.FileName} · {GetFriendlyError(ex)}");
                    AppendLog(ex.ToString());
                    LogExpander.IsExpanded = true;
                }

                RefreshQueueUi();
            }

            if (wasCancelled)
            {
                MarkQueuedItemsCancelled();
                UpdateQueueCancelledState();
            }
            else
            {
                UpdateQueueCompletionState(queuedItems.Count, failedCount);
            }
        }
        catch (OperationCanceledException)
        {
            MarkQueuedItemsCancelled();
            UpdateQueueCancelledState();
        }
        catch (Exception ex)
        {
            UpdateFailedState(ex);
            if (!_closeAfterCancellation)
            {
                ShowFriendlyError(GetFriendlyError(ex), "转写队列没有完成");
            }
        }
        finally
        {
            _elapsedTimer.Stop();
            _taskStopwatch?.Stop();
            _runCancellation?.Dispose();
            _runCancellation = null;
            _isRunning = false;
            _activeQueueItem = null;
            ChangeFileButton.IsEnabled = true;
            DropZoneBorder.IsEnabled = true;
            QueueListBox.IsEnabled = true;
            ClearQueueButton.IsEnabled = true;
            SettingsPanel.IsEnabled = true;
            RecognitionModePanel.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            RefreshQueueUi();
            UpdateStartButtonState();

            if (_closeAfterCancellation)
            {
                await Dispatcher.InvokeAsync(Close);
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RequestCancellation();
    }

    private void LanguageCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var hasSelectedLanguage = HasSelectedLanguage();
        LanguageValidationText.Visibility = hasSelectedLanguage ? Visibility.Collapsed : Visibility.Visible;
        UpdateStartButtonState();
    }

    private void OutputFormatCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var hasSelectedFormat = HasSelectedOutputFormat();
        OutputFormatValidationText.Visibility = hasSelectedFormat ? Visibility.Collapsed : Visibility.Visible;
        UpdateStartButtonState();
    }

    private void TranscriptionMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateTranscriptionModeUi();
        UpdateStartButtonState();
    }

    private void UpdateTranscriptionModeUi()
    {
        var mode = GetSelectedTranscriptionMode();

        QwenLocalSetupPanel.Visibility = mode == TranscriptionMode.QwenLocalHighAccuracy
            ? Visibility.Visible
            : Visibility.Collapsed;
        WhisperSetupPanel.Visibility = mode == TranscriptionMode.WhisperTurboFast
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProcessingModeStatusDot.Fill = LocalStatusDot;
        PrivacyIconBorder.Background = SuccessBackground;
        PrivacyIconText.Foreground = SuccessForeground;

        switch (mode)
        {
            case TranscriptionMode.QwenLocalHighAccuracy:
                ProcessingModeStatusText.Text = "本地高精度";
                RecognitionSummaryTitleText.Text = "Qwen3-ASR 1.7B + ForcedAligner";
                RecognitionSummaryDetailText.Text = "Qwen 负责最终文字与逐词时间轴；只有勾选说话人区分时才额外运行本地 MOSS。";
                PrivacyTitleText.Text = "隐私保护";
                PrivacySubtitleText.Text = "文件不会上传云端";
                PrivacyDetailText.Text = "视频、临时音频和转写文字都只在这台电脑上处理。首次安装会从官方 ModelScope 下载并校验 Qwen 与对齐模型，安装后可断网运行。";
                FooterModeText.Text = "MediaScribe · Qwen 本地高精度";
                StageTranscribeText.Text = "③ Qwen 识别与对齐";
                break;

            case TranscriptionMode.MossLocalFast:
                ProcessingModeStatusText.Text = "完全本地处理";
                RecognitionSummaryTitleText.Text = "MOSS-Transcribe-Diarize Q8_0";
                RecognitionSummaryDetailText.Text = "文字、时间轴和说话人均由本地 MOSS 一次生成，适合兼容既有工作流。";
                PrivacyTitleText.Text = "隐私保护";
                PrivacySubtitleText.Text = "文件不会上传云端";
                PrivacyDetailText.Text = "视频、临时音频和转写文字都只在这台电脑上处理。首次使用仅下载公开 MOSS 模型。";
                FooterModeText.Text = "MediaScribe · MOSS 本地兼容";
                StageTranscribeText.Text = "③ MOSS 识别与分人";
                break;

            default:
                ProcessingModeStatusText.Text = "完全本地处理";
                RecognitionSummaryTitleText.Text = "Whisper large-v3-turbo + Faster-Whisper";
                RecognitionSummaryDetailText.Text = "Whisper 生成文字与时间轴；勾选说话人区分时，再按需调用本地 MOSS。";
                PrivacyTitleText.Text = "隐私保护";
                PrivacySubtitleText.Text = "文件不会上传云端";
                PrivacyDetailText.Text = "视频、临时音频和转写文字都只在这台电脑上处理。Whisper 模型与 CUDA 运行组件安装后均可断网使用。";
                FooterModeText.Text = "MediaScribe · Whisper Turbo 快速模式";
                StageTranscribeText.Text = "③ Whisper 识别";
                break;
        }

        if (!_isRunning && _currentState == JobState.Idle)
        {
            StatusDetailText.Text = !_queueItems.Any(item => item.State == QueueItemState.Queued)
                ? GetSelectionPrompt(mode)
                : GetReadyDetail(mode);
        }
    }

    private async void InstallQwenRuntime_Click(object sender, RoutedEventArgs e)
    {
        await LaunchRuntimeInstallerAsync(
            FindQwenInstallerPath(),
            "Qwen 高精度组件",
            "Qwen3-ASR、ForcedAligner 与标准 PyTorch CUDA 运行环境已校验完成。现在可断网使用高精度模式。");
    }

    private async void InstallWhisperRuntime_Click(object sender, RoutedEventArgs e)
    {
        await LaunchRuntimeInstallerAsync(
            FindWhisperInstallerPath(),
            "Whisper Turbo 快速模式组件",
            "Whisper large-v3-turbo、Faster-Whisper 与 Windows CUDA 运行组件已校验完成。现在可断网使用快速模式。");
    }

    private async Task LaunchRuntimeInstallerAsync(
        string? scriptPath,
        string componentName,
        string successfulMessage)
    {
        if (_isRunning || _isInstallingQwen)
        {
            return;
        }

        if (scriptPath is null)
        {
            ShowFriendlyError(
                $"找不到 {componentName} 安装脚本。请重新安装 MediaScribe。",
                "缺少安装组件");
            return;
        }

        _isInstallingQwen = true;
        InstallQwenRuntimeButton.IsEnabled = false;
        InstallWhisperRuntimeButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        ChangeFileButton.IsEnabled = false;
        DropZoneBorder.IsEnabled = false;
        QueueListBox.IsEnabled = false;
        ClearQueueButton.IsEnabled = false;
        SettingsPanel.IsEnabled = false;
        RecognitionModePanel.IsEnabled = false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows PowerShell 安装进程未能启动。");

            AppendLog($"{componentName}安装窗口已打开，正在等待运行环境与模型安装完成…");

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                AppendLog($"{componentName}安装完成。");
                MessageBox.Show(
                    this,
                    successfulMessage,
                    "安装完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (process.ExitCode == InstallerAlreadyRunningExitCode)
            {
                AppendLog($"未启动安装：另一个 MediaScribe {componentName}安装程序仍在运行。");
                ShowFriendlyError(
                    "另一项运行组件安装仍在进行。请等待现有的 PowerShell 安装窗口完成后再重试。",
                    "安装正在进行");
            }
            else
            {
                AppendLog($"{componentName}安装失败，PowerShell 退出码：{process.ExitCode}。");
                ShowFriendlyError(
                    $"{componentName}没有安装成功。请查看刚才的 PowerShell 窗口输出，修复问题后重试。",
                    "安装失败");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppendLog($"无法启动 {componentName}安装程序：{ex.Message}");
            ShowFriendlyError("无法启动 Windows PowerShell 安装窗口。请确认 Windows PowerShell 可用后重试。", "无法启动安装");
        }
        finally
        {
            _isInstallingQwen = false;
            InstallQwenRuntimeButton.IsEnabled = !_isRunning;
            InstallWhisperRuntimeButton.IsEnabled = !_isRunning;
            ChangeFileButton.IsEnabled = !_isRunning;
            DropZoneBorder.IsEnabled = !_isRunning;
            QueueListBox.IsEnabled = !_isRunning;
            ClearQueueButton.IsEnabled = !_isRunning;
            SettingsPanel.IsEnabled = !_isRunning;
            RecognitionModePanel.IsEnabled = !_isRunning;
            UpdateStartButtonState();
        }
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        var resultPath = _lastPrimaryOutputPath;
        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
        {
            ShowFriendlyError("找不到结果文件。请到输出文件夹中确认文件是否被移动。", "文件不存在");
            return;
        }

        TryOpenWithShell(resultPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var outputDirectory = _lastOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            ShowFriendlyError("找不到结果文件夹。", "文件夹不存在");
            return;
        }

        TryOpenWithShell(outputDirectory);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogTextBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(LogTextBox.Text);
            FooterStatusText.Text = "诊断信息已复制";
        }
        catch (ExternalException ex)
        {
            AppendLog($"无法复制到剪贴板：{ex.Message}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isInstallingQwen)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "高精度组件仍在安装，主窗口暂时不能关闭。请等待 PowerShell 安装窗口完成；如需取消，请先关闭该 PowerShell 窗口，待本界面恢复后再退出。",
                "安装仍在进行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_isRunning)
        {
            return;
        }

        if (_closeAfterCancellation)
        {
            e.Cancel = true;
            return;
        }

        var answer = MessageBox.Show(
            this,
            "转写队列仍在进行。现在退出会取消当前任务并停止队列，但不会删除原始媒体文件。要继续退出吗？",
            "取消队列并退出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        e.Cancel = true;
        if (answer == MessageBoxResult.Yes)
        {
            _closeAfterCancellation = true;
            RequestCancellation();
        }
    }

    private void ElapsedTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRunning && _taskStopwatch is not null)
        {
            UpdateElapsedAndEtaDisplay();
        }
    }

    private void QueueSourceFiles(IEnumerable<string> paths)
    {
        var addedFiles = new List<FileInfo>();
        var unsupportedCount = 0;
        var candidatePaths = new List<string>();

        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                candidatePaths.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                unsupportedCount++;
                AppendLog($"文件路径无效，未加入队列：{path}");
            }
        }

        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                unsupportedCount++;
                continue;
            }

            if (_queueItems.Any(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLog($"已在队列中，跳过重复文件：{path}");
                continue;
            }

            try
            {
                var file = new FileInfo(path);
                _queueItems.Add(new QueueItem(file));
                addedFiles.Add(file);
                AppendLog($"已加入队列：{file.FullName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                unsupportedCount++;
                AppendLog($"无法读取文件，未加入队列：{path} · {ex.Message}");
            }
        }

        if (addedFiles.Count == 0)
        {
            ShowFriendlyError("没有可加入队列的媒体文件。支持 MP4、MKV、MOV、AVI、WEBM、MP3、WAV、M4A 等格式。", "没有添加文件");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolderTextBox.Text))
        {
            OutputFolderTextBox.Text = Path.Combine(addedFiles[0].DirectoryName ?? Environment.CurrentDirectory, "转写结果");
        }

        if (unsupportedCount > 0)
        {
            AppendLog($"有 {unsupportedCount} 个文件不受支持或无法读取，未加入队列。");
        }

        ResultCard.Visibility = Visibility.Collapsed;
        _lastPrimaryOutputPath = null;
        _lastOutputDirectory = null;
        RefreshQueueUi();
        SetIdleState("队列准备就绪", GetReadyDetail(GetSelectedTranscriptionMode()), "可以开始");
        UpdateStartButtonState();
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isInstallingQwen || sender is not Button { Tag: QueueItem item })
        {
            return;
        }

        _queueItems.Remove(item);
        AppendLog($"已从队列移除：{item.FileName}");
        RefreshQueueUi();
        UpdateStartButtonState();
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isInstallingQwen || _queueItems.Count == 0)
        {
            return;
        }

        _queueItems.Clear();
        _activeQueueItem = null;
        _sourcePath = null;
        ResultCard.Visibility = Visibility.Collapsed;
        _lastPrimaryOutputPath = null;
        _lastOutputDirectory = null;
        AppendLog("已清空转写队列。");
        RefreshQueueUi();
        SetIdleState("准备就绪", GetSelectionPrompt(GetSelectedTranscriptionMode()), "等待开始");
        UpdateStartButtonState();
    }

    private void RefreshQueueUi()
    {
        var queueCount = _queueItems.Count;
        var queuedCount = _queueItems.Count(item => item.State == QueueItemState.Queued);
        var runningCount = _queueItems.Count(item => item.State == QueueItemState.Running);
        var completedCount = _queueItems.Count(item => item.State == QueueItemState.Completed);
        var failedCount = _queueItems.Count(item => item.State == QueueItemState.Failed);
        var cancelledCount = _queueItems.Count(item => item.State == QueueItemState.Cancelled);

        if (queueCount == 0)
        {
            _sourcePath = null;
            EmptyFilePanel.Visibility = Visibility.Visible;
            SelectedFilePanel.Visibility = Visibility.Collapsed;
            QueueHeaderPanel.Visibility = Visibility.Collapsed;
            QueueListBox.Visibility = Visibility.Collapsed;
            return;
        }

        var currentItem = _activeQueueItem
            ?? _queueItems.FirstOrDefault(item => item.State == QueueItemState.Running)
            ?? _queueItems.FirstOrDefault(item => item.State == QueueItemState.Queued)
            ?? _queueItems[^1];
        _sourcePath = currentItem.SourcePath;
        EmptyFilePanel.Visibility = Visibility.Collapsed;
        SelectedFilePanel.Visibility = Visibility.Visible;
        QueueHeaderPanel.Visibility = Visibility.Visible;
        QueueListBox.Visibility = Visibility.Visible;
        ClearQueueButton.IsEnabled = !_isRunning && !_isInstallingQwen;

        SelectedFileNameText.Text = queueCount == 1
            ? currentItem.FileName
            : $"{queueCount} 个媒体文件已加入队列";
        SelectedFileNameText.ToolTip = currentItem.SourcePath;
        SelectedFileDetailsText.Text = queueCount == 1
            ? currentItem.Details
            : $"待处理 {queuedCount} · 处理中 {runningCount} · 已完成 {completedCount} · 失败 {failedCount} · 已取消 {cancelledCount}";
        SelectedFileStatusText.Text = runningCount > 0
            ? "正在处理"
            : queuedCount > 0
                ? "队列已就绪"
                : failedCount > 0
                    ? "处理结束"
                    : "已完成";
        QueueSummaryText.Text = $"处理顺序固定；当前队列 {queueCount} 个文件，待处理 {queuedCount} 个。";
    }

    private void PrepareForRun()
    {
        _isRunning = true;
        _closeAfterCancellation = false;
        _activeQueueItem = null;
        _lastPrimaryOutputPath = null;
        _lastOutputDirectory = null;
        ResultCard.Visibility = Visibility.Collapsed;
        PreviewTextBox.Clear();
        LogTextBox.Clear();
        ChangeFileButton.IsEnabled = false;
        DropZoneBorder.IsEnabled = false;
        QueueListBox.IsEnabled = false;
        ClearQueueButton.IsEnabled = false;
        SettingsPanel.IsEnabled = false;
        RecognitionModePanel.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        ElapsedText.Text = "已用时 00:00 · 正在估算剩余时间";
        LogProgressSummaryText.Text = "0% · 正在估算剩余时间";
        _lastDisplayedPercentage = 0;
        _lastLoggedProgressPercentage = null;
        _lastLoggedProgressState = null;
        _lastLoggedProgressMessage = null;
        _estimatedCompletionAt = null;
        _taskStopwatch = Stopwatch.StartNew();
        _elapsedTimer.Start();
        RefreshQueueUi();
    }

    private void PrepareForQueueItem(QueueItem queueItem, int completedBeforeCurrent, int totalCount)
    {
        _activeQueueItem = queueItem;
        _sourcePath = queueItem.SourcePath;
        _lastDisplayedPercentage = (int)Math.Round((double)completedBeforeCurrent / totalCount * 100, MidpointRounding.AwayFromZero);
        _lastLoggedProgressPercentage = null;
        _lastLoggedProgressState = null;
        _lastLoggedProgressMessage = null;
        _estimatedCompletionAt = null;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = _lastDisplayedPercentage;
        ProgressPercentText.Text = $"{_lastDisplayedPercentage}%";
        queueItem.SetState(QueueItemState.Running, "0%");
        RefreshQueueUi();
        AppendLog($"开始队列项 {completedBeforeCurrent + 1}/{totalCount}：{queueItem.SourcePath}");
        UpdateQueueProgress(
            new OperationProgress(JobState.WaitingForModel, GetPreparationMessage(GetSelectedTranscriptionMode()), 0),
            queueItem,
            completedBeforeCurrent,
            totalCount);
    }

    private void UpdateQueueProgress(
        OperationProgress operation,
        QueueItem queueItem,
        int completedBeforeCurrent,
        int totalCount)
    {
        var itemFraction = operation.Fraction is double fraction && double.IsFinite(fraction)
            ? Math.Clamp(fraction, 0, 1)
            : 0;
        var itemPercentage = (int)Math.Round(itemFraction * 100, MidpointRounding.AwayFromZero);
        queueItem.SetState(QueueItemState.Running, $"{itemPercentage}%");

        var queueFraction = Math.Clamp((completedBeforeCurrent + itemFraction) / totalCount, 0, 1);
        UpdateProgress(operation with { Fraction = queueFraction });
        StatusTitleText.Text = $"队列 {completedBeforeCurrent + 1}/{totalCount}：{GetStateTitle(operation.State)}";
        StatusDetailText.Text = $"当前文件：{queueItem.FileName} · {operation.Message}";
        FooterStatusText.Text = $"队列 {completedBeforeCurrent + 1}/{totalCount} · {operation.Message}";
        RefreshQueueUi();
    }

    private void AppendQueueDiagnosticLog(
        string message,
        QueueItem queueItem,
        int completedBeforeCurrent,
        int totalCount)
    {
        AppendDiagnosticLog($"[队列 {completedBeforeCurrent + 1}/{totalCount} · {queueItem.FileName}] {message}");
    }

    private void MarkQueuedItemsCancelled()
    {
        foreach (var queueItem in _queueItems.Where(item => item.State == QueueItemState.Queued))
        {
            queueItem.SetState(QueueItemState.Cancelled);
        }
    }

    private void UpdateQueueCompletionState(int totalCount, int failedCount)
    {
        _currentState = failedCount == 0 ? JobState.Completed : JobState.Failed;
        _lastDisplayedPercentage = 100;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 100;
        ProgressPercentText.Text = "100%";
        _estimatedCompletionAt = null;
        var succeededCount = totalCount - failedCount;
        StatusTitleText.Text = failedCount == 0 ? "队列转写完成" : "队列处理完成（部分失败）";
        StatusDetailText.Text = failedCount == 0
            ? $"已按顺序完成 {succeededCount} 个媒体文件。"
            : $"已完成 {succeededCount} 个，失败 {failedCount} 个；失败原因见运行记录。";
        FooterStatusText.Text = failedCount == 0
            ? $"队列完成 · {succeededCount}/{totalCount}"
            : $"队列结束 · {succeededCount} 完成 / {failedCount} 失败";
        ElapsedText.Text = $"总用时 {FormatElapsed(_taskStopwatch?.Elapsed ?? TimeSpan.Zero)} · 队列已结束";
        LogProgressSummaryText.Text = failedCount == 0 ? "100% · 队列已完成" : "100% · 队列含失败项";

        if (failedCount == 0)
        {
            StatusBadgeText.Text = "队列完成";
            StatusBadgeBorder.Background = SuccessBackground;
            StatusBadgeText.Foreground = SuccessForeground;
            ApplyStageVisuals(PipelinePhase.Completed, JobState.Completed);
        }
        else
        {
            StatusBadgeText.Text = "部分完成";
            StatusBadgeBorder.Background = DangerBackground;
            StatusBadgeText.Foreground = DangerForeground;
        }

        AppendLog(failedCount == 0
            ? $"队列已完成：{succeededCount}/{totalCount} 个文件。"
            : $"队列已结束：{succeededCount} 个完成，{failedCount} 个失败。");
    }

    private void UpdateQueueCancelledState()
    {
        var requeuedCount = RequeueCancelledItemsForRetry();
        _currentState = JobState.Idle;
        StatusTitleText.Text = "队列已停止";
        StatusDetailText.Text = requeuedCount == 0
            ? "当前任务已取消。原始媒体文件没有被修改。"
            : $"当前任务已取消；{requeuedCount} 个未完成文件已保留在队列中，可更换模型后直接重新开始。";
        FooterStatusText.Text = requeuedCount == 0 ? "队列已取消" : "队列已取消 · 可重新开始";
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = _lastDisplayedPercentage;
        ProgressPercentText.Text = $"{_lastDisplayedPercentage}%";
        _estimatedCompletionAt = null;
        ElapsedText.Text = $"总用时 {FormatElapsed(_taskStopwatch?.Elapsed ?? TimeSpan.Zero)} · 已取消";
        LogProgressSummaryText.Text = requeuedCount == 0
            ? $"{_lastDisplayedPercentage}% · 队列已取消"
            : $"{_lastDisplayedPercentage}% · 已可重新开始";
        StatusBadgeText.Text = requeuedCount == 0 ? "已取消" : "可重新开始";
        StatusBadgeBorder.Background = requeuedCount == 0 ? IdleBackground : PrimaryBackground;
        StatusBadgeText.Foreground = requeuedCount == 0 ? IdleForeground : PrimaryForeground;
        AppendLog(requeuedCount == 0
            ? "队列已由用户取消。"
            : $"队列已由用户取消；{requeuedCount} 个未完成文件已重新排队，可切换模型后直接点击开始转写。");
    }

    private int RequeueCancelledItemsForRetry()
    {
        var requeuedCount = 0;
        foreach (var queueItem in _queueItems.Where(item => item.State == QueueItemState.Cancelled))
        {
            queueItem.SetState(QueueItemState.Queued, "上次已取消");
            requeuedCount++;
        }

        return requeuedCount;
    }

    private void UpdateProgress(OperationProgress progress)
    {
        _currentState = progress.State;
        StatusTitleText.Text = GetStateTitle(progress.State);
        StatusDetailText.Text = progress.Message;
        FooterStatusText.Text = progress.Message;
        ApplyBadge(progress.State);
        ApplyStageVisuals(progress.Phase, progress.State);

        if (progress.Fraction is double fraction && double.IsFinite(fraction))
        {
            var percentage = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero);
            percentage = Math.Max(_lastDisplayedPercentage, percentage);
            _lastDisplayedPercentage = percentage;
            OverallProgressBar.IsIndeterminate = false;
            OverallProgressBar.Value = percentage;
            ProgressPercentText.Text = $"{percentage}%";
        }

        if (progress.State == JobState.Completed && _activeQueueItem is null)
        {
            _lastDisplayedPercentage = 100;
            OverallProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
        }

        _estimatedCompletionAt = progress.EstimatedRemaining is TimeSpan remaining
            ? DateTimeOffset.Now + remaining
            : null;
        UpdateElapsedAndEtaDisplay();
        AppendProgressLog(progress);
    }

    private void ShowResult(PipelineResult result)
    {
        _lastOutputDirectory = result.OutputDirectory;
        var primaryOutput = SelectPrimaryOutput(result);
        _lastPrimaryOutputPath = primaryOutput?.Path;
        OpenResultButton.IsEnabled = primaryOutput is not null;
        OpenResultButton.Content = primaryOutput is null
            ? "打开结果"
            : $"打开 {GetOutputFormatDisplayName(primaryOutput.Value.Format)}";

        var speakers = result.Document.Segments
            .Where(segment => segment.SpeakerId > 0)
            .Select(segment => segment.SpeakerId)
            .Distinct()
            .Count();
        var partialLabel = result.Document.IsPartial ? " · 注意：结果不完整" : string.Empty;
        var formatCount = Math.Max(1, result.OutputPaths.Count);
        ResultSummaryText.Text = $"{result.Document.Segments.Count:N0} 个时间段 · {speakers:N0} 位说话人 · {formatCount} 种格式 · {FormatDuration(result.Document.MediaDuration)}{partialLabel}";

        var preview = TranscriptFormatter.ToTxt(result.Document, options: result.FormattingOptions);
        const int previewLimit = 200_000;
        PreviewTextBox.Text = preview.Length <= previewLimit
            ? preview
            : preview[..previewLimit] + Environment.NewLine + Environment.NewLine + "—— 预览到此为止；完整内容请打开导出文件 ——";
        PreviewTextBox.ScrollToHome();
        ResultCard.Visibility = Visibility.Visible;
    }

    private void UpdateCancelledState()
    {
        _currentState = JobState.Cancelled;
        StatusTitleText.Text = "任务已取消";
        StatusDetailText.Text = "原始媒体文件没有被修改。下次可以重新开始。";
        FooterStatusText.Text = "已取消";
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = _lastDisplayedPercentage;
        ProgressPercentText.Text = $"{_lastDisplayedPercentage}%";
        _estimatedCompletionAt = null;
        ElapsedText.Text = $"已用时 {FormatElapsed(_taskStopwatch?.Elapsed ?? TimeSpan.Zero)} · 已取消";
        LogProgressSummaryText.Text = $"{_lastDisplayedPercentage}% · 已取消";
        StatusBadgeText.Text = "已取消";
        StatusBadgeBorder.Background = IdleBackground;
        StatusBadgeText.Foreground = IdleForeground;
        AppendLog("任务已由用户取消。");
    }

    private void UpdateFailedState(Exception exception)
    {
        _currentState = JobState.Failed;
        StatusTitleText.Text = "转写没有完成";
        StatusDetailText.Text = GetFriendlyError(exception);
        FooterStatusText.Text = "失败 · 展开运行记录可查看详情";
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = _lastDisplayedPercentage;
        ProgressPercentText.Text = $"{_lastDisplayedPercentage}%";
        _estimatedCompletionAt = null;
        ElapsedText.Text = $"已用时 {FormatElapsed(_taskStopwatch?.Elapsed ?? TimeSpan.Zero)} · 已停止";
        LogProgressSummaryText.Text = $"{_lastDisplayedPercentage}% · 失败";
        StatusBadgeText.Text = "需要处理";
        StatusBadgeBorder.Background = DangerBackground;
        StatusBadgeText.Foreground = DangerForeground;
        AppendLog(exception.ToString());
        LogExpander.IsExpanded = true;
    }

    private void RequestCancellation()
    {
        if (!_isRunning || _runCancellation is null || _runCancellation.IsCancellationRequested)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        StatusBadgeText.Text = "正在取消";
        StatusTitleText.Text = "正在安全停止…";
        StatusDetailText.Text = "正在结束当前处理进程，并停止队列中尚未开始的文件，可能需要几秒钟。";
        FooterStatusText.Text = "正在停止队列";
        AppendLog("收到取消请求，正在停止当前任务和剩余队列。");
        _runCancellation.Cancel();
    }

    private void ApplyBadge(JobState state)
    {
        switch (state)
        {
            case JobState.Completed:
                StatusBadgeText.Text = "已完成";
                StatusBadgeBorder.Background = SuccessBackground;
                StatusBadgeText.Foreground = SuccessForeground;
                break;
            case JobState.Failed:
                StatusBadgeText.Text = "失败";
                StatusBadgeBorder.Background = DangerBackground;
                StatusBadgeText.Foreground = DangerForeground;
                break;
            case JobState.Idle:
            case JobState.Cancelled:
            case JobState.Interrupted:
                StatusBadgeText.Text = "等待开始";
                StatusBadgeBorder.Background = IdleBackground;
                StatusBadgeText.Foreground = IdleForeground;
                break;
            default:
                StatusBadgeText.Text = "处理中";
                StatusBadgeBorder.Background = PrimaryBackground;
                StatusBadgeText.Foreground = PrimaryForeground;
                break;
        }
    }

    private void ApplyStageVisuals(PipelinePhase phase, JobState state)
    {
        var currentStage = phase switch
        {
            PipelinePhase.ProbeMedia or PipelinePhase.PrepareModel => 0,
            PipelinePhase.ExtractAudio => 1,
            PipelinePhase.MossDiarization or PipelinePhase.QwenRecognition => 2,
            PipelinePhase.Validate or PipelinePhase.Export => 3,
            PipelinePhase.Completed => 4,
            _ => state switch
            {
                JobState.ProbingMedia or JobState.WaitingForModel or JobState.DownloadingModel or JobState.VerifyingModel => 0,
                JobState.ExtractingAudio => 1,
                JobState.ReadyToTranscribe or JobState.Transcribing => 2,
                JobState.ValidatingResult or JobState.Exporting => 3,
                JobState.Completed => 4,
                _ => -1,
            },
        };

        SetStageVisual(StageModelBorder, StageModelText, 0, currentStage);
        SetStageVisual(StageAudioBorder, StageAudioText, 1, currentStage);
        SetStageVisual(StageTranscribeBorder, StageTranscribeText, 2, currentStage);
        SetStageVisual(StageExportBorder, StageExportText, 3, currentStage);
    }

    private void ResetStageVisuals()
    {
        SetStageVisual(StageModelBorder, StageModelText, 0, -1);
        SetStageVisual(StageAudioBorder, StageAudioText, 1, -1);
        SetStageVisual(StageTranscribeBorder, StageTranscribeText, 2, -1);
        SetStageVisual(StageExportBorder, StageExportText, 3, -1);
    }

    private static void SetStageVisual(Border border, TextBlock text, int stage, int currentStage)
    {
        if (currentStage > stage)
        {
            border.Background = SuccessBackground;
            border.BorderBrush = SuccessBackground;
            text.Foreground = SuccessForeground;
            text.FontWeight = FontWeights.SemiBold;
            return;
        }

        if (currentStage == stage)
        {
            border.Background = PrimaryBackground;
            border.BorderBrush = PrimaryBackground;
            text.Foreground = PrimaryForeground;
            text.FontWeight = FontWeights.SemiBold;
            return;
        }

        border.Background = StageIdleBackground;
        border.BorderBrush = StageIdleBorder;
        text.Foreground = IdleForeground;
        text.FontWeight = FontWeights.Normal;
    }

    private void SetIdleState(string title, string detail, string badge)
    {
        _currentState = JobState.Idle;
        StatusTitleText.Text = title;
        StatusDetailText.Text = detail;
        StatusBadgeText.Text = badge;
        StatusBadgeBorder.Background = IdleBackground;
        StatusBadgeText.Foreground = IdleForeground;
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        ElapsedText.Text = "等待开始";
        LogProgressSummaryText.Text = "0% · 等待开始";
        _lastDisplayedPercentage = 0;
        _estimatedCompletionAt = null;
        FooterStatusText.Text = "就绪";
        ResetStageVisuals();
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var wasAtEnd = LogTextBox.VerticalOffset >= LogTextBox.ExtentHeight - LogTextBox.ViewportHeight - 2;
        var line = $"{DateTime.Now:HH:mm:ss}  {RedactSensitiveText(message.Trim())}";
        LogTextBox.AppendText(line + Environment.NewLine);
        if (LogTextBox.Text.Length > MaximumVisibleLogCharacters)
        {
            var targetRemoval = LogTextBox.Text.Length - (MaximumVisibleLogCharacters * 4 / 5);
            var lineBreak = LogTextBox.Text.IndexOf('\n', targetRemoval);
            if (lineBreak >= 0)
            {
                LogTextBox.Text = "……较早的界面日志已省略，完整诊断仍保存在任务目录……" +
                    Environment.NewLine +
                    LogTextBox.Text[(lineBreak + 1)..];
                LogTextBox.CaretIndex = LogTextBox.Text.Length;
            }
        }

        if (wasAtEnd)
        {
            LogTextBox.ScrollToEnd();
        }
    }

    private void AppendProgressLog(OperationProgress progress)
    {
        var percentage = _lastDisplayedPercentage;
        if (_lastLoggedProgressPercentage == percentage &&
            _lastLoggedProgressState == progress.State &&
            string.Equals(_lastLoggedProgressMessage, progress.Message, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedProgressPercentage = percentage;
        _lastLoggedProgressState = progress.State;
        _lastLoggedProgressMessage = progress.Message;
        var etaText = progress.State == JobState.Completed
            ? "已完成"
            : progress.EstimatedRemaining is TimeSpan remaining
                ? $"ETA 约 {FormatElapsed(remaining)}"
                : "ETA 估算中";
        AppendLog($"[{percentage}% | {etaText}] [{GetPhaseDisplayName(progress.Phase)}] {progress.Message}");
    }

    private void AppendDiagnosticLog(string message)
    {
        var etaText = _estimatedCompletionAt is DateTimeOffset completion
            ? $"ETA 约 {FormatElapsed(Maximum(completion - DateTimeOffset.Now, TimeSpan.FromSeconds(1)))}"
            : "ETA 估算中";
        AppendLog($"[{_lastDisplayedPercentage}% | {etaText}] [诊断] {message}");
    }

    private void UpdateElapsedAndEtaDisplay()
    {
        var elapsed = _taskStopwatch?.Elapsed ?? TimeSpan.Zero;
        string etaText;
        if (_currentState == JobState.Completed)
        {
            etaText = "已完成";
        }
        else if (_estimatedCompletionAt is DateTimeOffset completion)
        {
            var remaining = completion - DateTimeOffset.Now;
            etaText = $"预计剩余约 {FormatElapsed(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1))}";
        }
        else
        {
            etaText = "正在估算剩余时间";
        }

        ElapsedText.Text = $"已用时 {FormatElapsed(elapsed)} · {etaText}";
        LogProgressSummaryText.Text = $"{_lastDisplayedPercentage}% · {etaText}";
    }

    private static string GetPhaseDisplayName(PipelinePhase phase) => phase switch
    {
        PipelinePhase.ProbeMedia => "媒体检查",
        PipelinePhase.PrepareModel => "模型准备",
        PipelinePhase.ExtractAudio => "音频提取",
        PipelinePhase.MossDiarization => "MOSS 识别/分人",
        PipelinePhase.QwenRecognition => "Qwen 高精度",
        PipelinePhase.Validate => "结果检查",
        PipelinePhase.Export => "文件导出",
        PipelinePhase.Completed => "完成",
        _ => "准备",
    };

    private static bool TryGetDroppedFiles(IDataObject data, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        paths = files
            .Where(path => File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Count > 0;
    }

    private IReadOnlyList<string> GetSelectedLanguageCodes()
    {
        return GetLanguageOptions()
            .Where(option => option.CheckBox.IsChecked == true)
            .Select(option => option.Code)
            .ToArray();
    }

    private bool HasSelectedLanguage() =>
        GetLanguageOptions().Any(option => option.CheckBox.IsChecked == true);

    private TranscriptOutputFormat GetSelectedOutputFormats()
    {
        var formats = TranscriptOutputFormat.None;
        if (TxtOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Txt;
        }
        if (MarkdownOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Markdown;
        }
        if (DocxOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Docx;
        }
        if (PdfOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Pdf;
        }
        if (SrtOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Srt;
        }
        if (JsonOutputCheckBox.IsChecked == true)
        {
            formats |= TranscriptOutputFormat.Json;
        }

        return formats;
    }

    private bool HasSelectedOutputFormat() => GetSelectedOutputFormats() != TranscriptOutputFormat.None;

    private void UpdateStartButtonState()
    {
        StartButton.IsEnabled = !_isRunning &&
            !_isInstallingQwen &&
            _queueItems.Any(item => item.State == QueueItemState.Queued) &&
            HasSelectedLanguage() &&
            HasSelectedOutputFormat();
    }

    private IEnumerable<(CheckBox CheckBox, string Code)> GetLanguageOptions()
    {
        yield return (ChineseLanguageCheckBox, "zh");
        yield return (EnglishLanguageCheckBox, "en");
        yield return (CantoneseLanguageCheckBox, "yue");
        yield return (JapaneseLanguageCheckBox, "ja");
        yield return (KoreanLanguageCheckBox, "ko");
        yield return (FrenchLanguageCheckBox, "fr");
        yield return (GermanLanguageCheckBox, "de");
        yield return (SpanishLanguageCheckBox, "es");
        yield return (PortugueseLanguageCheckBox, "pt");
        yield return (RussianLanguageCheckBox, "ru");
        yield return (ItalianLanguageCheckBox, "it");
    }

    private TranscriptionMode GetSelectedTranscriptionMode()
    {
        if (QwenLocalModeRadioButton.IsChecked == true)
        {
            return TranscriptionMode.QwenLocalHighAccuracy;
        }

        return MossLocalModeRadioButton.IsChecked == true
            ? TranscriptionMode.MossLocalFast
            : TranscriptionMode.WhisperTurboFast;
    }

    private static string GetTranscriptionModeDisplayName(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.WhisperTurboFast => "Whisper Turbo / Faster-Whisper 快速模式",
        TranscriptionMode.QwenLocalHighAccuracy => "Qwen3-ASR + ForcedAligner 本地高精度",
        TranscriptionMode.MossLocalFast => "MOSS 本地兼容模式",
        _ => "未知模式"
    };

    private static string GetSelectionPrompt(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.WhisperTurboFast => "添加媒体文件后即可使用快速模式。首次使用请先安装 Whisper Turbo 组件。",
        TranscriptionMode.QwenLocalHighAccuracy => "添加媒体文件后即可使用本地高精度识别。首次使用请先安装高精度组件。",
        TranscriptionMode.MossLocalFast => "添加媒体文件后即可使用 MOSS 本地兼容模式。首次使用需下载模型。",
        _ => "请选择一种本地识别方案。"
    };

    private static string GetReadyDetail(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.WhisperTurboFast => "点击“开始转写”后会按队列顺序处理。如尚未安装，请先使用右侧的“安装快速模式组件”。",
        TranscriptionMode.QwenLocalHighAccuracy => "点击“开始转写”后会按队列顺序处理。如尚未安装，请先使用右侧的“安装高精度组件”。",
        TranscriptionMode.MossLocalFast => "点击“开始转写”后会按队列顺序处理。首次运行会先下载 MOSS 本地模型。",
        _ => "请选择一种本地识别方案。"
    };

    private static string GetPreparationMessage(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.WhisperTurboFast => "正在检查 Whisper Turbo 本地模型和 Faster-Whisper CUDA 环境…",
        TranscriptionMode.QwenLocalHighAccuracy => "正在检查 Qwen 本地权重、对齐模型和 PyTorch CUDA 环境…",
        TranscriptionMode.MossLocalFast => "正在检查 MOSS 本地模型和 Vulkan 运行环境…",
        _ => "正在检查本地运行环境…"
    };

    private static string? FindQwenInstallerPath()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = baseDirectory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tools", "qwen", "Install-QwenRuntime.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var workingDirectoryCandidate = Path.Combine(
            Environment.CurrentDirectory,
            "tools",
            "qwen",
            "Install-QwenRuntime.ps1");
        return File.Exists(workingDirectoryCandidate) ? workingDirectoryCandidate : null;
    }

    private static string? FindWhisperInstallerPath()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = baseDirectory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tools", "whisper", "Install-FasterWhisperRuntime.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        var workingDirectoryCandidate = Path.Combine(
            Environment.CurrentDirectory,
            "tools",
            "whisper",
            "Install-FasterWhisperRuntime.ps1");
        return File.Exists(workingDirectoryCandidate) ? workingDirectoryCandidate : null;
    }

    private static string FormatSelectedLanguages(IReadOnlyList<string> languageCodes) =>
        string.Join(" + ", languageCodes.Select(code => code switch
        {
            "zh" => "中文",
            "en" => "English",
            "yue" => "粤语",
            "ja" => "日本語",
            "ko" => "한국어",
            "fr" => "Français",
            "de" => "Deutsch",
            "es" => "Español",
            "pt" => "Português",
            "ru" => "Русский",
            "it" => "Italiano",
            _ => code,
        }));

    private static string FormatOutputFormats(TranscriptOutputFormat formats) =>
        string.Join(" / ", new[]
        {
            TranscriptOutputFormat.Txt,
            TranscriptOutputFormat.Markdown,
            TranscriptOutputFormat.Docx,
            TranscriptOutputFormat.Pdf,
            TranscriptOutputFormat.Srt,
            TranscriptOutputFormat.Json,
        }.Where(format => formats.HasFlag(format)).Select(GetOutputFormatDisplayName));

    private static string GetOutputFormatDisplayName(TranscriptOutputFormat format) => format switch
    {
        TranscriptOutputFormat.Txt => "TXT",
        TranscriptOutputFormat.Markdown => "Markdown",
        TranscriptOutputFormat.Docx => "Word",
        TranscriptOutputFormat.Pdf => "PDF",
        TranscriptOutputFormat.Srt => "SRT",
        TranscriptOutputFormat.Json => "JSON",
        _ => format.ToString(),
    };

    private static (TranscriptOutputFormat Format, string Path)? SelectPrimaryOutput(PipelineResult result)
    {
        TranscriptOutputFormat[] preferredOrder =
        [
            TranscriptOutputFormat.Docx,
            TranscriptOutputFormat.Pdf,
            TranscriptOutputFormat.Markdown,
            TranscriptOutputFormat.Txt,
            TranscriptOutputFormat.Srt,
            TranscriptOutputFormat.Json,
        ];
        foreach (var format in preferredOrder)
        {
            if (result.OutputPaths.TryGetValue(format, out var path) && !string.IsNullOrWhiteSpace(path))
            {
                return (format, path);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.TxtPath))
        {
            return (TranscriptOutputFormat.Txt, result.TxtPath);
        }
        if (!string.IsNullOrWhiteSpace(result.SrtPath))
        {
            return (TranscriptOutputFormat.Srt, result.SrtPath);
        }
        return !string.IsNullOrWhiteSpace(result.JsonPath)
            ? (TranscriptOutputFormat.Json, result.JsonPath)
            : null;
    }

    private static string FormatSwitch(bool enabled) => enabled ? "开" : "关";

    private static string GetStateTitle(JobState state) => state switch
    {
        JobState.WaitingForModel => "检查本地模型",
        JobState.DownloadingModel => "下载识别模型",
        JobState.VerifyingModel => "校验模型完整性",
        JobState.ProbingMedia => "读取视频信息",
        JobState.ExtractingAudio => "提取并整理音频",
        JobState.ReadyToTranscribe => "准备开始识别",
        JobState.Transcribing => "识别语音并区分说话人",
        JobState.ValidatingResult => "检查转写结果",
        JobState.Exporting => "生成所选输出文件",
        JobState.Completed => "转写完成",
        JobState.Cancelling => "正在取消",
        JobState.Cancelled => "任务已取消",
        JobState.Failed => "转写没有完成",
        JobState.Interrupted => "上次任务被中断",
        _ => "准备就绪"
    };

    private string GetFriendlyError(Exception exception)
    {
        var message = exception switch
        {
            FileNotFoundException => exception.Message,
            UnauthorizedAccessException => "没有权限读取视频或写入输出文件夹。请更换输出位置后重试。",
            HttpRequestException => "本地模型组件下载或更新失败。请检查网络后重新安装对应组件；已安装的本地识别不需要联网。",
            IOException => $"文件读写失败：{exception.Message}",
            InvalidDataException => $"视频或识别结果格式异常：{exception.Message}",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "发生未知错误，请展开运行记录查看详情。" : exception.Message
        };

        return RedactSensitiveText(message);
    }

    // Installer diagnostics can include a user-provided environment variable.
    // Keep the on-screen log useful without ever echoing an access token.
    private static string RedactSensitiveText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Regex.Replace(
            value,
            "(?i)(HF_TOKEN|MODELSCOPE_API_TOKEN|MODELSCOPE_TOKEN)\\s*[=:]\\s*[^\\s,;]+",
            "$1=[已隐藏]");
    }

    private void ShowFriendlyError(string message, string title)
    {
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void TryOpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppendLog($"无法打开：{ex}");
            ShowFriendlyError("Windows 无法打开这个位置，请从文件资源管理器手动进入输出文件夹。", "无法打开");
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size.ToString(size >= 100 || unitIndex == 0 ? "0" : "0.0", CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalHours = (int)elapsed.TotalHours;
        return totalHours > 0
            ? $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static TimeSpan Maximum(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return totalHours > 0
            ? $"{totalHours} 小时 {duration.Minutes} 分钟"
            : $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} 分钟";
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private enum QueueItemState
    {
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    private sealed class QueueItem : INotifyPropertyChanged
    {
        private QueueItemState _state = QueueItemState.Queued;
        private string _statusText = "排队中";

        public QueueItem(FileInfo file)
        {
            SourcePath = file.FullName;
            FileName = file.Name;
            Details = $"{file.Extension.TrimStart('.').ToUpperInvariant()} · {FormatFileSize(file.Length)}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SourcePath { get; }

        public string FileName { get; }

        public string Details { get; }

        public QueueItemState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                OnPropertyChanged(nameof(State));
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (string.Equals(_statusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public void SetState(QueueItemState state, string? detail = null)
        {
            State = state;
            StatusText = state switch
            {
                QueueItemState.Queued => string.IsNullOrWhiteSpace(detail) ? "排队中" : $"排队中 · {detail}",
                QueueItemState.Running => string.IsNullOrWhiteSpace(detail) ? "处理中" : $"处理中 · {detail}",
                QueueItemState.Completed => "已完成",
                QueueItemState.Failed => string.IsNullOrWhiteSpace(detail) ? "失败" : $"失败 · {detail}",
                QueueItemState.Cancelled => "已取消",
                _ => state.ToString(),
            };
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
