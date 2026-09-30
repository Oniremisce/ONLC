using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using LyricsApp;
using LyricsApp.WinUI.Controls;
using LyricsApp.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace LyricsApp.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private static readonly string[] MusicExtensions =
        { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma", ".ape", ".opus" };

    internal const string NoLyricsPlaceholder = "（该歌曲没有内嵌歌词）";

    private readonly ObservableCollection<SongItem> _songs = new();

    // 全部替换处理状态（key=文件路径），软件运行期间跨悬浮面板开关保留
    private readonly Dictionary<string, (ReplaceState State, string StatusText)> _replaceStates = new();
    // 应用级共享播放器：匹配歌词窗口控制的是同一播放流
    private readonly PlayerEngine _player = SharedPlayer.Instance.Engine;
    private readonly DispatcherQueueTimer _uiTimer;
    private List<LrcLine> _lrcLines = new();
    private int _playingIndex = -1;
    private SongItem? _playingItem;
    private bool _sliderDragging;
    // 写入后文件的保存路径（本次运行有效）：设置了则移动到该目录，未设置移动到 原目录/已处理
    private string? _savePath;

    public MainWindow()
    {
        InitializeComponent();

        Title = "逐字歌词";
        // 窗口/任务栏图标（EXE 图标由 csproj ApplicationIcon 设置，此处为标题栏小图标）
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "icon.ico"));
        // 默认窗口尺寸 1110×728 的 4/3
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 971));
        ClampToWorkArea();
        CenterOnScreen();
        SongList.ItemsSource = _songs;

        // 播放器 UI 定时器
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _uiTimer.Tick += OnPlayerTimerTick;
        _uiTimer.Start();
        _player.PlaybackEnded += () => DispatcherQueue.TryEnqueue(() =>
        {
            // 播放完毕默认停止（不自动下一曲）
            TrackSlider.Value = 0;
        });
        // 播放状态变化（来自本窗口或悬浮层）→ 同步播放按钮标题
        _player.StateChanged += () => DispatcherQueue.TryEnqueue(() => SyncPlayButton(PlayPauseButton, _player));

        // 歌词滚动：当前行变化时把该行滚到可视中部
        LyricsView.CurrentLineChanged += idx =>
        {
            if (idx < 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                // 当前行展开翻译/发音子行时行高动态变化，用 GetLineTop 取实际坐标
                var target = Math.Max(0, LyricsView.GetLineTop(idx) - (LyricsScroll.ViewportHeight - LyricsCanvas.RowHeight) / 2);
                LyricsScroll.ChangeView(null, target, null);
            });
        };
        LyricsScroll.ViewChanged += (object? s, ScrollViewerViewChangedEventArgs e) =>
        {
            LyricsView.SetViewport((float)LyricsScroll.VerticalOffset, (float)LyricsScroll.ViewportHeight);
        };

        // 点击歌词行：从该句开始播放（暂停中则恢复播放）
        LyricsView.LineClicked += idx =>
        {
            if (_playingIndex < 0 || idx < 0 || idx >= _lrcLines.Count) return;
            _player.Seek(_lrcLines[idx].Time);
            LyricsView.SetPosition(_lrcLines[idx].Time);
            ResumeIfPaused();
        };

        // Slider 内部 Thumb 会把指针事件标记为已处理，必须用 handledEventsToo 捕获，否则拖动状态失效
        TrackSlider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSliderPointerPressed), true);
        TrackSlider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSliderPointerReleased), true);
        TrackSlider.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSliderPointerReleased), true);

        Closed += (s, e) => _uiTimer.Stop(); // 共享播放器随进程生命周期结束，不在此销毁
    }

    // 播放状态 → 播放/暂停按钮标题（主界面与悬浮层统一调用此方法同步，避免逻辑重复）
    public static void SyncPlayButton(Button button, PlayerEngine engine)
    {
        var title = engine.State == NAudio.Wave.PlaybackState.Playing ? "暂停" : "播放";
        if ((button.Content as string) == title) return;
        button.Content = title;
    }

    // 点击进度条/歌词后，暂停状态恢复播放
    private void ResumeIfPaused()
    {
        if (_playingIndex >= 0 && _player.State == NAudio.Wave.PlaybackState.Paused)
            _player.Play(); // 按钮标题由 StateChanged 事件统一同步
    }

    // 窗口超屏幕时收紧到工作区，再居中（WinForms 版默认行为）
    private void ClampToWorkArea()
    {
        var area = DisplayArea.Primary;
        var w = Math.Min(AppWindow.Size.Width, area.WorkArea.Width);
        var h = Math.Min(AppWindow.Size.Height, area.WorkArea.Height);
        if (w != AppWindow.Size.Width || h != AppWindow.Size.Height)
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.Primary;
        var w = AppWindow.Size.Width;
        var h = AppWindow.Size.Height;
        var x = area.WorkArea.X + (area.WorkArea.Width - w) / 2;
        var y = area.WorkArea.Y + (area.WorkArea.Height - h) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    // ===== 读取文件夹 =====
    private async void OnLoadFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        LoadFolderButton.IsEnabled = false;
        try
        {
            var files = Directory.EnumerateFiles(folder.Path)
                .Where(f => MusicExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = await Task.Run(() => BuildListItems(files));
            _songs.Clear();
            foreach (var it in items) _songs.Add(it);
            ResetStatusBar();
            ClearEditFields();
            if (items.Count == 0)
                await ShowDialogAsync("提示", "所选文件夹中没有找到常见歌曲文件。");
        }
        finally
        {
            LoadFolderButton.IsEnabled = true;
        }
    }

    // ===== 列表拖拽：文件夹=读取文件夹逻辑；音乐文件=追加到当前列表 =====
    private void OnSongListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnSongListDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();

        var files = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFolder folder)
            {
                // 文件夹：与读取文件夹一致，枚举其中音乐文件
                files.AddRange(Directory.EnumerateFiles(folder.Path)
                    .Where(f => MusicExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (item is StorageFile file &&
                     MusicExtensions.Contains(Path.GetExtension(file.Path).ToLowerInvariant()))
            {
                files.Add(file.Path);
            }
        }
        if (files.Count == 0) return;

        var newItems = await Task.Run(() => BuildListItems(files));
        var no = _songs.Count;
        foreach (var it in newItems)
        {
            it.No = (++no).ToString(); // 追加进现有列表，序号接续
            _songs.Add(it);
        }
    }

    private static List<SongItem> BuildListItems(List<string> files)
    {
        var items = new List<SongItem>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var title = Path.GetFileNameWithoutExtension(file);
            var artist = "";
            try
            {
                using var tagFile = TagLib.File.Create(file);
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                    title = tagFile.Tag.Title;
                artist = string.Join(", ", tagFile.Tag.Performers);
            }
            catch
            {
                // 无法解析标签的文件保持兜底值
            }
            items.Add(new SongItem { No = (i + 1).ToString(), Title = title, Artist = artist, File = file });
        }
        return items;
    }

    // ===== 选中歌曲：读取标签/歌词/时长 =====
    private async void OnSongSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongList.SelectedItem is not SongItem item) return;
        var file = item.File;

        StatusTitle.Text = $"歌名: {item.Title}";
        StatusArtist.Text = $"歌手: {item.Artist}";
        StatusFile.Text = $"文件: {Path.GetFileName(file)}";
        LyricsBox.Text = "正在读取歌词...";

        var info = await Task.Run(() => ReadSongInfoSafe(file));
        if (SongList.SelectedItem != item) return; // 快速切换时丢弃旧结果
        ApplySongInfo(item, info);
    }

    private sealed record SongInfo(string Lyrics, TimeSpan Duration, string Title, string Artist, string Album, int Bitrate);

    private static SongInfo ReadSongInfoSafe(string file)
    {
        try
        {
            using var tagFile = TagLib.File.Create(file);
            var tag = tagFile.Tag;
            return new SongInfo(
                tag.Lyrics ?? "",
                tagFile.Properties?.Duration ?? TimeSpan.Zero,
                tag.Title ?? "",
                string.Join(", ", tag.Performers),
                tag.Album ?? "",
                tagFile.Properties?.AudioBitrate ?? 0);
        }
        catch
        {
            return new SongInfo("", TimeSpan.Zero, "", "", "", 0);
        }
    }

    private void ApplySongInfo(SongItem item, SongInfo info)
    {
        var displayTitle = info.Title.Length > 0 ? info.Title : Path.GetFileNameWithoutExtension(item.File);
        item.Title = displayTitle;
        item.Artist = info.Artist;

        TitleBox.Text = info.Title;
        ArtistBox.Text = info.Artist;
        AlbumBox.Text = info.Album;
        BitrateBox.Text = info.Bitrate > 0 ? $"{info.Bitrate} kbps" : "-";
        LyricsBox.Text = info.Lyrics.Length > 0 ? NormalizeNewlines(info.Lyrics) : NoLyricsPlaceholder;

        StatusTitle.Text = $"歌名: {displayTitle}";
        StatusArtist.Text = $"歌手: {info.Artist}";
    }

    private void ClearEditFields()
    {
        TitleBox.Text = "";
        ArtistBox.Text = "";
        AlbumBox.Text = "";
        BitrateBox.Text = "";
        LyricsBox.Text = "";
        _lrcLines = new List<LrcLine>();
        LyricsView.SetData(_lrcLines);
    }

    // ===== 保存路径：设置后本次运行中所有写入完成的文件都移动到该目录 =====
    private async void OnSavePathClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _savePath = folder.Path;
        SavePathText.Text = _savePath;
    }

    private void ResetStatusBar()
    {
        StatusTitle.Text = "歌名: -";
        StatusArtist.Text = "歌手: -";
        StatusFile.Text = "文件: -";
    }

    // ===== 关于：弹出关于悬浮窗 =====
    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var overlay = new AboutOverlay();
        OverlayRoot.Children.Add(overlay);
        overlay.Show();
        await overlay.Completion;
        OverlayRoot.Children.Remove(overlay);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static string FormatDuration(TimeSpan duration) => duration >= TimeSpan.FromHours(1)
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"mm\:ss");

    // ===== 播放 =====
    private void OnSongDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SongList.SelectedItem is SongItem item)
            PlayAt(_songs.IndexOf(item));
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_playingIndex < 0)
        {
            if (SongList.SelectedItem is SongItem item)
                PlayAt(_songs.IndexOf(item));
            return;
        }
        _player.Toggle();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => PlayNeighbor(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => PlayNeighbor(+1);

    private void PlayNeighbor(int offset)
    {
        if (_songs.Count == 0 || _playingIndex < 0) return;
        PlayAt((_playingIndex + offset + _songs.Count) % _songs.Count);
    }

    private async void PlayAt(int index)
    {
        if (index < 0 || index >= _songs.Count) return;
        var item = _songs[index];

        try
        {
            // 相同文件不重复加载；双击当前曲目则从头重播
            SharedPlayer.Instance.LoadFile(item.File);
            _player.Seek(TimeSpan.Zero);
            _player.Play();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"无法播放该文件（可能缺少系统解码器）: {ex.Message}");
            return;
        }

        _playingIndex = index;
        _playingItem = item;
        PlayingLabel.Text = $"正在播放: {item.Title} - {item.Artist}";
        SongList.SelectedIndex = index;
        SongList.ScrollIntoView(item);

        var info = await Task.Run(() => ReadSongInfoSafe(item.File));
        var duration = info.Duration > TimeSpan.Zero ? info.Duration : _player.Duration;
        LoadPlayingLyrics(info.Lyrics, duration);
        await LoadCoverAsync(item.File);
    }

    // 读取歌曲内置封面显示到播放器封面区（无内置封面时清空）
    private async Task LoadCoverAsync(string? file)
    {
        var data = await Task.Run(() =>
        {
            if (file is not { Length: > 0 }) return null;
            try
            {
                using var tf = TagLib.File.Create(file);
                var pics = tf.Tag.Pictures;
                var pic = pics?.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                          ?? pics?.FirstOrDefault();
                return pic?.Data?.Data;
            }
            catch { return null; }
        });

        if (data is not { Length: > 0 })
        {
            CoverImage.Source = null;
            return;
        }
        try
        {
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(data);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            CoverImage.Source = bmp;
        }
        catch { CoverImage.Source = null; }
    }

    private void LoadPlayingLyrics(string lyrics, TimeSpan duration)
    {
        _lrcLines = LrcParser.Parse(lyrics);
        if (_lrcLines.Count == 0 && lyrics.Trim().Length > 0)
        {
            var lines = lyrics.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var per = duration.Ticks / Math.Max(1, lines.Length);
            _lrcLines = lines
                .Select((text, i) => new LrcLine(TimeSpan.FromTicks(per * i), TimeSpan.Zero,
                    new List<KaraokeChar> { new(text, TimeSpan.FromTicks(per * i), TimeSpan.Zero) }))
                .ToList();
        }
        ApplyCharLevelFallback(_lrcLines, duration);
        LyricsView.SetData(_lrcLines);
    }

    // 行级 LRC → 单字时间轴估算（KTV 逐字染色）
    private static void ApplyCharLevelFallback(List<LrcLine> lines, TimeSpan totalDuration)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineDuration = (i + 1 < lines.Count)
                ? lines[i + 1].Time - line.Time
                : totalDuration - line.Time;
            if (lineDuration <= TimeSpan.Zero || lineDuration.TotalSeconds > 8)
                lineDuration = TimeSpan.FromSeconds(3);

            if (line.Chars.Count == 1 && line.Chars[0].Duration == TimeSpan.Zero)
            {
                var text = line.Text;
                if (text.Length <= 1) continue;
                var per = lineDuration.Ticks / text.Length;
                lines[i] = new LrcLine(line.Time, lineDuration,
                    text.Select((c, k) => new KaraokeChar(
                        c.ToString(),
                        line.Time + TimeSpan.FromTicks(per * k),
                        TimeSpan.FromTicks(per))).ToList());
                continue;
            }

            if (line.Chars.Count > 1 && line.Chars.Any(c => c.Duration == TimeSpan.Zero))
            {
                var chars = line.Chars.ToList();
                for (var k = 0; k < chars.Count; k++)
                {
                    if (chars[k].Duration != TimeSpan.Zero) continue;
                    var end = (k + 1 < chars.Count) ? chars[k + 1].Time : line.Time + lineDuration;
                    var d = end - chars[k].Time;
                    chars[k] = chars[k] with { Duration = d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(300) };
                }
                lines[i] = line with { Duration = lineDuration, Chars = chars };
            }
        }
    }

    // ===== 播放器 UI 刷新 =====
    private void OnPlayerTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_playingIndex < 0 || _playingItem == null) return;

        var pos = _player.Position;
        var dur = _player.Duration;

        if (dur.TotalMilliseconds > 0 && !_sliderDragging)
        {
            TrackSlider.Value = Math.Clamp(pos.TotalMilliseconds / dur.TotalMilliseconds * 1000, 0, 1000);
        }
        TimeLabel.Text = $"{FormatDuration(pos)} / {FormatDuration(dur)}";
        LyricsView.SetPosition(pos);
    }

    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _sliderDragging = true;

        // WinUI Slider 点击轨道默认不跳转，手动把点击位置换算为进度（触发 ValueChanged → Seek）
        if (_playingIndex < 0 || _player.Duration.TotalMilliseconds <= 0) return;
        var pt = e.GetCurrentPoint(TrackSlider).Position;
        if (TrackSlider.ActualWidth <= 0) return;
        var ratio = Math.Clamp(pt.X / TrackSlider.ActualWidth, 0, 1);
        TrackSlider.Value = ratio * TrackSlider.Maximum;
    }

    private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _sliderDragging = false;
        ResumeIfPaused(); // 点击/拖动进度条后，暂停状态从当前位置恢复播放
    }

    private void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_sliderDragging) return;
        if (_playingIndex < 0 || _player.Duration.TotalMilliseconds <= 0) return;
        _player.Seek(TimeSpan.FromMilliseconds(e.NewValue / 1000.0 * _player.Duration.TotalMilliseconds));
    }

    // ===== 匹配歌词 =====
    private async void OnMatchLyricsClick(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not SongItem item)
        {
            await ShowDialogAsync("提示", "请先在列表中选择一首歌曲。");
            return;
        }
        var file = item.File;

        // 播放器正在播放其他歌曲时，切换到当前选中歌曲播放
        if (_playingIndex >= 0 && _playingItem != item)
            PlayAt(_songs.IndexOf(item));

        MatchLyricsButton.IsEnabled = false;
        try
        {
            var info = await Task.Run(() => ReadSongInfoSafe(file));
            var displayTitle = info.Title.Length > 0 ? info.Title : Path.GetFileNameWithoutExtension(file);
            var song = new Web.LocalSong(displayTitle, info.Artist, info.Album, info.Duration, Path.GetFileName(file));

            // 悬浮层方式打开匹配界面（主窗口内遮罩 + 居中面板，带开关动画）
            var overlay = new MatchOverlay(song, file);
            // 主界面未播放或播放的是别的歌时，悬浮层开始播放 → 同步主界面播放上下文
            overlay.PlaybackStarted += f =>
            {
                var item = _songs.FirstOrDefault(s => s.File == f);
                if (item != null) SyncPlayingContext(item);
            };
            // 悬浮层写入封面完成 → 刷新主界面播放器封面
            overlay.CoverWritten += f => DispatcherQueue.TryEnqueue(() => _ = LoadCoverAsync(f));
            OverlayRoot.Children.Add(overlay);
            overlay.Show();
            var result = await overlay.Completion;
            OverlayRoot.Children.Remove(overlay);

            if (result != null)
            {
                // 试听/播放中的文件被播放器占用，需先停止再写入，写完恢复
                var playingThis = SharedPlayer.Instance.CurrentFile == file;
                var wasPlaying = playingThis && _player.State == NAudio.Wave.PlaybackState.Playing;
                var resumePos = playingThis ? _player.Position : TimeSpan.Zero;
                if (playingThis) _player.Stop();

                try
                {
                    await Task.Run(() => WriteMatchResultAsync(file, result));

                    // 写入完成统一后处理：移动到「已处理」+ 状态记录 + 列表排序
                    var (finalState, finalStatus, newFile) = await OnWriteCompletedAsync(item, ReplaceState.Ok, "已写入");
                    file = newFile;

                    var fresh = await Task.Run(() => ReadSongInfoSafe(file));
                    if (SongList.SelectedItem == item)
                        ApplySongInfo(item, fresh);
                    await ShowDialogAsync("提示",
                        finalState == ReplaceState.Fail ? finalStatus
                        : result.WriteTags ? "歌词与标签已写入。" : "歌词已写入。");
                }
                catch (Exception ex)
                {
                    await ShowDialogAsync("写入失败", ex.Message);
                }
                finally
                {
                    if (playingThis)
                    {
                        try
                        {
                            _player.Load(file);
                            _player.Seek(resumePos);
                            if (wasPlaying) _player.Play();
                        }
                        catch { }
                    }
                }
            }
        }
        finally
        {
            MatchLyricsButton.IsEnabled = true;
        }
    }

    // ===== 手工打词 =====
    private async void OnManualLyricsClick(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not SongItem item)
        {
            await ShowDialogAsync("提示", "请先选择歌曲。");
            return;
        }

        ManualLyricsButton.IsEnabled = false;
        try
        {
            // 防御：确保进入悬浮层时处于常速（正常路径由悬浮层关闭时复位）
            SharedPlayer.Instance.Engine.SetSlowSpeed(false);
            // 播放器切换到当前选中歌曲，但不自动开始播放
            if (!string.Equals(SharedPlayer.Instance.CurrentFile, item.File, StringComparison.OrdinalIgnoreCase))
                SharedPlayer.Instance.LoadFile(item.File);
            SyncPlayingContext(item);

            var overlay = new ManualLyricsOverlay(this, item, LyricsBox.Text);
            OverlayRoot.Children.Add(overlay);
            overlay.Show();
            await overlay.Completion;
            OverlayRoot.Children.Remove(overlay);

            // 写入完成后刷新选中项歌词/信息显示（未写入时重新读取无副作用）
            var fresh = await Task.Run(() => ReadSongInfoSafe(item.File));
            if (SongList.SelectedItem == item)
                ApplySongInfo(item, fresh);
        }
        finally
        {
            ManualLyricsButton.IsEnabled = true;
        }
    }

    // ===== 全部替换 =====
    private async void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Count == 0)
        {
            await ShowDialogAsync("提示", "请先读取文件夹加载歌曲列表。");
            return;
        }

        ReplaceAllButton.IsEnabled = false;
        try
        {
            var overlay = new ReplaceOverlay(this, _songs.ToList(), _replaceStates);
            OverlayRoot.Children.Add(overlay);
            overlay.Show();
            var written = await overlay.Completion;
            OverlayRoot.Children.Remove(overlay);

            if (!written) return;

            // 有歌词写入：刷新选中项与正在播放歌曲的歌词显示
            if (SongList.SelectedItem is SongItem sel)
            {
                var fresh = await Task.Run(() => ReadSongInfoSafe(sel.File));
                if (SongList.SelectedItem == sel)
                    ApplySongInfo(sel, fresh);
            }
            if (_playingItem is SongItem play)
            {
                var info = await Task.Run(() => ReadSongInfoSafe(play.File));
                var dur = info.Duration > TimeSpan.Zero ? info.Duration : _player.Duration;
                LoadPlayingLyrics(NormalizeNewlines(info.Lyrics), dur);
            }
        }
        finally
        {
            ReplaceAllButton.IsEnabled = true;
        }
    }

    // 替换状态排序权重：失败 → 无匹配 → 待处理 → 已写入
    internal static int StateRank(ReplaceState state) => state switch
    {
        ReplaceState.Fail => 0,
        ReplaceState.NoMatch => 1,
        ReplaceState.Ok => 3,
        _ => 2, // Pending
    };

    // 全部替换/写入完成后，主界面列表按状态顺序排列（失败→无匹配→待处理→已写入，组内保持原顺序）
    internal void ReorderSongsByReplaceState()
    {
        if (_songs.Count < 2) return;
        var sel = SongList.SelectedItem;
        var ordered = _songs.OrderBy(s => _replaceStates.TryGetValue(s.File, out var st)
            ? StateRank(st.State)
            : StateRank(ReplaceState.Pending)).ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = _songs.IndexOf(ordered[target]);
            if (current != target) _songs.Move(current, target); // 就地移动，避免整表重建
        }
        if (SongList.SelectedItem != sel) SongList.SelectedItem = sel;
    }

    /// <summary>
    /// 写入完成统一后处理（所有写入行为的唯一出口，任意线程可调用）：
    /// 先移动文件到 原歌曲目录/已处理（没有则新建，已在其中则跳过），再记录运行期状态并排序列表。
    /// 移动失败时状态降级为 Fail。返回 (最终状态, 状态文本, 最终文件路径)。
    /// </summary>
    internal async Task<(ReplaceState State, string Status, string File)> OnWriteCompletedAsync(
        SongItem item, ReplaceState state, string status)
    {
        if (state == ReplaceState.Ok)
        {
            try
            {
                var newPath = await MoveToProcessedAsync(item.File);
                var moved = !string.Equals(newPath, item.File, StringComparison.OrdinalIgnoreCase);
                var oldFile = item.File;
                return await RunOnUiAsync(() =>
                {
                    if (moved)
                    {
                        _replaceStates.Remove(oldFile);
                        item.File = newPath;
                    }
                    item.Processed = true; // 主界面序号浅绿标记
                    _replaceStates[item.File] = (state, status);
                    ReorderSongsByReplaceState();
                    return (state, status, item.File);
                });
            }
            catch (Exception ex)
            {
                state = ReplaceState.Fail;
                status = $"已写入，但移动失败: {ex.Message}";
            }
        }
        return await RunOnUiAsync(() =>
        {
            _replaceStates[item.File] = (state, status);
            ReorderSongsByReplaceState();
            return (state, status, item.File);
        });
    }

    // 任意线程 → UI 线程执行委托并回传结果
    private Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            tcs.TrySetCanceled();
        return tcs.Task;
    }

    private async Task<string> MoveToProcessedAsync(string file)
    {
        // 设置了保存路径则统一移动到该目录，否则默认移动到 原歌曲目录/已处理
        var dir = !string.IsNullOrEmpty(_savePath)
            ? _savePath
            : Path.Combine(Path.GetDirectoryName(file) ?? ".", "已处理");
        var newPath = Path.Combine(dir, Path.GetFileName(file));
        if (string.Equals(newPath, file, StringComparison.OrdinalIgnoreCase))
            return file; // 已在目标目录内，无需移动
        Directory.CreateDirectory(dir);
        await Task.Run(() => File.Move(file, newPath, overwrite: true));
        return newPath;
    }

    /// <summary>
    /// 写入内嵌歌词：先清除 ID3v2 中所有旧 USLT 歌词帧（旧帧与新帧并存时，其他播放器只读第一个 USLT，
    /// 读到的仍是旧歌词），再写入唯一一帧；编码用 UTF-16（ID3v2.3 标准编码，兼容性最好）。
    /// 注意：不能对 MP3 走 Tag.Lyrics 组合 setter（会再建一个 desc=null 的帧造成重复）。
    /// 只改内存标签，由调用方 Save。
    /// </summary>
    internal static void WriteLyricsToTag(TagLib.File tagFile, string lyrics)
    {
        var id3v2 = tagFile.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
        if (id3v2 == null && tagFile.MimeType == "audio/mpeg")
            id3v2 = tagFile.GetTag(TagLib.TagTypes.Id3v2, true) as TagLib.Id3v2.Tag;

        if (id3v2 != null)
        {
            foreach (var fr in id3v2.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().ToList())
                id3v2.RemoveFrame(fr);
            var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3v2, "", "", true);
            frame.TextEncoding = TagLib.StringType.UTF16;
            frame.Text = lyrics;
            return;
        }
        tagFile.Tag.Lyrics = lyrics; // 无 ID3v2 的格式（FLAC/M4A 等）走通用歌词字段
    }

    internal static void WriteLyricsOnly(string file, string lyrics)
    {
        using var tagFile = TagLib.File.Create(file);
        WriteLyricsToTag(tagFile, lyrics); // 网络歌词原格式写入，不做任何转换
        tagFile.Save();
    }

    internal static readonly System.Net.Http.HttpClient CoverHttp = new();

    // 悬浮层确认结果写入：歌词原格式；WriteTags=true 时连歌名/歌手/专辑/封面一起写入
    private static async Task WriteMatchResultAsync(string file, MatchResult result)
    {
        using var tagFile = TagLib.File.Create(file);
        WriteLyricsToTag(tagFile, result.Lyrics);

        if (result.WriteTags)
        {
            tagFile.Tag.Title = result.Title;
            tagFile.Tag.Performers = result.Artist.Length > 0
                ? result.Artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
            tagFile.Tag.Album = result.Album;

            // 封面图片：下载网络封面写入（失败不阻断歌词/标签写入）
            if (result.CoverUrl.Length > 0)
            {
                try
                {
                    var data = await CoverHttp.GetByteArrayAsync(result.CoverUrl);
                    if (data.Length > 0)
                    {
                        var mime = result.CoverUrl.Contains(".png", StringComparison.OrdinalIgnoreCase)
                            ? "image/png" : "image/jpeg";
                        tagFile.Tag.Pictures = new[]
                        {
                            new TagLib.Picture(new TagLib.ByteVector(data))
                            {
                                Type = TagLib.PictureType.FrontCover,
                                MimeType = mime,
                                Description = "cover",
                            },
                        };
                    }
                }
                catch { }
            }
        }
        tagFile.Save();
    }

    // 悬浮层驱动播放时，把主界面"正在播放"上下文同步到该歌曲（列表选中、状态条、逐字歌词）
    private void SyncPlayingContext(SongItem item)
    {
        if (_playingItem == item) return;
        _playingIndex = _songs.IndexOf(item);
        _playingItem = item;
        PlayingLabel.Text = $"正在播放: {item.Title} - {item.Artist}";
        SongList.SelectedIndex = _playingIndex;
        SongList.ScrollIntoView(item);

        _ = Task.Run(() => ReadSongInfoSafe(item.File)).ContinueWith(t =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var info = t.Result;
                var dur = info.Duration > TimeSpan.Zero ? info.Duration : _player.Duration;
                LoadPlayingLyrics(NormalizeNewlines(info.Lyrics), dur);
            });
        });
        _ = LoadCoverAsync(item.File);
    }

    // ===== 写入数据 =====
    private async void OnWriteDataClick(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not SongItem item)
        {
            await ShowDialogAsync("提示", "请先在列表中选择一首歌曲。");
            return;
        }
        var file = item.File;

        WriteDataButton.IsEnabled = false;

        // 正在播放/加载的文件被播放引擎占用，需先停止释放句柄，写完再恢复
        var playingThis = _playingItem == item;
        var wasPlaying = playingThis && _player.State == NAudio.Wave.PlaybackState.Playing;
        var resumePos = playingThis ? _player.Position : TimeSpan.Zero;
        if (playingThis) _player.Stop();

        try
        {
            var title = TitleBox.Text.Trim();
            var artist = ArtistBox.Text.Trim();
            var album = AlbumBox.Text.Trim();
            var lyrics = LyricsBox.Text == NoLyricsPlaceholder ? "" : LyricsBox.Text;

            await Task.Run(() => WriteTags(file, title, artist, album, lyrics));

            // 写入完成统一后处理：移动到「已处理」+ 状态记录 + 列表排序
            var (finalState, finalStatus, newFile) = await OnWriteCompletedAsync(item, ReplaceState.Ok, "已写入");
            file = newFile;

            // 刷新列表与状态条
            var displayTitle = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(file);
            item.Title = displayTitle;
            item.Artist = artist;

            // 从文件重读刷新列表行，保证列表与文件实际标签一致
            var fresh = await Task.Run(() => ReadSongInfoSafe(file));
            if (fresh.Title.Length > 0) item.Title = fresh.Title;
            if (fresh.Artist.Length > 0) item.Artist = fresh.Artist;
            StatusTitle.Text = $"歌名: {item.Title}";
            StatusArtist.Text = $"歌手: {item.Artist}";

            // 写入后刷新主界面滚动歌词面板（正在播放其他歌曲时保持其歌词不动，避免同步错乱）
            if (_playingIndex < 0 || playingThis)
            {
                var dur = fresh.Duration > TimeSpan.Zero ? fresh.Duration : _player.Duration;
                LoadPlayingLyrics(NormalizeNewlines(lyrics), dur);
            }

            await ShowDialogAsync("完成",
                finalState == ReplaceState.Fail ? finalStatus : "已写入歌曲内嵌标签。");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("写入失败",
                $"{ex.Message}\n\n若该文件仍被其他程序占用，请先关闭占用它的程序后重试。");
        }
        finally
        {
            // 恢复播放（从停止处继续）
            if (playingThis)
            {
                try
                {
                    _player.Load(file);
                    _player.Seek(resumePos);
                    if (wasPlaying) _player.Play();
                }
                catch { }
                // 按钮标题由 StateChanged 事件统一同步
            }
            WriteDataButton.IsEnabled = true;
        }
    }

    private static void WriteTags(string file, string title, string artist, string album, string lyrics)
    {
        using var tagFile = TagLib.File.Create(file);
        tagFile.Tag.Title = title;
        tagFile.Tag.Performers = artist.Length > 0
            ? artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        tagFile.Tag.Album = album;
        WriteLyricsToTag(tagFile, lyrics.Replace("\r\n", "\n"));
        tagFile.Save();
    }

    // ===== 公共对话框 =====
    private async Task ShowDialogAsync(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
