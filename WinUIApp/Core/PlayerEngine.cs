using System.Text.RegularExpressions;
using NAudio.Wave;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LyricsApp;

// 逐字歌词中的一个字/片段（Time 为绝对时间，Duration 为该字持续时长）
public sealed record KaraokeChar(string Text, TimeSpan Time, TimeSpan Duration);

// 一行歌词：行起始时间与持续、字级时间轴（逐行 LRC 时 Chars 只含整行一个元素）。
// Translation/Romanization 为同时间跟行的翻译/发音（增强 LRC + 译文格式），IsEnhanced 标识尖括号逐字行
public sealed record LrcLine(TimeSpan Time, TimeSpan Duration, IReadOnlyList<KaraokeChar> Chars,
    string? Translation = null, string? Romanization = null, bool IsEnhanced = false)
{
    public string Text => string.Concat(Chars.Select(c => c.Text));
    public bool HasSubLines => Translation != null || Romanization != null;
}

// 歌词解析：兼容逐字格式（QRC/YRC/KRC 风格）与逐行 LRC
public static class LrcParser
{
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    // 逐字行头：[起始ms,持续ms]
    private static readonly Regex MsLineRegex = new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);
    // 字级时间标记：(offset[,dur]) 或 <offset[,dur]>
    private static readonly Regex CharTagRegex = new(@"(?:\(|<)(\d+)(?:,(\d+))?(?:\)|>)", RegexOptions.Compiled);
    // 增强型 LRC 尖括号逐字标记：<mm:ss.mmm>（token 在字前）
    private static readonly Regex EnhancedTagRegex = new(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);

    public static List<LrcLine> Parse(string lyrics)
    {
        var result = new List<LrcLine>();
        // 兼容 CR-only（\r）与 CRLF 换行
        foreach (var raw in lyrics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // 1) 逐字格式：行头 [start,dur] + 字级 (offset[,dur])/<offset[,dur]>，如 QRC/KRC/YRC
            var msLine = MsLineRegex.Match(line);
            if (msLine.Success)
            {
                var start = TimeSpan.FromMilliseconds(double.Parse(msLine.Groups[1].Value));
                var dur = TimeSpan.FromMilliseconds(double.Parse(msLine.Groups[2].Value));
                var chars = ParseCharTimeline(line[msLine.Length..], start);
                if (chars.Count > 0)
                {
                    result.Add(new LrcLine(start, dur, chars));
                    continue;
                }
            }

            var tags = TimeTagRegex.Matches(line);
            if (tags.Count == 0)
                continue;
            var lineTime = ParseLrcTimestamp(tags[0]);

            var clean = TimeTagRegex.Replace(line, "").Trim();
            if (clean.Length == 0)
                continue; // 纯标签行（元数据等）

            // 2) 增强型 LRC：方括号逐句定位 + 尖括号逐字定位（token 在字前，如 <00:00.125>字）
            if (EnhancedTagRegex.IsMatch(clean))
            {
                var chars = ParseEnhancedChars(clean, lineTime);
                if (chars.Count > 0)
                {
                    result.Add(new LrcLine(lineTime, TimeSpan.Zero, chars, IsEnhanced: true));
                    continue;
                }
                continue; // 只有尖括号 token 没有文本（纯间奏行），不显示
            }

            // 判断是否为行内字级时间轴：时间标签之间存在文字
            var textBetweenTags = false;
            for (var t = 0; t < tags.Count; t++)
            {
                var segStart = t == 0 ? 0 : tags[t - 1].Index + tags[t - 1].Length;
                var seg = line[segStart..tags[t].Index].Trim();
                if (seg.Length > 0)
                {
                    textBetweenTags = true;
                    break;
                }
            }

            if (textBetweenTags)
            {
                // 3) 行内多 [mm:ss.xx] 标签字级：标签在前、字在后（QRC→LRC 转换格式）
                var chars = ParseInlineLrcChars(line);
                if (chars.Count > 0)
                {
                    result.Add(new LrcLine(lineTime, TimeSpan.Zero, chars));
                    continue;
                }
            }

            // 3) 普通 LRC：一个或多个连续标签（多标签 = 同一句在多个时间重复）
            foreach (Match t in tags)
            {
                var start = ParseLrcTimestamp(t);
                var chars = new List<KaraokeChar> { new(clean, start, TimeSpan.Zero) };
                result.Add(new LrcLine(start, TimeSpan.Zero, chars));
            }
        }
        return MergeSubLines(result.OrderBy(l => l.Time).ToList());
    }

    // 增强逐字行后同时间的纯文本行合并为子行：含 CJK → 翻译，纯拉丁 → 发音；第三个同时间行按普通行保留
    private static List<LrcLine> MergeSubLines(List<LrcLine> sorted)
    {
        var result = new List<LrcLine>(sorted.Count);
        LrcLine? pending = null;
        var pendingIdx = -1;
        foreach (var line in sorted)
        {
            if (line.IsEnhanced)
            {
                result.Add(line);
                pending = line;
                pendingIdx = result.Count - 1;
                continue;
            }
            var isSub = pending != null && Math.Abs((line.Time - pending.Time).TotalMilliseconds) <= 60;
            if (isSub && HasCjk(line.Text) && pending!.Translation == null)
            {
                pending = pending with { Translation = line.Text };
                result[pendingIdx] = pending;
                continue;
            }
            if (isSub && pending!.Romanization == null)
            {
                pending = pending with { Romanization = line.Text };
                result[pendingIdx] = pending;
                continue;
            }
            result.Add(line);
            pending = null;
        }
        return result;
    }

    private static bool HasCjk(string s)
    {
        foreach (var c in s)
            if (c >= 0x2E80 && c <= 0x9FFF || c >= 0x3040 && c <= 0xD7AF || c >= 0xF900 && c <= 0xFAFF)
                return true;
        return false;
    }

    // 行内多 [mm:ss.xx] 标签字级时间轴：标签在前、字在后 —— 标签 i 的时间作用于其后文本，
    // 时长取到下一标签的间隔；尾部空文本不产生字
    private static List<KaraokeChar> ParseInlineLrcChars(string line)
    {
        var matches = TimeTagRegex.Matches(line);
        if (matches.Count == 0)
            return new List<KaraokeChar>();

        var chars = new List<KaraokeChar>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            var text = line[start..end];
            if (text.Trim().Length == 0) continue;
            var t = ParseLrcTimestamp(matches[i]);
            var dur = i + 1 < matches.Count ? ParseLrcTimestamp(matches[i + 1]) - t : TimeSpan.Zero;
            chars.Add(new KaraokeChar(text, t, dur > TimeSpan.Zero ? dur : TimeSpan.Zero));
        }
        return chars;
    }

    // 增强型 LRC 字级时间轴：尖括号 token 在前、文本在后 —— token i 的时间作用于其后文本，
    // 时长取到下一 token 的间隔；行尾 token 不产生字，作为最后一个字的结束边界
    private static List<KaraokeChar> ParseEnhancedChars(string body, TimeSpan lineTime)
    {
        var matches = EnhancedTagRegex.Matches(body);
        if (matches.Count == 0)
            return new List<KaraokeChar>();

        var chars = new List<KaraokeChar>();

        // 首个 token 前的文本（罕见）用行时间
        var prefix = body[..matches[0].Index];
        if (prefix.Trim().Length > 0)
        {
            var pDur = ParseLrcTimestamp(matches[0]) - lineTime;
            chars.Add(new KaraokeChar(prefix, lineTime, pDur > TimeSpan.Zero ? pDur : TimeSpan.Zero));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var text = body[start..end];
            if (text.Trim().Length == 0) continue;
            var t = ParseLrcTimestamp(matches[i]);
            var dur = i + 1 < matches.Count ? ParseLrcTimestamp(matches[i + 1]) - t : TimeSpan.Zero;
            chars.Add(new KaraokeChar(text, t, dur > TimeSpan.Zero ? dur : TimeSpan.Zero));
        }
        return chars;
    }

    private static TimeSpan ParseLrcTimestamp(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value);
        var seconds = int.Parse(m.Groups[2].Value);
        var frac = m.Groups[3].Value;
        var ms = frac.Length switch
        {
            1 => int.Parse(frac) * 100,
            2 => int.Parse(frac) * 10,
            3 => int.Parse(frac),
            _ => 0,
        };
        return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(ms);
    }

    // 解析 "字(123)字(456,78)..." 字级时间轴；offset 小于行起始视为相对行首，否则为绝对毫秒
    private static List<KaraokeChar> ParseCharTimeline(string body, TimeSpan lineStart)
    {
        var chars = new List<KaraokeChar>();
        var matches = CharTagRegex.Matches(body);
        if (matches.Count == 0)
            return chars;

        var textPos = 0;
        foreach (Match m in matches)
        {
            var text = body.Substring(textPos, m.Index - textPos);
            var offset = double.Parse(m.Groups[1].Value);
            var dur = m.Groups[2].Success ? double.Parse(m.Groups[2].Value) : 300;
            var absMs = offset < lineStart.TotalMilliseconds
                ? lineStart.TotalMilliseconds + offset
                : offset;
            if (text.Length > 0)
                chars.Add(new KaraokeChar(text, TimeSpan.FromMilliseconds(absMs), TimeSpan.FromMilliseconds(dur)));
            textPos = m.Index + m.Length;
        }

        // 行尾无时间标记的文本并入最后一字
        var tail = body[textPos..];
        if (tail.Length > 0 && chars.Count > 0)
            chars[^1] = chars[^1] with { Text = chars[^1].Text + tail };
        return chars;
    }
}

