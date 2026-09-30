using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LyricsApp.Web;
using LyricsApp.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LyricsApp.WinUI.Views;

/// <summary>悬浮层确认结果：WriteTags=true 时同时写入网络候选的歌名/歌手/专辑/封面。</summary>
public sealed record MatchResult(string Lyrics, bool WriteTags, string Title, string Artist, string Album, string CoverUrl);

/// <summary>
/// 匹配歌词悬浮层：宿主于主窗口内（遮罩 + 居中面板），带显示/退出动画。
/// 四平台并发搜索，确认后由 MainWindow 写入歌词。
/// </summary>
public sealed partial class MatchOverlay : UserControl
{
    private readonly LocalSong _song;
    private readonly ILyricsProvider[] _providers;
    private readonly List<SongCandidate>?[] _results;
    private readonly string[] _searchErrors;
    private readonly bool[] _searching; // 各平台搜索是否进行中（流式加入时区分"正在搜索"与"没有结果"）
    private readonly ObservableCollection<SongCandidateView> _views = new(); // 候选视图（流式增量加入）
    private SemaphoreSlim _prefetchSem = new(4); // 取词预取限流（每次搜索重置）
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<MatchResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, ImageSource?> _coverCache = new();

    private SongCandidate? _selected;
    private int _activeTab;
    private bool _closing;
    private MatchResult? _result;

    // 本地试听：控制与主界面相同的共享播放流（两边状态由定时器轮询保持同步）
    private readonly PlayerEngine _player = SharedPlayer.Instance.Engine;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _playTimer;
    private readonly string? _audioFile;
    private bool _sliderDragging;

    /// <summary>确认结果（WriteTags 标识是否连标签一起写入）；取消/关闭 → null。</summary>
    public Task<MatchResult?> Completion => _completion.Task;

    /// <summary>封面已写入（参数=音频文件路径），宿主刷新封面显示。</summary>
    public event Action<string>? CoverWritten;

    /// <summary>悬浮层驱动共享播放器进入播放状态时触发（参数为音频文件路径），供宿主同步播放上下文。</summary>
    public event Action<string>? PlaybackStarted;

