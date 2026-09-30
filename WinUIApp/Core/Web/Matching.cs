using System.Security.Cryptography;
using System.Text;

namespace LyricsApp.Web;

/// <summary>候选歌曲（来自网络平台的搜索结果）。</summary>
public sealed class SongCandidate
{
    public required string Source { get; init; }      // 平台名
    public required string Id { get; init; }          // 取词用的歌曲 id
    public string Extra { get; init; } = "";          // 平台取词附加数据（如酷狗 hash）
    public required string Title { get; init; }
    public string Artists { get; init; } = "";
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string CoverUrl { get; init; } = "";
    public double Score { get; set; }                 // 匹配度 0~100
    public bool DurationMatch { get; set; }

    // 取词结果（懒加载缓存）
    public string? LyricsText { get; set; }           // 统一 LRC 格式
    public string LyricType { get; set; } = "";       // "逐字" / "逐行"
}

/// <summary>本地待匹配歌曲信息。</summary>
public sealed record LocalSong(string Title, string Artist, string Album, TimeSpan Duration, string FileName);

/// <summary>歌词提供者统一接口。</summary>
public interface ILyricsProvider
{
    string Name { get; }
    // onItem：每解析出一个候选立即回调（流式加入列表用），完成后仍返回完整列表
    Task<List<SongCandidate>> SearchAsync(string keyword, LocalSong song, CancellationToken ct, Action<SongCandidate>? onItem = null);
    Task<string?> GetLyricsAsync(SongCandidate candidate, CancellationToken ct);
}

/// <summary>
/// 匹配评分（参考 LDDC 的匹配思路自行实现）：
/// 时长 ±4s 硬过滤 → 标题(50%)+歌手(50%) 或 标题(50%)+歌手(35%)+专辑(15%) 加权 → 阈值。
/// </summary>
public static class MatchScorer
{
    /// <summary>全角转半角 + 去空白 + 小写。</summary>
    public static string Unified(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            var c = ch switch
            {
                '　' => ' ',
                '（' => '(',
                '）' => ')',
                '［' => '[',
                '］' => ']',
                '｛' => '{',
                '｝' => '}',
                '！' => '!',
                '？' => '?',
                '：' => ':',
                '；' => ';',
                '，' => ',',
                '．' => '.',
                '～' => '~',
                '－' => '-',
                _ => ch,
            };
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Levenshtein 相似度比率 0~1（等价 difflib SequenceMatcher 的近似替代）。</summary>
    public static double TextDifference(string a, string b)
    {
        a = Unified(a);
        b = Unified(b);
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a == b) return 1.0;

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - d[a.Length, b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    /// <summary>版本标签归一：去掉 (Live)/(Inst)/(伴奏)/Ver./Mix 等常见版本词再比较。</summary>
    private static string StripVersionTags(string title)
    {
        var s = Unified(title);
        // 去掉括号内的版本标签与常见后缀词
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\(([^()]*)(live|inst|rumental|伴奏|纯音乐|offvocal|cover|remix|mix|ver|version|demo|tv|钢琴版|卡拉ok|伴奏版)[^()]*\)", "");
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(live|inst|rumental|伴奏|纯音乐|offvocal|cover|remix|mix|ver|version|demo)$", "");
        return s.Trim('-', '_', ' ', '.', '~');
    }

    /// <summary>标题评分 0~100：整体相似度与"去版本标签后相似度"取大者。</summary>
    public static double TitleScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var ua = Unified(a);
        var ub = Unified(b);
        if (ua == ub) return 100;
        var score0 = TextDifference(ua, ub) * 100;
        var sa = StripVersionTags(a);
        var sb = StripVersionTags(b);
        var score1 = sa.Length > 0 && sb.Length > 0 ? TextDifference(sa, sb) * 100 : 0;
        // 标签结构不同但主体相同 → 轻微折减
        return Math.Max(score0, score1 * 0.96);
    }

    /// <summary>歌手列表解析：按 , 、 / \ & feat. 分割。</summary>
    private static List<string> SplitArtists(string artist)
    {
        var list = System.Text.RegularExpressions.Regex.Split(
            Unified(artist).Replace("feat.", ",").Replace("ft.", ","),
            @"[,、/\\&・]|与|和");
        return list.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
    }

    /// <summary>多歌手贪心一一配对取平均相似度（参考 LDDC 的歌手匹配思路自行实现）。</summary>
    public static double ArtistScore(string a, string b)
    {
        var la = SplitArtists(a);
        var lb = SplitArtists(b);
        if (la.Count == 0 || lb.Count == 0) return 0;
        var pairs = new List<(int i, int j, double s)>();
        for (var i = 0; i < la.Count; i++)
            for (var j = 0; j < lb.Count; j++)
                pairs.Add((i, j, TextDifference(la[i], lb[j])));
        var usedA = new HashSet<int>();
        var usedB = new HashSet<int>();
        double total = 0;
        foreach (var (i, j, s) in pairs.OrderByDescending(p => p.s))
        {
            if (usedA.Contains(i) || usedB.Contains(j)) continue;
            usedA.Add(i);
            usedB.Add(j);
            total += s;
        }
        return total / Math.Max(la.Count, lb.Count) * 100;
    }

    /// <summary>综合评分（本地歌曲 vs 候选）。</summary>
    public static double Score(LocalSong song, SongCandidate cand)
    {
        // 时长硬过滤：差超过 4 秒直接淘汰
        if (song.Duration > TimeSpan.Zero && cand.Duration > TimeSpan.Zero)
        {
            var diff = song.Duration - cand.Duration;
            if (diff < TimeSpan.Zero) diff = -diff;
            cand.DurationMatch = diff <= TimeSpan.FromSeconds(2);
            if (diff > TimeSpan.FromSeconds(4))
                return -1;
        }

        var title = TitleScore(song.Title, cand.Title);
        double score;
        if (song.Artist.Length > 0 && cand.Artists.Length > 0)
        {
            var artist = ArtistScore(song.Artist, cand.Artists);
            var album = song.Album.Length > 0 && cand.Album.Length > 0
                ? TextDifference(song.Album, cand.Album) * 100 : 0;
            score = Math.Max(title * 0.5 + artist * 0.5, title * 0.5 + artist * 0.35 + album * 0.15);
        }
        else
        {
            score = song.Album.Length > 0 && cand.Album.Length > 0
                ? Math.Max(title, title * 0.7 + TextDifference(song.Album, cand.Album) * 30)
                : title;
        }

        // 标题都对不上基本没戏
        if (title < 30) score -= 35;
        return score;
    }
}