// 音频播放封装（Windows.Media.Playback.MediaPlayer = 系统自带 Media Foundation 官方封装，
// 无第三方 dll 与授权限制；0.5~2 倍速为非 thinning 速率——变速不变调、切换零断音）
public sealed class PlayerEngine : IDisposable
{
    private MediaPlayer? _mp;
    private bool _slow;
    private bool _ended;       // 自然结束后 MF 会话停在 Paused，映射回 Stopped 以还原原语义
    private bool _suppressEnd; // 手动 Stop/切歌期间抑制自然结束事件

    /// <summary>自然播放结束（非手动停止）时触发；可能来自非 UI 线程。</summary>
    public event Action? PlaybackEnded;

    /// <summary>播放状态（Playing/Paused/Stopped）发生改变时触发一次；可能来自非 UI 线程。</summary>
    public event Action? StateChanged;

    private PlaybackState _lastState = PlaybackState.Stopped;

    private void OnEnded(MediaPlayer sender, object args)
    {
        if (_suppressEnd) return;
        _ended = true;
        PlaybackEnded?.Invoke();
    }

    private void OnSessionStateChanged(MediaPlaybackSession sender, object args) => CheckStateChanged();

    private void CheckStateChanged()
    {
        var state = State;
        if (state == _lastState) return;
        _lastState = state;
        StateChanged?.Invoke();
    }

