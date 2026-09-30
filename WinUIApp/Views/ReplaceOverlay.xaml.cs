using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LyricsApp;
using LyricsApp.Web;
using LyricsApp.WinUI.Controls;
using LyricsApp.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;

namespace LyricsApp.WinUI.Views;

/// <summary>全部替换处理状态。</summary>
public enum ReplaceState
{
    Pending,
    Ok,
    NoMatch,
    Fail,
}

/// <summary>
/// 全部替换悬浮层：上半为处理结果列表，下方「网络歌词 / 本地歌词」两个入口。
/// 本地歌词：选择文件夹后按文件名匹配 .lrc，批量写入对应歌曲的内嵌歌词，
/// 写入成功的文件移动到「原目录/已处理」下；状态由外部字典跨开关保留。
/// </summary>
public sealed partial class ReplaceOverlay : UserControl
{
    private static readonly SolidColorBrush OkBrush = new(Windows.UI.Color.FromArgb(255, 63, 185, 80));
    private static readonly SolidColorBrush FailBrush = new(Windows.UI.Color.FromArgb(255, 248, 81, 73));
    private static readonly SolidColorBrush GrayBrush = new(Windows.UI.Color.FromArgb(255, 138, 145, 156));

    private readonly MainWindow _owner;
    private readonly List<ReplaceItemView> _items;
    private readonly Dictionary<string, (ReplaceState State, string StatusText)> _states;
    // 网络歌词源（与匹配歌词悬浮层一致的四平台）
    private readonly ILyricsProvider[] _providers =
        { new NetEaseProvider(), new LrclibProvider(), new QQMusicProvider(), new KugouProvider() };
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    private bool _written;
    private bool _moved;

    /// <summary>关闭时返回是否有歌词被写入。</summary>
    public Task<bool> Completion => _completion.Task;

    public ReplaceOverlay(MainWindow owner, IReadOnlyList<SongItem> songs,
        Dictionary<string, (ReplaceState State, string StatusText)> states)
    {
        _owner = owner;
        _states = states;
        _items = songs.Select((s, i) => new ReplaceItemView((i + 1).ToString(), s.Title, s.File, s)).ToList();
        InitializeComponent();
        // 软件运行期间保留已处理记录：恢复此前写入过/失败的状态标识
        foreach (var item in _items)
            if (_states.TryGetValue(item.File, out var st))
                item.ApplyState(st.State, st.StatusText);
        ResultList.ItemsSource = _items;

        // 面板尺寸跟随主窗口：长宽 = 悬浮层容器（即主窗口客户区）的 3/4
        SizeChanged += (s, e) =>
        {
            PanelBorder.Width = e.NewSize.Width * 3.0 / 4.0;
            PanelBorder.Height = e.NewSize.Height * 3.0 / 4.0;
        };
    }

    // ===== 显示 / 关闭（带动画，与 MatchOverlay 一致） =====
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
        await PlayHideAnimationAsync();
        _completion.TrySetResult(_written);
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

