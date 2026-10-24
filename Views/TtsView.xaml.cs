using System.IO;
using System.Security;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartBench.Services;

namespace SmartBench.Views;

public partial class TtsView : UserControl, IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _playbackGate = new();
    private readonly Queue<int> _pendingPlaybackTokens = new();
    private bool _disposed;
    private bool _exporting;
    private int _playbackToken;

    public TtsView()
    {
        InitializeComponent();
        LoadVoices();
        _synthesizer.SpeakCompleted += Synthesizer_SpeakCompleted;
        UpdateLabels();
        UpdateExportControls();
    }

    private void LoadVoices()
    {
        foreach (var voice in _synthesizer.GetInstalledVoices().Where(item => item.Enabled))
        {
            VoiceSelector.Items.Add(voice.VoiceInfo.Name);
        }

        if (VoiceSelector.Items.Count > 0)
        {
            VoiceSelector.SelectedItem = _synthesizer.Voice.Name;
            UpdateVoiceCulture();
        }
        else
        {
            PlaybackStatus.Text = "未发现可用系统语音";
            SpeakButton.IsEnabled = false;
            ExportAudioButton.IsEnabled = false;
        }
    }

    private void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureText()) return;
        try
        {
            // 每次播放发放一个令牌并入队。SpeakCompleted 不携带是哪次播放触发的，
            // 但完成事件按播放顺序到达，因此可以出队比对：只有最新令牌才允许更新状态。
            // 否则取消旧播放产生的"已停止"回调会晚到，把新播放的"正在播放…"覆盖掉。
            var token = Interlocked.Increment(ref _playbackToken);
            lock (_playbackGate) _pendingPlaybackTokens.Enqueue(token);

            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Rate = (int)RateSlider.Value;
            _synthesizer.Volume = (int)VolumeSlider.Value;
            if (VoiceSelector.SelectedItem is string voice) _synthesizer.SelectVoice(voice);
            _synthesizer.SpeakSsmlAsync(BuildSsml(SpeechText.Text, (int)PitchSlider.Value, SelectedCulture()));
            PlaybackStatus.Text = "正在播放…";
        }
        catch (Exception exception)
        {
            PlaybackStatus.Text = "无法启动语音";
            MessageBox.Show($"语音引擎未能启动。\n\n{exception.Message}", "文本转语音", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_synthesizer.State == SynthesizerState.Speaking)
        {
            _synthesizer.Pause();
            PlaybackStatus.Text = "已暂停";
        }
        else if (_synthesizer.State == SynthesizerState.Paused)
        {
            _synthesizer.Resume();
            PlaybackStatus.Text = "继续播放…";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _playbackToken);
        lock (_playbackGate) _pendingPlaybackTokens.Clear();
        _synthesizer.SpeakAsyncCancelAll();
        PlaybackStatus.Text = "已停止";
    }

    private void ImportTextButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入文本", Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > 2 * 1024 * 1024) throw new InvalidOperationException("文本文件不能超过 2 MB。");
            SpeechText.Text = File.ReadAllText(file.FullName);
            PlaybackStatus.Text = $"已导入 {file.Name}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法导入文本", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearTextButton_Click(object sender, RoutedEventArgs e)
    {
        SpeechText.Clear();
        PlaybackStatus.Text = "文本已清空";
    }

    private async void ExportAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || !EnsureText()) return;
        var format = SelectedExportFormat();
        var extension = format == AudioExportFormat.Mp3 ? "mp3" : "wav";
        var dialog = new SaveFileDialog
        {
            Title = "导出语音",
            Filter = "MP3 音频 (*.mp3)|*.mp3|WAV 音频 (*.wav)|*.wav",
            FilterIndex = format == AudioExportFormat.Mp3 ? 1 : 2,
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"语音导出.{extension}",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        var request = new AudioExportRequest(
            dialog.FileName,
            format,
            SelectedBitrateKbps(),
            SpeechText.Text,
            VoiceSelector.SelectedItem as string,
            SelectedCulture(),
            (int)RateSlider.Value,
            (int)VolumeSlider.Value,
            (int)PitchSlider.Value);

        try
        {
            _exporting = true;
            ExportAudioButton.IsEnabled = false;
            PlaybackStatus.Text = $"正在导出 {format.ToString().ToUpperInvariant()}…";
            await AudioExportService.ExportAsync(request);
            PlaybackStatus.Text = $"已导出：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            PlaybackStatus.Text = $"{format.ToString().ToUpperInvariant()} 导出失败";
            var fallback = format == AudioExportFormat.Mp3 ? "\n\n可尝试切换为 WAV 格式导出。" : string.Empty;
            MessageBox.Show($"{exception.Message}{fallback}", "无法导出音频", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _exporting = false;
            ExportAudioButton.IsEnabled = VoiceSelector.Items.Count > 0;
        }
    }

    private void NaturalPreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(0, 0, 100, "已套用自然预设");
    private void AnnouncePreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(2, 4, 100, "已套用播报预设");
    private void SlowPreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(-3, -4, 95, "已套用慢速预设");

    private void ApplyPreset(int rate, int pitch, int volume, string status)
    {
        RateSlider.Value = rate;
        PitchSlider.Value = pitch;
        VolumeSlider.Value = volume;
        PlaybackStatus.Text = status;
    }

    private void Synthesizer_SpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        int token;
        lock (_playbackGate)
        {
            if (_pendingPlaybackTokens.Count == 0) return;
            token = _pendingPlaybackTokens.Dequeue();
        }
        if (token != Volatile.Read(ref _playbackToken)) return;

        Dispatcher.InvokeAsync(() => PlaybackStatus.Text = e.Cancelled ? "已停止" : "播放完成");
    }

    private void VoiceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceSelector.SelectedItem is string voice && _synthesizer.State == SynthesizerState.Ready)
        {
            try { _synthesizer.SelectVoice(voice); } catch { /* The export/play action will provide a user-facing error. */ }
        }
        UpdateVoiceCulture();
    }

    private void ExportFormatSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExportControls();

    private void SpeechText_TextChanged(object sender, TextChangedEventArgs e)
    {
        var seconds = Math.Max(1, SpeechText.Text.Length / 4d);
        CharacterCount.Text = $"{SpeechText.Text.Length:N0} 字符 · 约 {TimeSpan.FromSeconds(seconds):m\\:ss}";
    }

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void PitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private bool EnsureText()
    {
        if (!string.IsNullOrWhiteSpace(SpeechText.Text)) return true;
        PlaybackStatus.Text = "请先输入要处理的文字";
        return false;
    }

    private void UpdateLabels()
    {
        if (RateValue is null || PitchValue is null || VolumeValue is null) return;
        var rate = (int)RateSlider.Value;
        RateValue.Text = rate switch { 0 => "正常", > 0 => $"+{rate}", _ => rate.ToString() };
        var pitch = (int)PitchSlider.Value;
        PitchValue.Text = pitch > 0 ? $"+{pitch}%" : $"{pitch}%";
        var volume = VolumeSlider.Value;
        var dbfs = volume <= 0 ? "−∞ dBFS" : $"{20 * Math.Log10(volume / 100):0.0} dBFS";
        VolumeValue.Text = $"{volume:0}% · {dbfs}";
    }

    private void UpdateExportControls()
    {
        if (ExportFormatSelector is null || Mp3BitrateSelector is null || ExportAudioButton is null || ExportHint is null) return;
        var isMp3 = SelectedExportFormat() == AudioExportFormat.Mp3;
        Mp3BitrateSelector.IsEnabled = isMp3;
        ExportAudioButton.Content = isMp3 ? "导出 MP3 音频" : "导出 WAV 音频";
        ExportHint.Text = isMp3 ? $"{SelectedBitrateKbps()} kbps 单声道：适合语音发布与播放" : "44.1 kHz / 16-bit PCM：适合无损保存与二次处理";
    }

    private void UpdateVoiceCulture()
    {
        if (VoiceCulture is not null) VoiceCulture.Text = SelectedCulture();
    }

    private AudioExportFormat SelectedExportFormat() => ExportFormatSelector?.SelectedIndex == 1 ? AudioExportFormat.Wav : AudioExportFormat.Mp3;
    private int SelectedBitrateKbps() => Mp3BitrateSelector?.SelectedItem is ComboBoxItem item ? int.Parse(item.Content.ToString()!.Split(' ')[0]) : 96;

    private string SelectedCulture()
    {
        if (VoiceSelector.SelectedItem is string voice)
        {
            var installed = _synthesizer.GetInstalledVoices().FirstOrDefault(item => item.VoiceInfo.Name.Equals(voice, StringComparison.OrdinalIgnoreCase));
            if (installed is not null) return installed.VoiceInfo.Culture.Name;
        }
        return "zh-CN";
    }

    private static string BuildSsml(string text, int pitch, string culture)
    {
        var pitchText = pitch > 0 ? $"+{pitch}%" : $"{pitch}%";
        var escapedText = SecurityElement.Escape(text) ?? string.Empty;
        return $"<speak version=\"1.0\" xml:lang=\"{culture}\"><prosody pitch=\"{pitchText}\">{escapedText}</prosody></speak>";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.Dispose();
    }
}
