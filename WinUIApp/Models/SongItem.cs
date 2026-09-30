using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace LyricsApp.WinUI.Models;

/// <summary>列表歌曲项。</summary>
public sealed class SongItem : INotifyPropertyChanged
{
    // SolidColorBrush 是 XAML 对象只能在 UI 线程创建，而 SongItem 可能在后台线程（Task.Run 读标签）实例化，
    // 因此惰性创建：首次访问只发生在 UI 线程的绑定求值 / Processed 赋值时
    private static SolidColorBrush? _greenBgBrush;
    private static SolidColorBrush GreenBgBrush => _greenBgBrush ??= new(Windows.UI.Color.FromArgb(255, 198, 240, 198));
    private static SolidColorBrush? _clearBrush;
    private static SolidColorBrush ClearBrush => _clearBrush ??= new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private string _title = "";
    private string _artist = "";
    private bool _processed;

    public string No { get; set; } = "";

    /// <summary>音频文件路径；「已处理」移动后会被更新。</summary>
    public string File { get; set; } = "";

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    public string Artist
    {
        get => _artist;
        set { _artist = value; OnPropertyChanged(); }
    }

    /// <summary>全部替换已写入歌词标记（序号背景浅绿）。</summary>
    public bool Processed
    {
        get => _processed;
        set
        {
            _processed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProcessedBrush));
        }
    }

    /// <summary>序号背景画刷：已处理浅绿，未处理透明。</summary>
    public Brush ProcessedBrush => _processed ? GreenBgBrush : ClearBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
