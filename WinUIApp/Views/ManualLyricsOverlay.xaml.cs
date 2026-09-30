using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LyricsApp.WinUI.Controls;
using LyricsApp.WinUI.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LyricsApp.WinUI.Views;

/// <summary>
/// 手工打词悬浮层：上方左右文本框（左=主界面歌词框内容只读，右=打词输入），
/// 底部播放/暂停 + 3 倍慢放开关 + 进度条；控制与主界面/匹配悬浮层是同一播放器。
/// </summary>
public sealed partial class ManualLyricsOverlay : UserControl
{
    private readonly MainWindow _owner;
    private readonly SongItem _item; // 当前操作的歌曲（写入目标）
    private readonly PlayerEngine _player = SharedPlayer.Instance.Engine;
    private readonly DispatcherQueueTimer _uiTimer;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly KeyEventHandler _rootPreviewHandler; // 窗口根隧道键监听（空格/回车 → 打词）
    private bool _closing;
    private bool _sliderDragging;

    // 时间轴微调状态：拖动中冻结的播放位置基准、上次定位到的行
    private double _jogStartMs;
    private int _lastJogLine;
    private ScrollViewer? _workScroll;
    private ToolTip? _sliderTip;
    private bool _ownsTip; // 自建气泡时手动控制开关

    /// <summary>关闭时完成。</summary>
    public Task<bool> Completion => _completion.Task;

    public ManualLyricsOverlay(MainWindow owner, SongItem item, string sourceLyrics)
    {
        _owner = owner;
        _item = item;
        _rootPreviewHandler = OnRootPreviewKeyDown;
        InitializeComponent();
        SourceBox.Text = sourceLyrics;

        // 左侧歌词变化（如滑杆平移时间戳）且右侧为空时，滚动歌词框立即重新同步左侧内容
        SourceBox.TextChanged += (s, e) =>
        {
            if (WorkBox.Text.Length == 0) RefreshOverlayLyrics();
        };

        // 顶部波形进度条：按下从该处播放，松开落到最终位置；后台解码整首波形
        Waveform.SeekPressed += OnWaveformSeekPressed;
        Waveform.SeekReleased += r => SeekToWave(r);
        _ = Waveform.LoadAsync(SharedPlayer.Instance.CurrentFile);

        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _uiTimer.Tick += (s, e) =>
        {
            var dur = _player.Duration;
            Waveform.SetProgress(dur > TimeSpan.Zero ? _player.Position.TotalMilliseconds / dur.TotalMilliseconds : 0);
            var pos = _player.Position;
            TimeLabel.Text = $"{(int)pos.TotalMinutes:D2}:{pos.Seconds:D2}";
            // 滚动歌词框跟随播放进度；拖动微调滑杆期间由滑杆全权控制，避免两者互相覆盖
            if (LyricsOverlayToggle.IsOn && !_sliderDragging)
                OverlayLyricsView.SetPosition(_player.Position);
        };
        _uiTimer.Start();

        // 滚动歌词框：当前行变化滚动居中、拖动视口同步、点击行从该句播放（同主界面歌词区）
        OverlayLyricsView.CurrentLineChanged += idx =>
        {
            if (idx < 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                var target = Math.Max(0, OverlayLyricsView.GetLineTop(idx) - (OverlayLyricsScroll.ViewportHeight - LyricsCanvas.RowHeight) / 2);
                OverlayLyricsScroll.ChangeView(null, target, null);
            });
        };
        OverlayLyricsScroll.ViewChanged += (s, e) =>
            OverlayLyricsView.SetViewport((float)OverlayLyricsScroll.VerticalOffset, (float)OverlayLyricsScroll.ViewportHeight);
        OverlayLyricsView.LineClicked += idx =>
        {
            if (idx < 0 || idx >= _overlayLines.Count) return;
            _player.Seek(_overlayLines[idx].Time);
            OverlayLyricsView.SetPosition(_overlayLines[idx].Time);
            if (_player.Duration > TimeSpan.Zero && _player.State != NAudio.Wave.PlaybackState.Playing)
                _player.Play();
        };