    public MatchOverlay(LocalSong song, string audioFile)
    {
        _song = song;
        _audioFile = audioFile;
        _providers = new ILyricsProvider[] { new NetEaseProvider(), new LrclibProvider(), new QQMusicProvider(), new KugouProvider() };
        _results = new List<SongCandidate>?[_providers.Length];
        _searchErrors = new string[_providers.Length];
        _searching = new bool[_providers.Length];
        InitializeComponent();
        CandidateList.ItemsSource = _views;

        // 面板尺寸跟随主窗口：长宽 = 悬浮层容器（即主窗口客户区）的 3/4
        SizeChanged += (s, e) =>
        {
            PanelBorder.Width = e.NewSize.Width * 3.0 / 4.0;
            PanelBorder.Height = e.NewSize.Height * 3.0 / 4.0;
        };

        SubtitleLabel.Text = _song.Title;
        FileLabel.Text = $" 文件名: {_song.FileName}";
        SearchBox.Text = BuildDefaultKeyword();

        // 试听播放器 UI 刷新（轮询共享播放流，与主界面状态互相同步）
        _playTimer = DispatcherQueue.CreateTimer();
        _playTimer.Interval = TimeSpan.FromMilliseconds(100);
        _playTimer.Tick += (s, e) =>
        {
            var dur = _player.Duration;
            if (dur.TotalMilliseconds > 0 && !_sliderDragging)
                PlaySlider.Value = Math.Clamp(_player.Position.TotalMilliseconds / dur.TotalMilliseconds * 1000, 0, 1000);
            // 歌词预览渲染驱动：逐字 → KTV 画布染色；逐行 → 行高亮滚动
            if (_previewIsCharLevel)
                KaraokeCanvas.SetPosition(_player.Position);
            else
                UpdatePreviewHighlight(_player.Position);
        };
        _playTimer.Start();

        // Slider 内部 Thumb 会把指针事件标记为已处理，必须用 handledEventsToo 捕获
        PlaySlider.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPlaySliderPointerPressed), true);
        PlaySlider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPlaySliderPointerReleased), true);
        PlaySlider.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPlaySliderPointerReleased), true);

        // 播放状态变化（含主界面触发）→ 同步本悬浮层的播放按钮标题
        _player.StateChanged += OnEngineStateChanged;

        // 逐字渲染画布：行变化时滚动居中，并把可视区域同步给画布
        KaraokeCanvas.CurrentLineChanged += idx =>
        {
            if (idx < 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                // 当前行展开翻译/发音子行时行高动态变化，用 GetLineTop 取实际坐标
                var target = Math.Max(0, KaraokeCanvas.GetLineTop(idx) - (KaraokeScroll.ViewportHeight - LyricsCanvas.RowHeight) / 2);
                KaraokeScroll.ChangeView(null, target, null);
            });
        };
        KaraokeScroll.ViewChanged += (object? s, ScrollViewerViewChangedEventArgs e) =>
        {
            KaraokeCanvas.SetViewport((float)KaraokeScroll.VerticalOffset, (float)KaraokeScroll.ViewportHeight);
        };

        StartSearch();
    }

    // ===== 显示 / 关闭（带动画） =====
    public void Show()
    {
        Visibility = Visibility.Visible;
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
        _cts.Cancel();
        _playTimer.Stop();
        _player.StateChanged -= OnEngineStateChanged;
        await PlayHideAnimationAsync();
        _completion.TrySetResult(_result);
        Visibility = Visibility.Collapsed;
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

    // ===== 本地试听 =====
    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_audioFile == null) return;
        try
        {
            // 首次播放或主界面换了歌：切到本悬浮层对应的音频
            if (SharedPlayer.Instance.CurrentFile != _audioFile || _player.Duration == TimeSpan.Zero)
                SharedPlayer.Instance.LoadFile(_audioFile);
            _player.Toggle();
            if (_player.State == NAudio.Wave.PlaybackState.Playing)
                PlaybackStarted?.Invoke(_audioFile);
        }
        catch
        {
            PlayButton.IsEnabled = false;
        }
    }

    private void OnPlaySliderPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _sliderDragging = true;
        // WinUI Slider 点击轨道默认不跳转，手动把点击位置换算为进度
        if (_player.Duration.TotalMilliseconds <= 0) return;
        var pt = e.GetCurrentPoint(PlaySlider).Position;
        if (PlaySlider.ActualWidth <= 0) return;
        var ratio = Math.Clamp(pt.X / PlaySlider.ActualWidth, 0, 1);
        PlaySlider.Value = ratio * PlaySlider.Maximum;
    }

    private void OnPlaySliderPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _sliderDragging = false;
        ResumeIfPaused();
    }

    private void OnPlaySliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_sliderDragging) return;
        if (_player.Duration.TotalMilliseconds <= 0) return;
        _player.Seek(TimeSpan.FromMilliseconds(e.NewValue / 1000.0 * _player.Duration.TotalMilliseconds));
    }

    private void ResumeIfPaused()
    {
        if (_player.State == NAudio.Wave.PlaybackState.Paused)
        {
            _player.Play();
            MainWindow.SyncPlayButton(PlayButton, _player);
        }
    }

    private void OnEngineStateChanged() =>
        DispatcherQueue.TryEnqueue(() => MainWindow.SyncPlayButton(PlayButton, _player));

    private string BuildDefaultKeyword()
    {
        if (_song.Artist.Length > 0) return $"{_song.Artist.Split(" / ")[0]} - {_song.Title}";
        return _song.Title.Length > 0 ? _song.Title : Path.GetFileNameWithoutExtension(_song.FileName);
    }

    // ===== 搜索 =====
    private void OnSearchClick(object sender, RoutedEventArgs e) => StartSearch();
    private void OnSearchBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) StartSearch();
    }

    private void StartSearch()
    {
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0) return;
        var token = _cts.Token;
        _prefetchSem = new SemaphoreSlim(4);

        for (var i = 0; i < _providers.Length; i++)
        {
            _results[i] = new List<SongCandidate>();
            _searchErrors[i] = "";
            _searching[i] = true;
        }
        _selected = null;
        _views.Clear();
        ListStatusLabel.Text = "正在搜索…";
        ListStatusLabel.Visibility = Visibility.Visible;

        for (var i = 0; i < _providers.Length; i++)
        {
            var idx = i;
            var provider = _providers[i];
            // 流式：每解析出一个候选立即切 UI 线程加入列表（仅当前显示的平台）
            var onItem = new Action<SongCandidate>(cand => _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing || idx != _activeTab) return;
                AddCandidateView(cand);
            }));
            _ = Task.Run(async () =>
            {
                try
                {
                    _results[idx] = await provider.SearchAsync(keyword, _song, token, onItem);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _results[idx] = new List<SongCandidate>();
                    _searchErrors[idx] = ex.Message;
                }
                _searching[idx] = false;
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (idx != _activeTab) return;
                    // 流式中途切过平台导致显示被清空时，用完整结果补齐
                    if (_views.Count == 0 && _results[idx] is { Count: > 0 } res)
                        foreach (var c in res) AddCandidateView(c);
                    RefreshStatusLabel();
                });
            }, token);
        }
    }

    private void OnPlatformChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_results == null) return;
        _activeTab = PlatformBar.Items.IndexOf(PlatformBar.SelectedItem);
        RefreshCandidateList();
    }

    // 切换平台时全量重建候选视图（每个候选内部有歌词缓存判断，不重复取词）
    private void RefreshCandidateList()
    {
        _views.Clear();
        if (_results[_activeTab] is { Count: > 0 } res)
            foreach (var c in res) AddCandidateView(c);
        RefreshStatusLabel();
    }

    // 状态标签：搜索中 / 失败 / 没有结果 / 有候选时隐藏
    private void RefreshStatusLabel()
    {
        var res = _results[_activeTab];
        if (_views.Count > 0 || (res != null && res.Count > 0))
        {
            ListStatusLabel.Visibility = Visibility.Collapsed;
            return;
        }
        ListStatusLabel.Text = _searching[_activeTab] ? "正在搜索…"
            : _searchErrors[_activeTab].Length > 0 ? $"搜索失败: {_searchErrors[_activeTab]}"
            : "没有找到匹配的歌曲";
        ListStatusLabel.Visibility = Visibility.Visible;
    }

    // 候选加入列表（流式）：加入即预取歌词打类型标签；第一个候选自动选中预览
    private void AddCandidateView(SongCandidate cand)
    {
        var view = new SongCandidateView(cand, GetCover);
        _views.Add(view);
        if (_views.Count == 1) CandidateList.SelectedIndex = 0;
        ListStatusLabel.Visibility = Visibility.Collapsed;
        _ = PrefetchOneAsync(cand, view);
    }

    // 单候选取词预取（共享 4 并发限流），取到后解析类型并在列表打标签
    private async Task PrefetchOneAsync(SongCandidate cand, SongCandidateView view)
    {
        var provider = _providers.FirstOrDefault(p => p.Name == cand.Source);
        if (provider == null) return;
        try
        {
            await _prefetchSem.WaitAsync(_cts.Token);
            try
            {
                if (cand.LyricsText == null)
                    cand.LyricsText = await provider.GetLyricsAsync(cand, _cts.Token);
            }
            finally { _prefetchSem.Release(); }
            if (cand.LyricsText == null) return;
            var isCharLevel = LrcParser.Parse(cand.LyricsText).Any(l => l.Chars.Count > 1);
            DispatcherQueue.TryEnqueue(() => view.SetLyricsType(isCharLevel));
        }
        catch (OperationCanceledException) { }
        catch { /* 单个候选取词失败则不打标签 */ }
    }

    // ===== 选中候选 =====
    private async void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not SongCandidateView view) return;
        var cand = view.Candidate;
        _selected = cand;

        CoverImage.Source = GetCover(cand);
        DetailTitle.Text = cand.Title;
        DetailArtist.Text = cand.Artists;
        DetailAlbum.Text = cand.Album;
        SourceBadge.Text = $"歌词  {cand.Source}";
        SourceBadge.Visibility = Visibility.Visible;
        RenderPreview(null, "");

        if (cand.LyricsText == null)
        {
            try
            {
                var provider = _providers.First(p => p.Name == cand.Source);
                cand.LyricsText = await provider.GetLyricsAsync(cand, _cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { cand.LyricsText = null; }
            if (_selected != cand) return;
        }

        RenderPreview(cand.LyricsText, cand.LyricType);

        // 取词完成后把类型徽章同步到左侧候选列表
        if (cand.LyricsText != null)
        {
            var isCharLevel = LrcParser.Parse(cand.LyricsText).Any(l => l.Chars.Count > 1);
            view.SetLyricsType(isCharLevel);
        }
    }

    // 歌词预览：逐字歌词用 KTV 逐字染色画布，逐行歌词用行高亮列表；跟随播放进度滚动
    private List<LrcLine>? _previewLrc;
    private List<Border>? _previewRows;
    private int _previewCurrent = -1;
    private bool _previewIsCharLevel;

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(a, r, g, b));

    private void RenderPreview(string? lyrics, string typeLabel)
    {
        PreviewPanel.Children.Clear();
        _previewRows = null;
        _previewLrc = null;
        _previewCurrent = -1;

        if (lyrics == null)
        {
            _previewIsCharLevel = false;
            KaraokeHost.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Visible;
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "取词失败",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        var lines = LrcParser.Parse(lyrics);
        if (lines.Count == 0)
        {
            // 非时间轴文本：逐行纯文本兜底
            _previewIsCharLevel = false;
            KaraokeHost.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Visible;
            foreach (var t in lyrics.Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0))
                PreviewPanel.Children.Add(new TextBlock { Text = t, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            return;
        }

        var isCharLevel = lines.Any(l => l.Chars.Count > 1);
        _previewIsCharLevel = isCharLevel;

        if (isCharLevel)
        {
            // 逐字歌词：KTV 逐字染色渲染
            PreviewScroll.Visibility = Visibility.Collapsed;
            KaraokeHost.Visibility = Visibility.Visible;
            KaraokeCanvas.SetData(lines);
            return;
        }

        // 逐行歌词：行高亮列表渲染
        KaraokeHost.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Visible;
        _previewLrc = lines;
        _previewRows = new List<Border>(lines.Count);
        foreach (var line in lines)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Text = line.Text,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            PreviewPanel.Children.Add(row);
            _previewRows.Add(row);
        }
    }

    // 播放进度驱动：当前行高亮并滚动到可视中部（逐字/逐行匹配）
    private void UpdatePreviewHighlight(TimeSpan pos)
    {
        if (_previewLrc == null || _previewRows == null || _previewRows.Count != _previewLrc.Count) return;

        int lo = 0, hi = _previewLrc.Count - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_previewLrc[mid].Time <= pos) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (idx == _previewCurrent) return;
        _previewCurrent = idx;

        for (var i = 0; i < _previewRows.Count; i++)
        {
            _previewRows[i].Background = i == idx ? Brush(45, 47, 111, 237) : null;
        }

        if (idx >= 0 && idx < _previewRows.Count)
        {
            var row = _previewRows[idx];
            // 以内容根(PreviewPanel)为基准取内容坐标；相对 ScrollViewer 的坐标含滚动偏移会导致定位错乱
            var offsetY = row.TransformToVisual(PreviewPanel).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            PreviewScroll.ChangeView(null, offsetY - PreviewScroll.ViewportHeight / 2 + row.ActualHeight / 2, null, true);
        }
    }

    // ===== 封面 =====
    private ImageSource? GetCover(SongCandidate cand)
    {
        if (cand.CoverUrl.Length == 0) return null;
        if (_coverCache.TryGetValue(cand.CoverUrl, out var img)) return img;
        _coverCache[cand.CoverUrl] = null;
        var uri = new Uri(cand.CoverUrl);
        var bitmap = new BitmapImage(uri);
        bitmap.ImageOpened += (s, e) =>
        {
            _coverCache[cand.CoverUrl] = bitmap;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_selected != null && _selected.CoverUrl == cand.CoverUrl)
                    CoverImage.Source = bitmap;
            });
        };
        _coverCache[cand.CoverUrl] = bitmap;
        return bitmap;
    }

    // ===== 按钮 =====
    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(null, false);

    private void OnWriteLyricsClick(object sender, RoutedEventArgs e)
    {
        if (_selected?.LyricsText == null)
        {
            WarnNoLyrics();
            return;
        }
        Finish(_selected.LyricsText, false);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_selected?.LyricsText == null)
        {
            WarnNoLyrics();
            return;
        }
        Finish(_selected.LyricsText, true);
    }

    // 写入封面：把当前选中候选的网络封面写入当前歌曲内置封面，
    // 不参与写入信息成功事件（不排序、不标绿、不移动文件），写完通知宿主刷新封面显示
    private async void OnWriteCoverClick(object sender, RoutedEventArgs e)
    {
        var url = _selected?.CoverUrl;
        if (url is not { Length: > 0 })
        {
            var warn = new ContentDialog
            {
                Title = "提示",
                Content = "请先选择一个有封面的候选。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await warn.ShowAsync();
            return;
        }

        WriteCoverButton.IsEnabled = false;

        // 试听占用了音频文件时先停止，写完恢复
        var wasPlaying = SharedPlayer.Instance.CurrentFile == _audioFile
            && _player.State == NAudio.Wave.PlaybackState.Playing;
        var pos = wasPlaying ? _player.Position : TimeSpan.Zero;
        if (wasPlaying) _player.Stop();

        try
        {
            var data = await MainWindow.CoverHttp.GetByteArrayAsync(url);
            if (data.Length == 0) throw new InvalidOperationException("封面下载为空。");
            var file = _audioFile!;
            await Task.Run(() =>
            {
                using var tf = TagLib.File.Create(file);
                var mime = url.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                tf.Tag.Pictures = new[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(data))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = mime,
                        Description = "cover",
                    },
                };
                tf.Save();
            });
            CoverWritten?.Invoke(file);

            var ok = new ContentDialog
            {
                Title = "完成",
                Content = "封面已写入。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await ok.ShowAsync();
        }
        catch (Exception ex)
        {
            var fail = new ContentDialog
            {
                Title = "写入失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await fail.ShowAsync();
        }
        finally
        {
            if (wasPlaying)
            {
                try
                {
                    _player.Load(_audioFile!);
                    _player.Seek(pos);
                    _player.Play();
                }
                catch { }
            }
            WriteCoverButton.IsEnabled = true;
        }
    }

    private async void WarnNoLyrics()
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = "请先选择一个有歌词的候选。",
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private void Finish(string? lyrics, bool writeTags)
    {
        if (_closing) return;
        _result = lyrics == null
            ? null
            : new MatchResult(lyrics, writeTags, _selected?.Title ?? "", _selected?.Artists ?? "", _selected?.Album ?? "", _selected?.CoverUrl ?? "");
        Close();
    }
}

