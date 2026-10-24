using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SmartBench.Views;

namespace SmartBench;

public partial class MainWindow : Window
{
    // 画刷缓存：原先每次导航都用 BrushConverter 转换 4 次字符串，白白产生垃圾。
    private static readonly Brush ActiveBackground = CreateBrush("#18345A");
    private static readonly Brush ActiveForeground = Brushes.White;
    private static readonly Brush IdleBackground = Brushes.Transparent;
    private static readonly Brush IdleForeground = CreateBrush("#C9D5E9");

    private readonly TtsView _ttsView = new();
    private readonly CommunicationView _communicationView = new();
    private readonly UsbTransferView _usbTransferView = new();

    public MainWindow()
    {
        InitializeComponent();
        PageHost.Content = _ttsView;
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.7.0");
    }

    private static Brush CreateBrush(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = 1 };

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string destination) return;

        foreach (var candidate in new[] { TtsNavigation, CommunicationNavigation, UsbNavigation })
        {
            candidate.Background = IdleBackground;
            candidate.BorderBrush = IdleBackground;
            candidate.Foreground = IdleForeground;
        }
        button.Background = ActiveBackground;
        button.BorderBrush = ActiveBackground;
        button.Foreground = ActiveForeground;

        switch (destination)
        {
            case "tts":
                PageHost.Content = _ttsView;
                PageTitle.Text = "文本转语音";
                PageSubtitle.Text = "将文字自然地转换为本机语音";
                break;
            case "communication":
                PageHost.Content = _communicationView;
                PageTitle.Text = "通讯调试";
                PageSubtitle.Text = "RS232、Modbus RTU 与 UDP 收发";
                break;
            case "usb":
                PageHost.Content = _usbTransferView;
                PageTitle.Text = "U 盘批量下发";
                PageSubtitle.Text = "识别可用设备并完整复制选中的文件与文件夹";
                break;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ttsView.Dispose();
        _communicationView.Dispose();
        _usbTransferView.Dispose();
    }
}