        // 底部滑杆与播放进度解耦：左右拖动调整歌词时间轴（±5s），松手改写时间戳并回归置中
        ProgressSlider.ValueChanged += (s, e) => { if (_sliderDragging) OnJogValueChanged(); };

        // 播放/暂停标题与主界面统一方法同步（StateChanged 事件驱动）
        MainWindow.SyncPlayButton(PlayPauseButton, _player);
        _player.StateChanged += OnStateChanged;
        _player.PlaybackEnded += () => DispatcherQueue.TryEnqueue(() => Waveform.SetProgress(0));

        // Slider 内部 Thumb 会吞掉指针事件，必须 handledEventsToo 捕获（与主界面一致）
        ProgressSlider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSliderPointerPressed), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSliderPointerReleased), true);
        ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSliderPointerReleased), true);

        // 面板尺寸跟随主窗口：长宽 = 主窗口客户区的 3/4
        SizeChanged += (s, e) =>
        {
            PanelBorder.Width = e.NewSize.Width * 3.0 / 4.0;
            PanelBorder.Height = e.NewSize.Height * 3.0 / 4.0;
        };
    }

    private void OnStateChanged() =>
        DispatcherQueue.TryEnqueue(() => MainWindow.SyncPlayButton(PlayPauseButton, _player));

    // ===== 显示 / 关闭（带动画，与前两个悬浮层一致） =====
    public void Show()
    {
        Visibility = Visibility.Visible;
        IsTabStop = true;
        Focus(FocusState.Programmatic);
        // 窗口根隧道捕获空格/回车：抢在按钮类处理（焦点按钮自触发）与冒泡之前，保证快捷键固定触发打词
        if (_owner.Content is UIElement root)
            root.AddHandler(UIElement.PreviewKeyDownEvent, _rootPreviewHandler, true);
        var sb = new Storyboard();
        AddFade(sb, MaskBorder, 0, 1, 180, null);
        AddFade(sb, PanelBorder, 0, 1, 220, EasingMode.EaseOut);
        AddSlide(sb, PanelTransform, 48, 0, 220, EasingMode.EaseOut);
        sb.Begin();
    }

    private async void Close()
    {
        if (_closing) return;
        _closing = true;
        // 慢速只在悬浮层内生效：关闭时恢复常速并同步关闭开关
        if (SlowToggle.IsOn)
            SlowToggle.IsOn = false; // Toggled 事件回调 SetSlowSpeed(false)
        else
            _player.SetSlowSpeed(false);
        await PlayHideAnimationAsync();
        if (_owner.Content is UIElement root)
            root.RemoveHandler(UIElement.PreviewKeyDownEvent, _rootPreviewHandler);
        _completion.TrySetResult(true);
        Visibility = Visibility.Collapsed;
        IsTabStop = false; // 快捷键仅在本悬浮层打开期间生效
    }

    private Task PlayHideAnimationAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sb = new Storyboard();
        AddFade(sb, MaskBorder, 1, 0, 160, null);
        AddFade(sb, PanelBorder, 1, 0, 200, EasingMode.EaseIn);
        AddSlide(sb, PanelTransform, 0, 48, 200, EasingMode.EaseIn);
        sb.Completed += (s, e) => tcs.TrySetResult();
        sb.Begin();
        return tcs.Task;
    }

    private static void AddFade(Storyboard sb, DependencyObject target, double from, double to, int ms, EasingMode? easing)
    {
        var anim = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
        if (easing != null) anim.EasingFunction = new CubicEase { EasingMode = easing.Value };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
    }

    private static void AddSlide(Storyboard sb, TranslateTransform target, double from, double to, int ms, EasingMode easing)
    {
        var anim = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
        anim.EasingFunction = new CubicEase { EasingMode = easing };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Y");
        sb.Children.Add(anim);
    }

    // ===== 播放控制 =====
    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_player.Duration == TimeSpan.Zero) return;
        RefreshOverlayLyrics(); // 点击播放时取词渲染：右侧文本框非空取右侧，否则取左侧
        _player.Toggle();
    }

    // 滚动歌词框数据：源文本变化才重建，SetPosition 由定时器驱动
    private List<LrcLine> _overlayLines = new();
    private string? _overlaySource;

    private void RefreshOverlayLyrics()
    {
        var src = WorkBox.Text.Length > 0 ? WorkBox.Text : SourceBox.Text;
        OverlayLyricsView.SetPosition(_player.Position);
        if (src == _overlaySource) return;
        _overlaySource = src;
        _overlayLines = LrcParser.Parse(src);
        OverlayLyricsView.SetData(_overlayLines);
        OverlayLyricsView.SetPosition(_player.Position);
    }

    // 开关：显示/隐藏覆盖在打词输入框上的滚动歌词框
    private void OnLyricsOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        OverlayLyricsScroll.Visibility = LyricsOverlayToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!LyricsOverlayToggle.IsOn)
            CenterGuide.Visibility = Visibility.Collapsed; // 防御：拖动中关闭开关时收起虚线
        else
            RefreshOverlayLyrics();
    }

    private void OnSlowToggled(object sender, RoutedEventArgs e) =>
        _player.SetSlowSpeed(SlowToggle.IsOn);

    // 波形把柄：按下即从该位置播放（与主界面进度条行为一致），拖动只做预览，松手落到最终位置
    private void OnWaveformSeekPressed(double ratio)
    {
        SeekToWave(ratio);
        if (_player.Duration > TimeSpan.Zero && _player.State != NAudio.Wave.PlaybackState.Playing)
            _player.Play();
    }

    private void SeekToWave(double ratio)
    {
        if (_player.Duration <= TimeSpan.Zero) return;
        _player.Seek(TimeSpan.FromMilliseconds(_player.Duration.TotalMilliseconds * Math.Clamp(ratio, 0, 1)));
    }

    // ===== 歌词时间轴偏移滑杆：0~100 映射 -5s~+5s，默认置中；只能拖动，松手改写时间戳 =====
    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _sliderDragging = true;
        // 冻结按下时的播放位置作为基准，拖动期间滚动/Seek 都相对它计算
        _jogStartMs = _player.Position.TotalMilliseconds;
        _lastJogLine = int.MinValue;
        // 行时间直接用滚动歌词框已解析的歌词；尚无数据（未点过播放）时先取词解析
        if (_overlayLines.Count == 0) RefreshOverlayLyrics();
        CenterGuide.X2 = WorkBoxHost.ActualWidth;
        // 虚线只在滚动歌词框显示时出现（覆盖其上方作基准线）
        CenterGuide.Visibility = LyricsOverlayToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UpdateSliderTip();
        if (_ownsTip) _sliderTip!.IsOpen = true;
    }

    private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_sliderDragging) return;
        _sliderDragging = false;
        CenterGuide.Visibility = Visibility.Collapsed;
        if (_ownsTip && _sliderTip != null) _sliderTip.IsOpen = false;
        var offsetMs = OffsetFromSlider();
        ProgressSlider.Value = 50; // 松手回归置中
        if (Math.Abs(offsetMs) >= 1)
            SourceBox.Text = ShiftLyricsTimestamps(SourceBox.Text, offsetMs);
    }

    private double OffsetFromSlider() => (ProgressSlider.Value - 50) / 50 * 5000.0;

    // 拖动中：气泡显示偏移量；把「平移后覆盖基准播放位置」的歌词行定位到滚动歌词框居中并高亮。
    // 不控制播放进度（波形/播放位置不动），松手后歌词框恢复跟随播放
    private void OnJogValueChanged()
    {
        UpdateSliderTip();
        if (_overlayLines.Count == 0) return;

        var offsetMs = OffsetFromSlider();
        var idx = -1;
        for (var i = 0; i < _overlayLines.Count; i++)
        {
            if (_overlayLines[i].Time.TotalMilliseconds + offsetMs <= _jogStartMs) idx = i;
        }
        if (idx < 0) idx = 0;
        if (idx == _lastJogLine) return;
        _lastJogLine = idx;

        OverlayLyricsView.SetPosition(_overlayLines[idx].Time); // 高亮该行并触发居中滚动
    }

    // 气泡（Slider 模板经 ToolTipService 挂在 Thumb 上的 ToolTip，不在视觉树）显示格式化偏移量；
    // 模板拿不到内部 ToolTip 时自建一个手动控制开关
    private void UpdateSliderTip()
    {
        if (_sliderTip == null)
        {
            var thumb = FindDescendant<Thumb>(ProgressSlider);
            if (thumb == null) return;
            if (ToolTipService.GetToolTip(thumb) is ToolTip tip)
            {
                _sliderTip = tip;
            }
            else
            {
                _sliderTip = new ToolTip();
                ToolTipService.SetToolTip(thumb, _sliderTip);
                _ownsTip = true;
                _sliderTip.IsOpen = true;
            }
        }
        var offsetMs = OffsetFromSlider();
        var text = $"{(offsetMs >= 0 ? "+" : string.Empty)}{offsetMs:F0}ms";
        // Slider 内部拖动时会把气泡重写为原始数值，TryEnqueue 保证我们的文本在其后执行
        DispatcherQueue.TryEnqueue(() => { if (_sliderTip != null) _sliderTip.Content = text; });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            var result = FindDescendant<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // 歌词全部时间戳平移 offsetMs（正值向歌曲终点方向）：
    // 支持 [mm:ss.xx]/<mm:ss.xx> 时钟标签（整体平移）与
    // [start,dur...]/<start,dur...>/(start[,dur...]) 毫秒标记（仅平移起始，时长等其余字段不动）
    private static string ShiftLyricsTimestamps(string lyrics, double offsetMs)
    {
        // 1) 时钟标签（行级 [..] 与字级 <..>）
        lyrics = Regex.Replace(lyrics, @"([\[<])(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?([\]>])", m =>
        {
            var ms = int.Parse(m.Groups[2].Value) * 60000 + int.Parse(m.Groups[3].Value) * 1000
                     + FracToMs(m.Groups[4].Value) + offsetMs;
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{m.Groups[1].Value}{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}{m.Groups[5].Value}";
        });

        // 2) 毫秒起始标记：首字段平移，其余字段（时长等）原样保留
        lyrics = Regex.Replace(lyrics, @"\\\[(\d+)((?:,\d+)*)\]", m => ShiftMsTag(m, offsetMs, "[", "]"));
        lyrics = Regex.Replace(lyrics, @"<(\d+)((?:,\d+)*)>", m => ShiftMsTag(m, offsetMs, "<", ">"));
        lyrics = Regex.Replace(lyrics, @"\((\d+)((?:,\d+)*)\)", m => ShiftMsTag(m, offsetMs, "(", ")"));
        return lyrics;
    }

    private static string ShiftMsTag(Match m, double offsetMs, string open, string close)
    {
        var start = Math.Max(0, (long)Math.Round(double.Parse(m.Groups[1].Value) + offsetMs));
        return $"{open}{start}{m.Groups[2].Value}{close}";
    }

    private static double FracToMs(string frac) => frac.Length switch
    {
        1 => int.Parse(frac) * 100.0,
        2 => int.Parse(frac) * 10.0,
        3 => int.Parse(frac),
        _ => 0,
    };

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ===== 空格/回车快捷键 → 打词（窗口根隧道监听，悬浮层打开期间全界面生效） =====
    // 抢在焦点按钮的类处理之前拦截：除焦点在文本框（正常打字）外一律触发打词按钮
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Visibility != Visibility.Visible || _closing) return;
        if (e.Key != Windows.System.VirtualKey.Space && e.Key != Windows.System.VirtualKey.Enter) return;
        if (FocusManager.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        DoLyricWord();
    }

    // 底部按钮：打词（空格/回车同入口）
    private void OnLyricWordClick(object sender, RoutedEventArgs e) => DoLyricWord();

    // ===== 打词：左侧歌词剥离时间标签得到待打字符，每次触发写入下一个字并打上触发时刻时间戳 =====
    // 行首字：[逐行时间]<逐字时间>字；行内字：<逐字时间>字；左侧歌词的原时间戳全部忽略
    private List<char[]> _wordLines = new(); // 左侧歌词剥离标签后的每行待打字符
    private int _wordLine;                   // 当前行索引
    private int _wordChar;                   // 行内字符索引

    private void DoLyricWord()
    {
        if (_wordLines.Count == 0)
        {
            _wordLines = BuildWordLines(SourceBox.Text);
            _wordLine = 0;
            _wordChar = 0;
        }

        // 跳过已打完的行（含空行），定位下一个待打字符
        while (_wordLine < _wordLines.Count && _wordChar >= _wordLines[_wordLine].Length)
        {
            _wordLine++;
            _wordChar = 0;
        }
        if (_wordLine >= _wordLines.Count) return; // 全部打完

        var ch = _wordLines[_wordLine][_wordChar];
        var stamp = FormatStamp(_player.Position);
        var text = WorkBox.Text;

        // 行首字（_wordChar == 0）先写逐行时间戳再写逐字时间戳，并另起一行
        var token = _wordChar == 0 ? $"[{stamp}]<{stamp}>{ch}" : $"<{stamp}>{ch}";
        if (_wordChar == 0 && text.Length > 0 && !text.EndsWith("\n")) token = "\n" + token;

        WorkBox.Text = text + token;
        _wordChar++;
        _workScroll ??= FindDescendant<ScrollViewer>(WorkBox);
        _workScroll?.ChangeView(null, _workScroll.ScrollableHeight, null, true);
    }

    // 剥离左侧歌词所有 ASCII 括号标签（[...] <...> (...)），按行取非空白字符作为待打序列
    // LRC 多时间标签同行（[00:00.00]词[00:05.00]词）先按行级时钟标签拆分为独立行
    private static List<char[]> BuildWordLines(string source)
    {
        var lines = new List<char[]>();
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            foreach (var seg in Regex.Split(raw, @"(?=\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])"))
            {
                var stripped = Regex.Replace(seg, @"\[[^\]]*\]|<[^\]>]*>|\([^)]*\)", "");
                var chars = stripped.Where(c => !char.IsWhiteSpace(c)).ToArray();
                if (chars.Length > 0) lines.Add(chars);
            }
        }
        return lines;
    }

    // 时间戳样式（用户指定）：[01:10.301] 分钟 2 位补零、秒 2 位、毫秒 3 位
    private static string FormatStamp(TimeSpan t) =>
        $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

    // 底部按钮：写入——右侧文本框有内容写右侧，否则写左侧；
    // 写入后走统一后处理（OnWriteCompletedAsync：移动文件+标绿+排序）并关闭悬浮层
    private bool _writing;

    private async void OnWriteLyricsClick(object sender, RoutedEventArgs e)
    {
        if (_writing) return;
        var lyrics = WorkBox.Text.Length > 0 ? WorkBox.Text : SourceBox.Text;
        if (lyrics.Length == 0 || lyrics == MainWindow.NoLyricsPlaceholder)
        {
            await ShowTipAsync("没有可写入的歌词：右侧文本框为空，左侧也没有歌词内容。");
            return;
        }

        var file = SharedPlayer.Instance.CurrentFile;
        if (string.IsNullOrEmpty(file))
        {
            await ShowTipAsync("没有当前歌曲。");
            return;
        }

        _writing = true;
        WriteLyricsButton.IsEnabled = false;

        // 播放占用音频文件，先停止再写入，写完恢复
        var wasPlaying = _player.State == NAudio.Wave.PlaybackState.Playing;
        var resumePos = wasPlaying ? _player.Position : TimeSpan.Zero;
        if (wasPlaying) _player.Stop();

        try
        {
            await Task.Run(() => MainWindow.WriteLyricsOnly(file, lyrics));
            // 统一后处理：移动到保存路径/已处理 + 状态记录 + 列表排序（_item.File 更新为新路径）
            var (_, _, newFile) = await _owner.OnWriteCompletedAsync(_item, ReplaceState.Ok, "已写入");
            if (wasPlaying)
            {
                try
                {
                    _player.Load(newFile);
                    _player.Seek(resumePos);
                    _player.Play();
                }
                catch { }
            }
            Close();
        }
        catch (Exception ex)
        {
            await ShowTipAsync($"写入失败：{ex.Message}");
        }
        finally
        {
            _writing = false;
            WriteLyricsButton.IsEnabled = true;
        }
    }

    private async Task ShowTipAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = msg,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