    // Opening/Closed 状态下读 Position/NaturalDuration 可能异常，仅安全状态读取
    private MediaPlaybackSession? Session =>
        _mp?.PlaybackSession is { } s &&
        s.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused or MediaPlaybackState.Buffering
            ? s : null;

    public TimeSpan Position => Session?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => Session?.NaturalDuration ?? TimeSpan.Zero;

    public PlaybackState State
    {
        get
        {
            if (_mp?.PlaybackSession is not { } s) return PlaybackState.Stopped;
            return s.PlaybackState switch
            {
                MediaPlaybackState.Playing => PlaybackState.Playing,
                MediaPlaybackState.Paused when !_ended => PlaybackState.Paused,
                _ => PlaybackState.Stopped, // Closed/Opening/Buffering/Interrupted/自然结束
            };
        }
    }

    public void Load(string file)
    {
        Stop();
        var mp = new MediaPlayer
        {
            AutoPlay = false, // 切歌只加载不自动播放（默认 true，必须关）
            Source = MediaSource.CreateFromUri(new Uri(file)),
        };
        mp.MediaEnded += OnEnded;
        mp.PlaybackSession.PlaybackStateChanged += OnSessionStateChanged;
        _mp = mp;
        _slow = false; // 新载入的歌曲始终常速；慢放只在打词悬浮层开启
        _ended = false;
        CheckStateChanged();
    }

    /// <summary>慢放开关：开启后以 0.7 倍速播放（MF 非 thinning 速率，变速不变调，切换零断音）。</summary>
    public void SetSlowSpeed(bool slow)
    {
        _slow = slow;
        ApplyRate();
    }

    private void ApplyRate()
    {
        if (_mp == null) return;
        try
        {
            _mp.PlaybackSession.PlaybackRate = _slow ? 0.7 : 1.0;
        }
        catch
        {
            // Opening 等状态下设置可能失败；Play() 时会重申
        }
    }

    public void Play()
    {
        if (_mp == null) return;
        if (_ended)
        {
            _ended = false;
            _mp.PlaybackSession.Position = TimeSpan.Zero; // 结束后再播从头开始
        }
        ApplyRate();
        _mp.Play();
        CheckStateChanged();
    }

    public void Pause()
    {
        _mp?.Pause();
        CheckStateChanged();
    }

    public void Toggle()
    {
        if (State == PlaybackState.Playing)
            Pause();
        else
            Play();
    }

    public void Seek(TimeSpan time)
    {
        if (Session is not { } s) return;
        var dur = s.NaturalDuration;
        var target = dur > TimeSpan.Zero && time > dur ? dur : time;
        s.Position = target > TimeSpan.Zero ? target : TimeSpan.Zero;
        _ended = false;
    }

    public void Stop()
    {
        _suppressEnd = true;
        try
        {
            if (_mp != null)
            {
                _mp.MediaEnded -= OnEnded;
                _mp.PlaybackSession.PlaybackStateChanged -= OnSessionStateChanged;
                _mp.Dispose();
            }
        }
        finally
        {
            _suppressEnd = false;
        }
        _mp = null;
        _slow = false;
        _ended = false;
        CheckStateChanged();
    }

    public void Dispose() => Stop();
}