/// <summary>候选列表视图模型。</summary>
public sealed class SongCandidateView : INotifyPropertyChanged
{
    public SongCandidate Candidate { get; }
    private readonly Func<SongCandidate, ImageSource?> _coverLoader;

    public SongCandidateView(SongCandidate candidate, Func<SongCandidate, ImageSource?> coverLoader)
    {
        Candidate = candidate;
        _coverLoader = coverLoader;
    }

    public string Title => Candidate.Title;
    public string ScoreText => $"{(int)Math.Round(Candidate.Score)}%";
    public Visibility DurationMatchVisibility => Candidate.DurationMatch ? Visibility.Visible : Visibility.Collapsed;
    public string Subtitle
    {
        get
        {
            var sub = $"{Candidate.Artists}{(Candidate.Artists.Length > 0 && Candidate.Album.Length > 0 ? " · " : "")}{Candidate.Album}";
            if (sub.Length == 0)
                sub = $"{Candidate.Source} · {Candidate.Duration:mm\\:ss}";
            return sub;
        }
    }

    public ImageSource? Cover => _coverLoader(Candidate);

    // 歌词类型徽章（取词后设置）：逐字 = 浅绿，逐行 = 浅橙
    private string _typeText = "";
    private Brush _typeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public string TypeText
    {
        get => _typeText;
        private set { _typeText = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeVisibility)); }
    }

    public Brush TypeBrush
    {
        get => _typeBrush;
        private set { _typeBrush = value; OnPropertyChanged(); }
    }

    public Visibility TypeVisibility => string.IsNullOrEmpty(_typeText) ? Visibility.Collapsed : Visibility.Visible;

    public void SetLyricsType(bool charLevel)
    {
        TypeBrush = charLevel
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 134, 214, 141))  // 浅绿
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 166, 90));  // 浅橙
        TypeText = charLevel ? "逐字" : "逐行";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
