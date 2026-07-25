using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SmartBench.Services;

public enum AudioExportFormat
{
    Mp3,
    Wav
}

public sealed record AudioExportRequest(
    string DestinationPath,
    AudioExportFormat Format,
    int Mp3BitrateKbps,
    string Text,
    string? Voice,
    string Culture,
    int Rate,
    int Volume,
    int Pitch);

public static class AudioExportService
{
    public static Task ExportAsync(AudioExportRequest request) => Task.Run(() => Export(request));

    private static void Export(AudioExportRequest request)
    {
        var targetPath = Path.GetFullPath(request.DestinationPath);
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("导出位置无效。");
        Directory.CreateDirectory(targetDirectory);

        var targetName = Path.GetFileNameWithoutExtension(targetPath);
        var targetExtension = Path.GetExtension(targetPath);
        var partialPath = Path.Combine(targetDirectory, $".{targetName}.{Guid.NewGuid():N}.partial{targetExtension}");
        var temporaryWavePath = request.Format == AudioExportFormat.Wav
            ? partialPath
            : Path.Combine(Path.GetTempPath(), $"SmartBench-{Guid.NewGuid():N}.wav");

        try
        {
            CreatePcmWave(temporaryWavePath, request);
            if (request.Format == AudioExportFormat.Mp3)
            {
                using var reader = new WaveFileReader(temporaryWavePath);
                MediaFoundationEncoder.EncodeToMp3(reader, partialPath, request.Mp3BitrateKbps * 1000);
            }

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length < 512)
                throw new InvalidOperationException("音频编码没有生成有效文件。");

            File.Move(partialPath, targetPath, true);
        }
        finally
        {
            TryDelete(partialPath);
            if (request.Format == AudioExportFormat.Mp3) TryDelete(temporaryWavePath);
        }
    }

    private static void CreatePcmWave(string outputPath, AudioExportRequest request)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (!string.IsNullOrWhiteSpace(request.Voice)) synthesizer.SelectVoice(request.Voice);
        synthesizer.Rate = request.Rate;
        synthesizer.Volume = request.Volume;
        synthesizer.SetOutputToWaveFile(outputPath, new SpeechAudioFormatInfo(44_100, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.SpeakSsml(BuildSsml(request.Text, request.Pitch, request.Culture));
    }

    private static string BuildSsml(string text, int pitch, string culture)
    {
        var pitchText = pitch > 0 ? $"+{pitch}%" : $"{pitch}%";
        var escapedText = System.Security.SecurityElement.Escape(text) ?? string.Empty;
        return $"<speak version=\"1.0\" xml:lang=\"{culture}\"><prosody pitch=\"{pitchText}\">{escapedText}</prosody></speak>";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