    // ===== 本地歌词：选择文件夹 → 按文件名匹配 → 批量写入 → 移动已处理 =====
    private async void OnLocalLyricsClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        NetworkButton.IsEnabled = false;
        LocalButton.IsEnabled = false;
        try
        {
            await RunLocalMatchAsync(folder.Path);
            SortResults();
            _owner.ReorderSongsByReplaceState(); // 全部无匹配/失败时主列表也要按状态排序
            if (_moved)
                await ShowDoneDialogAsync();
        }
        finally
        {
            NetworkButton.IsEnabled = true;
            LocalButton.IsEnabled = true;
        }
    }

    private async Task RunLocalMatchAsync(string folder)
    {
        // 歌词文件按「文件名（不含扩展名）」建索引（忽略大小写）
        var lrcMap = await Task.Run(() => Directory.EnumerateFiles(folder, "*.lrc")
            .GroupBy(f => Path.GetFileNameWithoutExtension(f) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

        // 待写入文件若正被播放器占用，先停止播放，全部写完后恢复
        var engine = SharedPlayer.Instance.Engine;
        var playingFile = SharedPlayer.Instance.CurrentFile;
        var playingThis = playingFile is { Length: > 0 } pf && _items.Any(i =>
            lrcMap.ContainsKey(i.SongKey) &&
            string.Equals(i.File, pf, StringComparison.OrdinalIgnoreCase));
        var wasPlaying = playingThis && engine.State == NAudio.Wave.PlaybackState.Playing;
        var resumePos = playingThis ? engine.Position : TimeSpan.Zero;
        if (playingThis) engine.Stop();

        try
        {
            foreach (var item in _items)
            {
                if (!lrcMap.TryGetValue(item.SongKey, out var lrcPath))
                {
                    RefreshItem(item, ReplaceState.NoMatch, "无匹配歌词");
                    continue;
                }
                try
                {
                    var lyrics = await File.ReadAllTextAsync(lrcPath);
                    var oldPath = item.File;
                    await Task.Run(() =>
                    {
                        using var tagFile = TagLib.File.Create(oldPath);
                        MainWindow.WriteLyricsToTag(tagFile, lyrics); // lrc 原样写入并清理旧 USLT 帧
                        tagFile.Save();
                    });
                    _written = true;

                    // 写入完成统一后处理（主窗口公共方法）：移动到「已处理」+ 状态记录 + 主列表排序
                    var (finalState, finalStatus, newFile) =
                        await _owner.OnWriteCompletedAsync(item.Song, ReplaceState.Ok, "已写入");
                    if (string.Equals(oldPath, playingFile, StringComparison.OrdinalIgnoreCase))
                        playingFile = newFile; // 播放恢复用移动后的新路径
                    if (!string.Equals(newFile, oldPath, StringComparison.OrdinalIgnoreCase))
                        _moved = true;
                    item.File = newFile;
                    item.SetStatus(finalStatus, StateBrush(finalState), finalState);
                }
                catch (Exception ex)
                {
                    RefreshItem(item, ReplaceState.Fail, $"失败: {ex.Message}");
                }
            }
        }
        finally
        {
            if (playingThis && playingFile is { } path)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        engine.Load(path);
                        engine.Seek(resumePos);
                        if (wasPlaying) engine.Play();
                    }
                    catch { }
                });
            }
        }
    }

    // 未发生写入的状态（无匹配/写入失败）在此记录；写入后的状态由主窗口 OnWriteCompletedAsync 统一记录
    // 状态更新（可能从并行工作线程调用）：切回 UI 线程更新绑定与共享状态字典
    private void RefreshItem(ReplaceItemView item, ReplaceState state, string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _states[item.File] = (state, status);
            item.SetStatus(status, StateBrush(state), state);
        });
    }

    private static Brush StateBrush(ReplaceState state) => state switch
    {
        ReplaceState.Ok => OkBrush,
        ReplaceState.Fail => FailBrush,
        _ => GrayBrush,
    };

    // 排序结果列表：失败 → 无匹配 → 待处理 → 已写入（组内保持原顺序，权重与主界面一致）
    private void SortResults()
    {
        var ordered = _items.OrderBy(i => MainWindow.StateRank(i.State)).ToList();
        ResultList.ItemsSource = ordered;
    }

    // ===== 网络歌词：选择模式 → 四平台搜索 → 过滤 100%+时长匹配 → 优先逐字写入 =====
    private async void OnNetworkLyricsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "网络歌词",
            Content = "只检索逐字歌词：仅为每首歌检索「匹配度100% · 时长匹配 · 逐字」的歌词，找不到则标记无匹配。\n\n"
                + "优先检索逐字歌词：先检索逐字歌词；没有逐字时，使用「匹配度100% · 时长匹配」的逐行歌词。",
            PrimaryButtonText = "只检索逐字歌词",
            SecondaryButtonText = "优先检索逐字歌词",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.None) return;
        var charOnly = result == ContentDialogResult.Primary;

        NetworkButton.IsEnabled = false;
        LocalButton.IsEnabled = false;
        try
        {
            await RunNetworkMatchAsync(charOnly);
            SortResults();
            _owner.ReorderSongsByReplaceState(); // 全部无匹配/失败时主列表也要按状态排序
            if (_moved)
                await ShowDoneDialogAsync();
        }
        finally
        {
            NetworkButton.IsEnabled = true;
            LocalButton.IsEnabled = true;
        }
    }

    // 网络歌词批量匹配写入：5 个并行工作流同时检索+写入（ThreadPool 线程，
    // SemaphoreSlim 控制并发上限，全部完成后任务自动结束、线程回收销毁）；
    // 写入完成走与本地歌词相同的统一后处理（移动到已处理 + 排序）
    private async Task RunNetworkMatchAsync(bool charOnly)
    {
        // 待写入文件若正被播放器占用，先停止播放，全部写完后恢复
        var engine = SharedPlayer.Instance.Engine;
        var playingFile = SharedPlayer.Instance.CurrentFile;
        var playingThis = playingFile is { Length: > 0 } pf && _items.Any(i =>
            i.State != ReplaceState.Ok &&
            string.Equals(i.File, pf, StringComparison.OrdinalIgnoreCase));
        var wasPlaying = playingThis && engine.State == NAudio.Wave.PlaybackState.Playing;
        var resumePos = playingThis ? engine.Position : TimeSpan.Zero;
        if (playingThis) engine.Stop();

        try
        {
            using var cts = new CancellationTokenSource();
            using var throttle = new SemaphoreSlim(5, 5);
            var playingLock = new object();

            async Task ProcessOneAsync(ReplaceItemView item)
            {
                await throttle.WaitAsync(cts.Token);
                try
                {
                    RefreshItem(item, item.State, "正在检索…");

                    var song = await BuildLocalSongAsync(item);
                    List<SongCandidate> filtered;
                    try
                    {
                        var all = await SearchAllAsync(song, cts.Token);
                        filtered = all
                            .Where(c => Math.Round(c.Score) == 100 && c.DurationMatch)
                            .OrderByDescending(c => c.Score)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        RefreshItem(item, ReplaceState.Fail, $"失败: {ex.Message}");
                        return;
                    }

                    if (filtered.Count == 0)
                    {
                        RefreshItem(item, ReplaceState.NoMatch, "无匹配歌词");
                        return;
                    }

                    string? lyrics;
                    try { lyrics = await PickLyricsAsync(filtered, charOnly, cts.Token); }
                    catch (Exception ex)
                    {
                        RefreshItem(item, ReplaceState.Fail, $"失败: {ex.Message}");
                        return;
                    }
                    if (lyrics == null)
                    {
                        RefreshItem(item, ReplaceState.NoMatch, "无匹配歌词");
                        return;
                    }

                    var oldPath = item.File;
                    try
                    {
                        await Task.Run(() =>
                        {
                            using var tagFile = TagLib.File.Create(oldPath);
                            MainWindow.WriteLyricsToTag(tagFile, lyrics); // 网络歌词原格式写入并清理旧 USLT 帧
                            tagFile.Save();
                        });
                        _written = true;

                        // 与本地歌词一致：写入完成统一后处理（移动到已处理 + 状态记录 + 主列表排序）
                        var (finalState, finalStatus, newFile) =
                            await _owner.OnWriteCompletedAsync(item.Song, ReplaceState.Ok, "已写入");
                        lock (playingLock)
                        {
                            if (string.Equals(oldPath, playingFile, StringComparison.OrdinalIgnoreCase))
                                playingFile = newFile;
                        }
                        if (!string.Equals(newFile, oldPath, StringComparison.OrdinalIgnoreCase))
                            _moved = true;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            item.File = newFile;
                            item.SetStatus(finalStatus, StateBrush(finalState), finalState);
                        });
                    }
                    catch (Exception ex)
                    {
                        RefreshItem(item, ReplaceState.Fail, $"失败: {ex.Message}");
                    }
                }
                finally
                {
                    throttle.Release();
                }
            }

            var workers = _items
                .Where(i => i.State != ReplaceState.Ok)
                .Select(ProcessOneAsync)
                .ToList();
            await Task.WhenAll(workers); // 全部任务完成后工作线程随之回收销毁
        }
        finally
        {
            if (playingThis && playingFile is { } path)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        engine.Load(path);
                        engine.Seek(resumePos);
                        if (wasPlaying) engine.Play();
                    }
                    catch { }
                });
            }
        }
    }

    // 从文件读取时长/专辑，构建批量网络匹配用的本地歌曲信息
    private async Task<LocalSong> BuildLocalSongAsync(ReplaceItemView item)
    {
        var f = item.File;
        var (dur, album) = await Task.Run(() =>
        {
            try
            {
                using var tf = TagLib.File.Create(f);
                return (tf.Properties?.Duration ?? TimeSpan.Zero, tf.Tag.Album ?? "");
            }
            catch { return (TimeSpan.Zero, ""); }
        });
        return new LocalSong(item.Song.Title, item.Song.Artist, album, dur, Path.GetFileName(f));
    }

    // 四平台并发搜索并聚合候选
    private async Task<List<SongCandidate>> SearchAllAsync(LocalSong song, CancellationToken token)
    {
        var keyword = song.Artist.Length > 0 ? song.Artist.Split(" / ")[0] + " - " + song.Title : song.Title;
        if (keyword.Length == 0) keyword = Path.GetFileNameWithoutExtension(song.FileName);
        var all = new List<SongCandidate>();
        var tasks = _providers.Select(async p =>
        {
            try
            {
                var res = await p.SearchAsync(keyword, song, token);
                lock (all) all.AddRange(res);
            }
            catch { /* 单平台失败忽略，其余平台结果可用 */ }
        }).ToList();
        await Task.WhenAll(tasks);
        return all;
    }

    private async Task<string?> FetchLyricsAsync(SongCandidate cand, CancellationToken token)
    {
        var provider = _providers.FirstOrDefault(p => p.Name == cand.Source);
        if (provider == null) return null;
        if (cand.LyricsText == null)
            cand.LyricsText = await provider.GetLyricsAsync(cand, token);
        return cand.LyricsText;
    }

    // 按分数降序逐个取词：找到逐字歌词立即返回；
    // charOnly=true 时只认逐字；false 时记住第一个逐行作为兜底
    private async Task<string?> PickLyricsAsync(List<SongCandidate> filtered, bool charOnly, CancellationToken token)
    {
        string? firstLine = null;
        foreach (var cand in filtered)
        {
            string? lyrics;
            try { lyrics = await FetchLyricsAsync(cand, token); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (lyrics == null) continue;
            var isCharLevel = LrcParser.Parse(lyrics).Any(l => l.Chars.Count > 1);
            if (isCharLevel) return lyrics;
            if (!charOnly) firstLine ??= lyrics;
        }
        return firstLine;
    }

    private async Task ShowDoneDialogAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "完成",
            Content = "写入完成，文件已经移动到「已处理」的目录下。",
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>全部替换结果列表项视图模型。</summary>
public sealed class ReplaceItemView : INotifyPropertyChanged
{
    public ReplaceItemView(string no, string title, string file, SongItem song)
    {
        No = no;
        Title = title;
        File = file;
        Song = song;
        SongKey = Path.GetFileNameWithoutExtension(file);
    }

    public string No { get; }
    public string Title { get; }
    public SongItem Song { get; }

    /// <summary>匹配键：歌曲文件名（不含扩展名）。</summary>
    public string SongKey { get; }

    /// <summary>音频文件路径；移动到已处理目录后更新。</summary>
    public string File { get; set; }

    public ReplaceState State { get; private set; } = ReplaceState.Pending;

    private string _status = "待处理";
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    private Brush _statusBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 145, 156));
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; OnPropertyChanged(); }
    }

    public void SetStatus(string status, Brush brush, ReplaceState state)
    {
        Status = status;
        StatusBrush = brush;
        State = state;
    }

    /// <summary>按持久化状态恢复显示。</summary>
    public void ApplyState(ReplaceState state, string statusText)
    {
        var brush = state switch
        {
            ReplaceState.Ok => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 63, 185, 80)),
            ReplaceState.Fail => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 81, 73)),
            ReplaceState.NoMatch => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 145, 156)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 145, 156)),
        };
        SetStatus(statusText, brush, state);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
