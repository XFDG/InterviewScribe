using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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

    private readonly DispatcherTimer _elapsedTimer;
    private TranscriptionPipeline? _pipeline;
    private CancellationTokenSource? _runCancellation;
    private Stopwatch? _transcriptionStopwatch;
    private string? _sourcePath;
    private string? _lastTxtPath;
    private string? _lastOutputDirectory;
    private JobState _currentState = JobState.Idle;
    private bool _isRunning;
    private bool _closeAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;
        ResetStageVisuals();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择面试录屏",
            Filter = "视频与音频|*.mp4;*.mkv;*.mov;*.avi;*.m4v;*.webm;*.wmv;*.mpeg;*.mpg;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_sourcePath) ?? Environment.CurrentDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SelectSourceFile(dialog.FileName);
        }
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        BrowseFile_Click(sender, e);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
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
        e.Effects = !_isRunning && TryGetDroppedFile(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_isRunning && TryGetDroppedFile(e.Data, out var path))
        {
            SelectSourceFile(path);
        }

        e.Handled = true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var sourcePath = _sourcePath;
        if (_isRunning || string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        if (!File.Exists(sourcePath))
        {
            ShowFriendlyError("找不到所选视频。它可能已被移动或删除，请重新选择。", "文件不存在");
            SelectSourceFile(null);
            return;
        }

        var outputDirectory = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            ShowFriendlyError("请先选择转写结果的保存位置。", "缺少输出位置");
            return;
        }

        PrepareForRun();
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(UpdateProgress);

        try
        {
            var request = new PipelineRequest(sourcePath, outputDirectory);
            _pipeline ??= new TranscriptionPipeline();
            var result = await _pipeline.RunAsync(request, progress, _runCancellation.Token);

            foreach (var message in result.LogMessages)
            {
                AppendLog(message);
            }

            ShowResult(result);
            UpdateProgress(new OperationProgress(JobState.Completed, "TXT、SRT 和 JSON 已安全保存。", 1));
        }
        catch (OperationCanceledException)
        {
            UpdateCancelledState();
        }
        catch (Exception ex)
        {
            UpdateFailedState(ex);
            if (!_closeAfterCancellation)
            {
                ShowFriendlyError(GetFriendlyError(ex), "转写没有完成");
            }
        }
        finally
        {
            _elapsedTimer.Stop();
            _transcriptionStopwatch?.Stop();
            _runCancellation?.Dispose();
            _runCancellation = null;
            _isRunning = false;
            ChangeFileButton.IsEnabled = true;
            DropZoneBorder.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            StartButton.IsEnabled = !string.IsNullOrWhiteSpace(_sourcePath);

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

    private void OpenTxt_Click(object sender, RoutedEventArgs e)
    {
        var txtPath = _lastTxtPath;
        if (string.IsNullOrWhiteSpace(txtPath) || !File.Exists(txtPath))
        {
            ShowFriendlyError("找不到 TXT 结果文件。请到输出文件夹中确认文件是否被移动。", "文件不存在");
            return;
        }

        TryOpenWithShell(txtPath);
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
            "转写仍在进行。现在退出会取消本次任务，但不会删除原视频。要继续退出吗？",
            "取消转写并退出",
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
        if (_currentState == JobState.Transcribing && _transcriptionStopwatch is not null)
        {
            ElapsedText.Text = $"已识别 {FormatElapsed(_transcriptionStopwatch.Elapsed)}";
        }
    }

    private void SelectSourceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _sourcePath = null;
            EmptyFilePanel.Visibility = Visibility.Visible;
            SelectedFilePanel.Visibility = Visibility.Collapsed;
            OutputFolderTextBox.Text = string.Empty;
            StartButton.IsEnabled = false;
            return;
        }

        var extension = Path.GetExtension(path);
        if (!File.Exists(path) || !SupportedExtensions.Contains(extension))
        {
            ShowFriendlyError("请选择常见的视频或音频文件。支持 MP4、MKV、MOV、AVI、WEBM、MP3、WAV、M4A 等格式。", "不支持这个文件");
            return;
        }

        try
        {
            var file = new FileInfo(path);
            _sourcePath = file.FullName;
            SelectedFileNameText.Text = file.Name;
            SelectedFileDetailsText.Text = $"{extension.TrimStart('.').ToUpperInvariant()} · {FormatFileSize(file.Length)}";
            SelectedFileNameText.ToolTip = file.FullName;
            EmptyFilePanel.Visibility = Visibility.Collapsed;
            SelectedFilePanel.Visibility = Visibility.Visible;
            OutputFolderTextBox.Text = Path.Combine(file.DirectoryName ?? Environment.CurrentDirectory, "转写结果");
            StartButton.IsEnabled = true;
            ResultCard.Visibility = Visibility.Collapsed;
            _lastTxtPath = null;
            _lastOutputDirectory = null;
            SetIdleState("准备就绪", "点击“开始转写”。首次运行会先下载本地模型。", "可以开始");
            AppendLog($"已选择：{file.FullName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowFriendlyError($"无法读取这个文件：{ex.Message}", "文件不可用");
        }
    }

    private void PrepareForRun()
    {
        _isRunning = true;
        _closeAfterCancellation = false;
        _lastTxtPath = null;
        _lastOutputDirectory = null;
        ResultCard.Visibility = Visibility.Collapsed;
        PreviewTextBox.Clear();
        LogTextBox.Clear();
        ChangeFileButton.IsEnabled = false;
        DropZoneBorder.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        OverallProgressBar.IsIndeterminate = true;
        ProgressPercentText.Text = string.Empty;
        ElapsedText.Text = string.Empty;
        _transcriptionStopwatch = null;
        _elapsedTimer.Start();
        AppendLog($"任务开始：{_sourcePath}");
        UpdateProgress(new OperationProgress(JobState.WaitingForModel, "正在检查本地模型和运行环境…"));
    }

    private void UpdateProgress(OperationProgress progress)
    {
        if (progress.State == JobState.Transcribing && _currentState != JobState.Transcribing)
        {
            _transcriptionStopwatch = Stopwatch.StartNew();
        }

        _currentState = progress.State;
        StatusTitleText.Text = GetStateTitle(progress.State);
        StatusDetailText.Text = progress.Message;
        FooterStatusText.Text = progress.Message;
        ApplyBadge(progress.State);
        ApplyStageVisuals(progress.State);

        if (progress.Fraction is double fraction)
        {
            var normalized = fraction > 1 ? fraction / 100 : fraction;
            var percentage = Math.Clamp(normalized * 100, 0, 100);
            OverallProgressBar.IsIndeterminate = false;
            OverallProgressBar.Value = percentage;
            ProgressPercentText.Text = $"{percentage:0}%";
        }
        else
        {
            OverallProgressBar.IsIndeterminate = IsIndeterminateState(progress.State);
            ProgressPercentText.Text = string.Empty;
            if (!OverallProgressBar.IsIndeterminate)
            {
                OverallProgressBar.Value = progress.State == JobState.Completed ? 100 : 0;
            }
        }

        if (progress.State == JobState.Transcribing)
        {
            var elapsed = progress.Elapsed ?? _transcriptionStopwatch?.Elapsed;
            ElapsedText.Text = elapsed is null ? "正在本地识别" : $"已识别 {FormatElapsed(elapsed.Value)}";
        }
        else if (progress.Elapsed is TimeSpan operationElapsed)
        {
            ElapsedText.Text = $"用时 {FormatElapsed(operationElapsed)}";
        }
        else if (progress.State != JobState.Completed)
        {
            ElapsedText.Text = string.Empty;
        }

        AppendLog($"[{progress.State}] {progress.Message}");
    }

    private void ShowResult(PipelineResult result)
    {
        _lastTxtPath = result.TxtPath;
        _lastOutputDirectory = result.OutputDirectory;

        var speakers = result.Document.Segments
            .Where(segment => segment.SpeakerId > 0)
            .Select(segment => segment.SpeakerId)
            .Distinct()
            .Count();
        var partialLabel = result.Document.IsPartial ? " · 注意：结果不完整" : string.Empty;
        ResultSummaryText.Text = $"{result.Document.Segments.Count:N0} 个时间段 · {speakers:N0} 位说话人 · {FormatDuration(result.Document.MediaDuration)}{partialLabel}";

        var preview = TranscriptFormatter.ToTxt(result.Document);
        const int previewLimit = 200_000;
        PreviewTextBox.Text = preview.Length <= previewLimit
            ? preview
            : preview[..previewLimit] + Environment.NewLine + Environment.NewLine + "—— 预览到此为止；完整内容请打开 TXT 文件 ——";
        PreviewTextBox.ScrollToHome();
        ResultCard.Visibility = Visibility.Visible;
    }

    private void UpdateCancelledState()
    {
        _currentState = JobState.Cancelled;
        StatusTitleText.Text = "任务已取消";
        StatusDetailText.Text = "原视频没有被修改。下次可以重新开始。";
        FooterStatusText.Text = "已取消";
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 0;
        ProgressPercentText.Text = string.Empty;
        ElapsedText.Text = string.Empty;
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
        ProgressPercentText.Text = string.Empty;
        ElapsedText.Text = string.Empty;
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
        StatusDetailText.Text = "正在结束本地处理进程，可能需要几秒钟。";
        FooterStatusText.Text = "正在取消";
        AppendLog("收到取消请求，正在停止任务。");
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

    private void ApplyStageVisuals(JobState state)
    {
        var currentStage = state switch
        {
            JobState.WaitingForModel or JobState.DownloadingModel or JobState.VerifyingModel => 0,
            JobState.ProbingMedia or JobState.ExtractingAudio => 1,
            JobState.ReadyToTranscribe or JobState.Transcribing => 2,
            JobState.ValidatingResult or JobState.Exporting => 3,
            JobState.Completed => 4,
            _ => -1
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
        ProgressPercentText.Text = string.Empty;
        ElapsedText.Text = string.Empty;
        FooterStatusText.Text = "就绪";
        ResetStageVisuals();
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {message.Trim()}";
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static bool TryGetDroppedFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            return false;
        }

        path = files[0];
        return File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));
    }

    private static bool IsIndeterminateState(JobState state) => state is
        JobState.WaitingForModel or
        JobState.VerifyingModel or
        JobState.ProbingMedia or
        JobState.ReadyToTranscribe or
        JobState.Transcribing or
        JobState.ValidatingResult or
        JobState.Exporting or
        JobState.Cancelling;

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
        JobState.Exporting => "生成 TXT、SRT 和 JSON",
        JobState.Completed => "转写完成",
        JobState.Cancelling => "正在取消",
        JobState.Cancelled => "任务已取消",
        JobState.Failed => "转写没有完成",
        JobState.Interrupted => "上次任务被中断",
        _ => "准备就绪"
    };

    private static string GetFriendlyError(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        UnauthorizedAccessException => "没有权限读取视频或写入输出文件夹。请更换输出位置后重试。",
        HttpRequestException => "模型下载失败。请检查网络后重试；已经下载的部分会保留。",
        IOException => $"文件读写失败：{exception.Message}",
        InvalidDataException => $"视频或识别结果格式异常：{exception.Message}",
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "发生未知错误，请展开运行记录查看详情。" : exception.Message
    };

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
}
