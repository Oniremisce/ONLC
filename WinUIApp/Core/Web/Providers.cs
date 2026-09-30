using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricsApp.Web;

/// <summary>共享 HTTP 与加密工具。</summary>
internal static class NetUtil
{
    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string Md5Hex(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Md5HexUpper(string s) => Md5Hex(s).ToUpperInvariant();

    /// <summary>AES-128-ECB + PKCS7 加密，返回 hex。</summary>
    public static string AesEcbEncryptHex(string plaintext, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var data = enc.TransformFinalBlock(Encoding.UTF8.GetBytes(plaintext), 0, Encoding.UTF8.GetByteCount(plaintext));
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    /// <summary>AES-128-ECB + PKCS7 解密 hex/bytes 为 UTF8 文本。</summary>
    public static string AesEcbDecryptText(byte[] cipher, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        var data = dec.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>3DES-ECB 解密（QQ音乐 QRC）。</summary>
    public static byte[] TripleDesEcbDecrypt(byte[] cipher, string key)
    {
        using var des = TripleDES.Create();
        des.Key = Encoding.UTF8.GetBytes(key);
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        using var dec = des.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>QQ QRC 的 3DES 解密：QQ 使用修改版 DES（位编号/置换与标准 DES 不同），移植自 WXRIW/QQMusicDecoder（MIT）。</summary>
    public static byte[] TripleDesQrc(byte[] cipher, string key) => QrcTripleDes.Decrypt(cipher, Encoding.UTF8.GetBytes(key));

    /// <summary>zlib 解压（跳过 2 字节头，用 DeflateStream 读原始 deflate 流）。</summary>
    public static byte[] ZlibInflate(byte[] data)
    {        // 跳过 zlib 头 2 字节；DeflateStream 读到 adler 尾巴可能抛异常，容忍截断
        using var ms = new MemoryStream(data, 2, data.Length - 2);
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        try
        {
            deflate.CopyTo(outMs);
        }
        catch (InvalidDataException)
        {
            // 尾部校验字节导致的截断可接受
        }
        return outMs.ToArray();
    }

    public static async Task<string> PostStringAsync(string url, HttpContent content, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (headers != null)
            foreach (var (k, v) in headers)
                req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await Http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public static async Task<string> GetStringAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null)
            foreach (var (k, v) in headers)
                req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await Http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public static JsonDocument ParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            // 网易云 eapi 响应可能是 hex 密文，交给调用方处理；此处仅报标准异常
            throw new InvalidDataException("响应不是有效 JSON");
        }
    }

    /// <summary>JsonElement 取字符串，缺失/空返回默认值。</summary>
    public static string Str(JsonElement e, string prop, string def = "")
    {
        if (e.ValueKind != JsonValueKind.Object) return def;
        if (!e.TryGetProperty(prop, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? def,
            JsonValueKind.Number => v.GetRawText(),
            _ => def,
        };
    }

    public static int Int(JsonElement e, string prop, int def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object) return def;
        if (!e.TryGetProperty(prop, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return def;
    }

    public static double Dbl(JsonElement e, string prop, double def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object) return def;
        if (!e.TryGetProperty(prop, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return def;
    }
}

/// <summary>
/// QRC/KRC 逐字歌词 → 逐字 LRC 转换。
/// 输入行格式：[start,duration](s,d,0)字(s,d,0)字... （毫秒）
/// 输出：行内多时间标签 LRC —— [mm:ss.xxx]字[mm:ss.xxx]字...
/// </summary>
public static partial class QrcKrcToLrc
{
    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex LineHead();

    // 逐字时间标记：(start,dur) / (start,dur,0) / <start,dur,0>
    [GeneratedRegex(@"[<(](\d+),(\d+)(?:,\d+)?[)>]")]
    private static partial Regex CharToken();

    /// <summary>元数据标签（id/ar/ti/by/hash/al/sign/qq/offset 等不含逐字内容）。</summary>
    private static readonly Regex MetaLine = new(@"^\[(id|ar|ti|by|hash|al|sign|qq|total|offset|kana):", RegexOptions.IgnoreCase);

    public static string Convert(string qrc)
    {
        if (string.IsNullOrWhiteSpace(qrc)) return "";
        var lines = qrc.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // 字级 token 时间基准整文件判定：酷狗 KRC 的 token 是行内相对毫秒（需 + 行头.start），
        // QQ QRC 的 token 是绝对毫秒；相对格式的每行所有 token.start 都小于行头.start
        var isRelative = DetectRelativeTokens(lines);

        var sb = new StringBuilder();
        var hasLyricLine = false;
        foreach (var line in lines)
        {
            var head = LineHead().Match(line);
            if (!head.Success)
            {
                // 已是普通 LRC / 元数据行，原样保留
                if (line.StartsWith("[") || line.StartsWith("<"))
                    sb.AppendLine(line);
                continue;
            }

            var content = head.Groups[3].Value;
            var lineStart = long.Parse(head.Groups[1].Value);
            // token 时间（相对格式补行头偏移，绝对格式原样）
            long TokenStart(Match t) =>
                isRelative ? lineStart + long.Parse(t.Groups[1].Value) : long.Parse(t.Groups[1].Value);

            // 纯音乐行 / 空内容行
            if (content.Trim().Length == 0)
            {
                sb.AppendLine($"[{MsToLrcTime(lineStart)}]♪");
                hasLyricLine = true;
                continue;
            }

            var headTime = MsToLrcTime(lineStart);
            var tokens = CharToken().Matches(content);
            if (tokens.Count == 0)
            {
                // 无逐字标记的行式 QRC（如 QQ 译文 [start,dur]整句文本）：整行用行头时间
                sb.AppendLine($"[{headTime}]{content.Trim()}");
                hasLyricLine = true;
                continue;
            }

            var sbLine = new StringBuilder();
            if (content.TrimStart().StartsWith("(") || content.TrimStart().StartsWith("<"))
            {
                // 格式A：token 在前、文本在后（yrc/KRC）——token i 的时间作用于其后文本
                var prefix = content[..tokens[0].Index];
                if (prefix.Trim().Length > 0)
                    sbLine.Append('[').Append(headTime).Append(']').Append(prefix);
                for (var i = 0; i < tokens.Count; i++)
                {
                    var start = tokens[i].Index + tokens[i].Length;
                    var end = i + 1 < tokens.Count ? tokens[i + 1].Index : content.Length;
                    var ch = content[start..end];
                    if (ch.Length == 0) continue;
                    sbLine.Append('[').Append(MsToLrcTime(TokenStart(tokens[i]))).Append(']').Append(ch);
                }
            }
            else
            {
                // 格式B：文本在前、token 在后（QQ 云端 QRC）——token i 的时间作用于其前文本
                for (var i = 0; i < tokens.Count; i++)
                {
                    var start = i > 0 ? tokens[i - 1].Index + tokens[i - 1].Length : 0;
                    var ch = content[start..tokens[i].Index];
                    if (ch.Length == 0) continue;
                    sbLine.Append('[').Append(MsToLrcTime(TokenStart(tokens[i]))).Append(']').Append(ch);
                }
                // 末尾无 token 的残留文本（如连字符）：用最后 token 的时间+时长
                var tailStart = tokens[^1].Index + tokens[^1].Length;
                var tail = content[tailStart..];
                if (tail.Trim().Length > 0)
                {
                    var lastT = TokenStart(tokens[^1]) + long.Parse(tokens[^1].Groups[2].Value);
                    sbLine.Append('[').Append(MsToLrcTime(lastT)).Append(']').Append(tail);
                }
            }
            if (sbLine.Length > 0)
            {
                sb.AppendLine(sbLine.ToString());
                hasLyricLine = true;
            }
        }

        var result = sb.ToString().TrimEnd();
        // 只有元数据、没有任何歌词行 → 视为取词失败
        if (!hasLyricLine && !result.Split('\n').Any(l => !MetaLine.IsMatch(l.Trim()) && l.Trim().Length > 0))
            return "";
        return result;
    }

    // 整文件投票：token 全部落在行头之前 → 相对（KRC）；全部不早于行头 → 绝对（QRC）
    private static bool DetectRelativeTokens(List<string> lines)
    {
        var rel = 0;
        var abs = 0;
        foreach (var line in lines)
        {
            var head = LineHead().Match(line);
            if (!head.Success) continue;
            var tokens = CharToken().Matches(head.Groups[3].Value);
            if (tokens.Count == 0) continue;
            var lineStart = long.Parse(head.Groups[1].Value);
            if (lineStart <= 0) continue; // 行头为 0 时相对/绝对等价，不参与投票
            var starts = tokens.Select(t => long.Parse(t.Groups[1].Value)).ToList();
            if (starts.All(s => s < lineStart)) rel++;
            else if (starts.All(s => s >= lineStart)) abs++;
        }
        return rel > abs;
    }

    private static string MsToLrcTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }
}

/// <summary>网易云音乐（eapi 接口，AES-ECB 加密，参考 md 2.1）。</summary>
public sealed class NetEaseProvider : ILyricsProvider
{
    public string Name => "网易云音乐";
    private const string EapiKey = "e82ckenh8dichen8";
    private const string Host = "https://interface.music.163.com";

    // 游客登录状态（进程内缓存）
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static Dictionary<string, string>? _cookies;

    /// <summary>deviceId 逐字符 XOR 密钥（参考 LDDC 的匿名用户名生成思路自行实现）。</summary>
    private const string DeviceIdXorKey = "3go8&$8*3*3h0k(2)2";

    private static string AnonimousUsername(string deviceId)
    {
        var xored = string.Concat(deviceId.Select((c, i) => (char)(c ^ DeviceIdXorKey[i % DeviceIdXorKey.Length])));
        var md5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(xored)));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{deviceId} {md5}"));
    }

    /// <summary>真实格式设备 ID 池（同 NeteaseCloudMusicApi 社区公开的设备库，事实数据）。</summary>
    private static readonly string[] DeviceIds =
    {
        "AA9955F5FE37BA7EAF48F8EF0C9966B28293CC8D6415CCD93549",
        "C4BE5BA8E337E26A1ECA938DAF7DDC6D99AA353D9E2E69F5DE2A",
        "2A6626990ED0B095ADBF14D63D91C6F8AE4CF352FF9BD1FE724E",
        "184117F946D9CF013300B74BAAFF42C04B74CE59EDA3A7B31C8E",
        "7051B0BEB96D5DC0DA8C17A034008DE086A21AB833EA41D321FF",
        "90D08AFA4FD3368D3ADD9C7BEB9D40B38066E55B4B2E9C123A26",
        "562D59EA36DB06BACE1D74A3735A7EC9753DED5BA380C2630439",
        "313CD3C6D39148E94A6CD885B40E7C489AC9504078A7513928CE",
        "75A3F0910D5A5A70B0E8BB9A084FBC672CBE8383CEDFC3C84AD2",
        "1AA1EEF80388FDD6FDB1696B84E8AE793DA9CAF444BF2277751F",
        "8DD5CA9A732199E7A3ADC4B5A3F43F00175273F8D18769CED397",
        "A998DD126BFDE300C1C6D2339BA9BA7936E5E31D38FE53E738C8",
        "F3E759572453849BB7705F232EBC44F6D40958F20DA9E33A27C7",
        "23667E54F134DA78658E73673931BCC5B1B66D64EB531633FB5E",
        "689C97934EB38AD53A7055A7E069BB8FA03064E05444E1F4416D",
        "904657F23CE1452C190DDDB7505B124874F7B7A7E5650170ACCA",
    };

    private static (string deviceId, Dictionary<string, string> preCookies) MakeDevice()
    {
        var deviceId = DeviceIds[Random.Shared.Next(DeviceIds.Length)];
        var mac = string.Join(":", Enumerable.Range(0, 6).Select(_ => Random.Shared.Next(255).ToString("X2")));
        var upper = string.Concat(Enumerable.Range(0, 8).Select(_ => (char)Random.Shared.Next('A', 'Z' + 1)));
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var cookies = new Dictionary<string, string>
        {
            ["os"] = "pc",
            ["deviceId"] = deviceId,
            ["osver"] = $"Microsoft-Windows-10--build-{Random.Shared.Next(200, 300)}00-64bit",
            ["clientSign"] = $"{mac}@@@{upper}@@@@@@{hash}",
            ["channel"] = "netease",
            ["mode"] = "MS-iCraft B760M WIFI",
            ["appver"] = "3.1.3.203419",
        };
        return (deviceId, cookies);
    }

    private static string ParamsHeader(Dictionary<string, string> cookies) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["clientSign"] = cookies["clientSign"],
            ["os"] = cookies["os"],
            ["appver"] = cookies["appver"],
            ["deviceId"] = cookies["deviceId"],
            ["requestId"] = 0,
            ["osver"] = cookies["osver"],
        });

    private static Dictionary<string, string> Headers(Dictionary<string, string> cookies)
    {
        var cookieStr = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        return new Dictionary<string, string>
        {
            ["accept"] = "*/*",
            ["content-type"] = "application/x-www-form-urlencoded",
            ["cookie"] = cookieStr,
            ["mconfig-info"] = "{\"IuRPVVmc3WWul9fT\":{\"version\":733184,\"appver\":\"3.1.3.203419\"}}",
            ["origin"] = "orpheus://orpheus",
            ["user-agent"] = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.3.203419",
        };
    }

    /// <summary>eapi 加密请求体："params=" + 大写 hex（网易 eapi 协议的公开格式）。</summary>
    private static string EncryptBody(string apiPath, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var sign = NetUtil.Md5Hex($"nobody{apiPath}use{json}md5forencrypt");
        var text = $"{apiPath}-36cd479b6b5-{json}-36cd479b6b5-{sign}";
        return "params=" + NetUtil.AesEcbEncryptHex(text, EapiKey).ToUpperInvariant();
    }

    private static async Task<byte[]> PostEapiRawAsync(string path, object payload, Dictionary<string, string> cookies, CancellationToken ct)
    {
        var body = EncryptBody(path.Replace("eapi", "api"), payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, Host + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        foreach (var (k, v) in Headers(cookies))
            req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await NetUtil.Http.SendAsync(req, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>游客登录（懒加载一次）。</summary>
    private static async Task<Dictionary<string, string>> EnsureInitAsync(CancellationToken ct)
    {
        if (_cookies != null) return _cookies;
        await InitLock.WaitAsync(ct);
        try
        {
            if (_cookies != null) return _cookies;
            var (_, pre) = MakeDevice();
            var payload = new Dictionary<string, object?>
            {
                ["username"] = AnonimousUsername(pre["deviceId"]),
                ["e_r"] = true,
                ["header"] = ParamsHeader(pre),
            };
            var respBytes = await PostEapiRawAsync("/eapi/register/anonimous", payload, pre, ct);
            var json = NetUtil.AesEcbDecryptText(respBytes, EapiKey);
            using var doc = NetUtil.ParseJson(json);
            if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code) && code != 200)
                throw new Exception($"游客登录失败 code={code}");

            var cookies = new Dictionary<string, string>(pre);
            cookies["NMTID"] = "";
            cookies["MUSIC_A"] = "";
            cookies["__csrf"] = "";
            cookies["WNMCID"] = "abcdef.1000000.01.0";
            _cookies = cookies;
            return cookies;
        }
        finally
        {
            InitLock.Release();
        }
    }

    /// <summary>eapi 请求：响应整体为 hex 密文，AES 解密回 JSON。（internal 供调试）</summary>
    internal static async Task<JsonDocument> EapiAsync(string path, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var cookies = await EnsureInitAsync(ct);
        var p = new Dictionary<string, object?>(payload)
        {
            ["e_r"] = true,
            ["header"] = ParamsHeader(cookies),
        };
        var respBytes = await PostEapiRawAsync(path, p, cookies, ct);
        var json = NetUtil.AesEcbDecryptText(respBytes, EapiKey);
        var doc = NetUtil.ParseJson(json);
        if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code) && code != 200)
            throw new Exception($"API 错误 code={code}");
        return doc;
    }

    public async Task<List<SongCandidate>> SearchAsync(string keyword, LocalSong song, CancellationToken ct, Action<SongCandidate>? onItem = null)
    {
        using var doc = await EapiAsync("/eapi/search/song/list/page",
            new Dictionary<string, object?>
            {
                ["keyword"] = keyword,
                ["limit"] = 20,
                ["offset"] = 0,
                ["scene"] = "NORMAL",
                ["needCorrect"] = true,
            }, ct);

        var list = new List<SongCandidate>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        if (!data.TryGetProperty("resources", out var resources)) return list;

        foreach (var res in resources.EnumerateArray())
        {
            if (!res.TryGetProperty("baseInfo", out var baseInfo)) continue;
            if (!baseInfo.TryGetProperty("simpleSongData", out var s)) continue;

            var title = NetUtil.Str(s, "name");
            var id = NetUtil.Str(s, "id");
            if (title.Length == 0 || id.Length == 0) continue;

            var artists = "";
            if (s.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array)
                artists = string.Join(" / ", ar.EnumerateArray().Select(a => NetUtil.Str(a, "name")).Where(x => x.Length > 0));
            var album = "";
            string? cover = null;
            if (s.TryGetProperty("al", out var al))
            {
                album = NetUtil.Str(al, "name");
                cover = NetUtil.Str(al, "picUrl");
            }
            var durationMs = NetUtil.Int(s, "dt") is var dt && dt > 0 ? dt : 0;

            var cand = new SongCandidate
            {
                Source = Name,
                Id = id,
                Title = title,
                Artists = artists,
                Album = album,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                CoverUrl = cover ?? "",
            };
            var score = MatchScorer.Score(song, cand);
            // 纯关键词列表：不再按本地歌曲信息淘汰，负分钳 0 仅用于排序
            cand.Score = Math.Max(score, 0);
            onItem?.Invoke(cand); // 搜到一个立即回调（流式加入列表）
            list.Add(cand);
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }

    public async Task<string?> GetLyricsAsync(SongCandidate candidate, CancellationToken ct)
    {
        using var doc = await EapiAsync("/eapi/song/lyric/v1",
            new Dictionary<string, object?>
            {
                ["id"] = candidate.Id,
                ["lv"] = -1, ["tv"] = -1, ["rv"] = -1, ["yv"] = -1,
            }, ct);

        var root = doc.RootElement;
        string? yrc = null, lrc = null;
        if (root.TryGetProperty("yrc", out var y) && y.ValueKind == JsonValueKind.Object)
            yrc = NetUtil.Str(y, "lyric");
        if (root.TryGetProperty("lrc", out var l) && l.ValueKind == JsonValueKind.Object)
            lrc = NetUtil.Str(l, "lyric");

        string result;
        if (!string.IsNullOrWhiteSpace(yrc))
        {
            result = QrcKrcToLrc.Convert(yrc);
            candidate.LyricType = "逐字";
        }
        else if (!string.IsNullOrWhiteSpace(lrc))
        {
            result = lrc.Replace("\r\n", "\n").Trim();
            candidate.LyricType = "逐行";
        }
        else
        {
            return null;
        }
        return result.Length > 0 ? result : null;
    }
}

/// <summary>QQ音乐（musicu.fcg 网关，参考 md 2.2）。</summary>
public sealed partial class QQMusicProvider : ILyricsProvider
{
    public string Name => "QQ 音乐";
    private const string Gateway = "https://u.y.qq.com/cgi-bin/musicu.fcg";

    private static Dictionary<string, string> Headers => new()
    {
        ["User-Agent"] = "okhttp/3.14.9",
        ["Cookie"] = "tmeLoginType=-1",
        ["Content-Type"] = "application/json",
    };

    private static object Comm => new
    {
        tmeAppID = "qqmusiclight",
        phonetype = "24122RKC7C",
        os_ver = "15",
        ct = 19,
        cv = 0,
        tmeLoginType = -1,
    };

    private static async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { @comm = Comm, request });
        var resp = await NetUtil.PostStringAsync(Gateway,
            new StringContent(body, Encoding.UTF8, "application/json"), Headers, ct);
        using var doc = NetUtil.ParseJson(resp);
        return doc.RootElement.GetProperty("request").Clone();
    }

    public async Task<List<SongCandidate>> SearchAsync(string keyword, LocalSong song, CancellationToken ct, Action<SongCandidate>? onItem = null)
    {
        var searchId = Random.Shared.NextInt64(100000000, 999999999);
        var req = await RequestAsync(new
        {
            method = "DoSearchForQQMusicLite",
            module = "music.search.SearchCgiService",
            param = new
            {
                query = keyword,
                search_type = 0,
                num_per_page = 20,
                page_num = 0,
                search_id = searchId,
            },
        }, ct);

        var list = new List<SongCandidate>();
        if (!req.TryGetProperty("data", out var data)) return list;
        if (!data.TryGetProperty("body", out var body)) return list;
        // 轻量版搜索返回 body.item_song[]
        if (!body.TryGetProperty("item_song", out var songs)) return list;

        foreach (var s in songs.EnumerateArray())
        {
            var title = NetUtil.Str(s, "title", "") is { Length: > 0 } t0 ? t0 : NetUtil.Str(s, "name");
            title = EmTag().Replace(title, ""); // 去掉搜索高亮 <em> 标签
            var id = NetUtil.Str(s, "id");
            var mid = NetUtil.Str(s, "mid");
            if (title.Length == 0 || (id.Length == 0 && mid.Length == 0)) continue;

            var artists = "";
            if (s.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
                artists = string.Join(" / ", singers.EnumerateArray().Select(a => NetUtil.Str(a, "name")).Where(x => x.Length > 0));
            var album = "";
            string cover = "";
            if (s.TryGetProperty("album", out var al))
            {
                album = NetUtil.Str(al, "name") is { Length: > 0 } an ? an : NetUtil.Str(al, "title");
                var albumMid = NetUtil.Str(al, "mid");
                if (albumMid.Length > 0)
                    cover = $"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg";
            }
            var interval = NetUtil.Dbl(s, "interval");

            var cand = new SongCandidate
            {
                Source = Name,
                Id = id.Length > 0 ? id : mid,
                Title = title,
                Artists = artists,
                Album = album,
                Duration = TimeSpan.FromSeconds(interval),
                CoverUrl = cover,
            };
            var score = MatchScorer.Score(song, cand);
            // 纯关键词列表：不再按本地歌曲信息淘汰，负分钳 0 仅用于排序
            cand.Score = Math.Max(score, 0);
            onItem?.Invoke(cand); // 搜到一个立即回调（流式加入列表）
            list.Add(cand);
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }

    [GeneratedRegex(@"</?em>")]
    private static partial Regex EmTag();

    public async Task<string?> GetLyricsAsync(SongCandidate candidate, CancellationToken ct)
    {
        var songId = long.TryParse(candidate.Id, out var sid) ? sid : 0;
        // 取词参数参考公开接口格式：crypt=1 请求加密数据，cv/lrc_t 等为客户端校验字段
        var req = await RequestAsync(new
        {
            method = "GetPlayLyricInfo",
            module = "music.musichallSong.PlayLyricInfo",
            param = new
            {
                albumName = Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate.Album)),
                crypt = 1,
                ct = 19,
                cv = 2111,
                interval = (int)candidate.Duration.TotalSeconds,
                lrc_t = 0,
                qrc = 1,
                qrc_t = 0,
                roma = 1,
                roma_t = 0,
                singerName = Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate.Artists)),
                songID = songId,
                songName = Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate.Title)),
                trans = 1,
                trans_t = 0,
                type = 0,
            },
        }, ct);

        if (!req.TryGetProperty("data", out var data)) return null;

        string? lyricHex = null, transHex = null;
        if (data.TryGetProperty("lyric", out var ly) && ly.ValueKind == JsonValueKind.String)
            lyricHex = ly.GetString();
        if (data.TryGetProperty("trans", out var tr) && tr.ValueKind == JsonValueKind.String)
            transHex = tr.GetString();

        if (string.IsNullOrWhiteSpace(lyricHex))
        {
            // 无逐字歌词时尝试 origin / lrc 字段（纯 LRC）
            var plain = NetUtil.Str(data, "origin") is { Length: > 0 } o ? o : NetUtil.Str(data, "lrc");
            if (plain.Length == 0) return null;
            candidate.LyricType = "逐行";
            return plain.Replace("\r\n", "\n").Trim();
        }

        // QRC：hex → 3DES → zlib → 剥离 XML 包装（QQMusicDecoder 方式）
        var plain1 = DecryptQrc(lyricHex);
        if (plain1.Length == 0) return null;
        candidate.LyricType = "逐字";

        var lrc = QrcKrcToLrc.Convert(plain1);

        // 译文：同样为加密 QRC，解密转 LRC 后按相同时间标签合并为双语
        if (!string.IsNullOrWhiteSpace(transHex))
        {
            try
            {
                var transLrc = QrcKrcToLrc.Convert(DecryptQrc(transHex));
                if (transLrc.Length > 0)
                    lrc = MergeTranslation(lrc, transLrc);
            }
            catch
            {
                // 译文解密失败不影响原文
            }
        }
        return lrc.Length > 0 ? lrc : null;
    }

    /// <summary>QQ QRC 密文(hex) → 3DES → zlib → 剥离 XML 包装，返回纯 QRC 文本。</summary>
    private static string DecryptQrc(string hex)
    {
        var cipher = Convert.FromHexString(hex.Trim());
        var zipped = NetUtil.TripleDesQrc(cipher, "!@#)(*$%123ZXC!@!@#)(NHL");
        return ExtractQrcContent(Encoding.UTF8.GetString(NetUtil.ZlibInflate(zipped)));
    }

    /// <summary>QQ 云端 QRC 解压后常为 XML 包装（LyricContent 属性内含真歌词），参考 QQMusicDecoder Helper 提取。</summary>
    internal static string ExtractQrcContent(string plain)
    {
        if (!plain.Contains("<Lyric_1"))
            return plain.Trim();
        // 属性值内引号已转义为 &quot;，第一个裸引号即结束
        var m = Regex.Match(plain, "LyricContent=\"(.*?)\"\\s*/?>", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : plain;
    }

    /// <summary>把逐行翻译按相同时间标签合并进 LRC（双语行）。</summary>
    private static string MergeTranslation(string lrc, string trans)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in trans.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^\[(\d+:\d+\.\d+)\](.+)$");
            if (m.Success && m.Groups[2].Value.Trim().Length > 0)
                map[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        if (map.Count == 0) return lrc;

        var sb = new StringBuilder();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            sb.AppendLine(raw);
            var m = Regex.Match(raw, @"^\[ (\d+:\d+\.\d+) \]|^\[(\d+:\d+\.\d+)\]");
            var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (key.Length > 0 && map.TryGetValue(key, out var tr))
                sb.AppendLine($"[{key}]{tr}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>酷狗音乐（安卓客户端签名 + KRC，参考 md 2.3）。</summary>
public sealed class KugouProvider : ILyricsProvider
{
    public string Name => "酷狗音乐";
    private const string Salt = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA";

    private static (string mid, string clienttime) Common()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (NetUtil.Md5Hex(ms.ToString()), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    /// <summary>通用签名：md5(盐 + 按 key 字典序 k=v 拼接 + 盐)。</summary>
    private static string Sign(Dictionary<string, object?> body)
    {
        var sb = new StringBuilder(Salt);
        foreach (var (k, v) in body.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v);
        sb.Append(Salt);
        return NetUtil.Md5Hex(sb.ToString());
    }

    private static Dictionary<string, string> Headers(string module) => new()
    {
        ["User-Agent"] = $"Android14-1070-11070-201-0-{module}-wifi",
        ["Content-Type"] = "application/json",
        ["x-router"] = "complexsearch",
    };

    public async Task<List<SongCandidate>> SearchAsync(string keyword, LocalSong song, CancellationToken ct, Action<SongCandidate>? onItem = null)
    {
        // 公开旧接口（无签名）；https 有 SSL 问题，用 http
        var url = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize=20";
        var resp = await NetUtil.GetStringAsync(url, ct: ct);
        using var doc = NetUtil.ParseJson(resp);

        var list = new List<SongCandidate>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        if (!data.TryGetProperty("info", out var info)) return list;

        foreach (var item in info.EnumerateArray())
        {
            var title = NetUtil.Str(item, "songname_original") is { Length: > 0 } t ? t : NetUtil.Str(item, "songname");
            var hash = NetUtil.Str(item, "hash");
            if (title.Length == 0 || hash.Length == 0) continue;

            var albumAudioId = NetUtil.Str(item, "album_audio_id") is { Length: > 0 } a ? a : NetUtil.Str(item, "audio_id");

            var cand = new SongCandidate
            {
                Source = Name,
                Id = albumAudioId.Length > 0 ? albumAudioId : hash,
                Extra = hash,
                Title = title,
                Artists = NetUtil.Str(item, "singername"),
                Album = NetUtil.Str(item, "album_name"),
                Duration = TimeSpan.FromSeconds(NetUtil.Int(item, "duration")),
                CoverUrl = "",
            };
            var score = MatchScorer.Score(song, cand);
            // 纯关键词列表：不再按本地歌曲信息淘汰，负分钳 0 仅用于排序
            cand.Score = Math.Max(score, 0);
            onItem?.Invoke(cand); // 搜到一个立即回调（流式加入列表）
            list.Add(cand);
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>酷狗取词请求：GET + 全参数签名 + 安卓客户端头（module=Lyric，接口参数为公开格式）。</summary>
    private static async Task<string> LyricGetAsync(string url, Dictionary<string, object?> bizParams, CancellationToken ct)
    {
        var mid = NetUtil.Md5Hex(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        var allParams = new Dictionary<string, object?>
        {
            ["appid"] = "3116",
            ["clientver"] = "11070",
        };
        foreach (var (k, v) in bizParams) allParams[k] = v;
        allParams["signature"] = Sign(allParams);

        var query = string.Join("&", allParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "Android14-1070-11070-201-0-Lyric-wifi",
            ["KG-Rec"] = "1",
            ["KG-RC"] = "1",
            ["KG-CLIENTTIMEMS"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["mid"] = mid,
        };
        return await NetUtil.GetStringAsync(url + "?" + query, headers, ct);
    }

    public async Task<string?> GetLyricsAsync(SongCandidate candidate, CancellationToken ct)
    {
        var durationMs = (long)candidate.Duration.TotalMilliseconds;
        var firstArtist = candidate.Artists.Split('、', '/', ',')[0].Trim();
        var keyword = $"{firstArtist} - {candidate.Title}".Trim(" -".ToCharArray());

        // 第一步：搜歌词候选（album_audio_id + duration(毫秒) + hash + keyword）
        var searchResp = await LyricGetAsync("https://lyrics.kugou.com/v1/search", new Dictionary<string, object?>
        {
            ["album_audio_id"] = candidate.Id,
            ["duration"] = durationMs,
            ["hash"] = candidate.Extra,
            ["keyword"] = keyword,
            ["lrctxt"] = "1",
            ["man"] = "no",
        }, ct);
        using var sdoc = NetUtil.ParseJson(searchResp);
        if (!sdoc.RootElement.TryGetProperty("candidates", out var candidates)) return null;
        if (candidates.GetArrayLength() == 0) return null;

        // 取第一个（服务端已按匹配度排序）
        var first = candidates[0];
        var lyricId = NetUtil.Str(first, "id");
        var accessKey = NetUtil.Str(first, "accesskey");
        if (lyricId.Length == 0) return null;

        // 第二步：下载 KRC（base64）
        var dlResp = await LyricGetAsync("http://lyrics.kugou.com/download", new Dictionary<string, object?>
        {
            ["accesskey"] = accessKey,
            ["charset"] = "utf8",
            ["client"] = "mobi",
            ["fmt"] = "krc",
            ["id"] = lyricId,
            ["ver"] = "1",
        }, ct);
        using var ddoc = NetUtil.ParseJson(dlResp);
        var content = NetUtil.Str(ddoc.RootElement, "content");
        var contentType = NetUtil.Int(ddoc.RootElement, "contenttype");
        if (content.Length == 0) return null;

        var raw = Convert.FromBase64String(content);
        if (contentType == 2)
        {
            // base64 编码的纯文本歌词
            var plainLrc = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n").Trim();
            candidate.LyricType = "逐行";
            return plainLrc.Length > 0 ? plainLrc : null;
        }

        // KRC：去 4 字节魔数 → XOR 密钥 → zlib → QRC 格式
        var key = Encoding.ASCII.GetBytes("@Gaw^2tGQ61-").Concat(new byte[] { 0xce, 0xd2 }).Concat(Encoding.ASCII.GetBytes("ni")).ToArray();
        var body2 = raw.Skip(4).Select((b, i) => (byte)(b ^ key[i % key.Length])).ToArray();
        var plain = Encoding.UTF8.GetString(NetUtil.ZlibInflate(body2));
        var lrc = QrcKrcToLrc.Convert(plain);
        candidate.LyricType = "逐字";
        return lrc.Length > 0 ? lrc : null;
    }
}

/// <summary>LRCLIB 开放接口（无加密，参考 md 2.4）。</summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Name => "LRCLIB";
    private const string Host = "https://lrclib.net";

    private static Dictionary<string, string> Headers => new()
    {
        ["User-Agent"] = "LyricsTool/1.0 (https://github.com/lyricstool)",
    };

    public async Task<List<SongCandidate>> SearchAsync(string keyword, LocalSong song, CancellationToken ct, Action<SongCandidate>? onItem = null)
    {
        var url = $"{Host}/api/search?q={Uri.EscapeDataString(keyword)}";
        var resp = await NetUtil.GetStringAsync(url, Headers, ct);
        using var doc = NetUtil.ParseJson(resp);

        var list = new List<SongCandidate>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var title = NetUtil.Str(t, "trackName");
            if (title.Length == 0) continue;
            var durationSec = NetUtil.Dbl(t, "duration");
            var synced = NetUtil.Str(t, "syncedLyrics");

            var cand = new SongCandidate
            {
                Source = Name,
                Id = NetUtil.Str(t, "id"),
                Title = title,
                Artists = NetUtil.Str(t, "artistName"),
                Album = NetUtil.Str(t, "albumName"),
                Duration = TimeSpan.FromSeconds(durationSec),
                CoverUrl = "",
                // LRCLIB 搜索结果自带歌词，直接缓存，取词无需二次请求
                LyricsText = synced.Length > 0 ? synced.Replace("\r\n", "\n").Trim() : null,
                LyricType = synced.Length > 0 ? "逐行" : "",
            };
            if (cand.LyricsText == null && NetUtil.Str(t, "plainLyrics").Length > 0)
                cand.LyricType = "纯文本";
            var score = MatchScorer.Score(song, cand);
            // 纯关键词列表：不再按本地歌曲信息淘汰，负分钳 0 仅用于排序
            cand.Score = Math.Max(score, 0);
            onItem?.Invoke(cand); // 搜到一个立即回调（流式加入列表）
            list.Add(cand);
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }

    public Task<string?> GetLyricsAsync(SongCandidate candidate, CancellationToken ct)
    {
        // 搜索阶段已缓存 syncedLyrics
        return Task.FromResult(candidate.LyricsText);
    }
}
/// <summary>
/// QQ 音乐 QRC 使用的修改版 3DES（位编号/置换与标准 DES 不同）。
/// 移植自 WXRIW/QQMusicDecoder 的 DESHelper.cs（MIT License, Copyright (c) WXRIW），
/// 结构与常量逐项对照其 C# 原版；LDDC 的 tripledes.py 亦译自同一实现。
/// </summary>
internal static class QrcTripleDes
{
    private const int EncryptMode = 1;
    private const int DecryptMode = 0;

    private static readonly int[][] Sbox =
    {
        new[]{ 14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8, 4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13 },
        new[]{ 15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5, 0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9 },
        new[]{ 10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1, 13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12 },
        new[]{ 7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9, 10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14 },
        new[]{ 2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6, 4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3 },
        new[]{ 12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8, 9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13 },
        new[]{ 4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6, 1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12 },
        new[]{ 13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2, 7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11 },
    };

    private static int Bitnum(byte[] a, int b, int c) =>
        ((a[(b / 32) * 4 + 3 - (b % 32) / 8] >> (7 - b % 8)) & 1) << c;

    private static uint BitnumIntr(uint a, int b, int c) => ((a >> (31 - b)) & 1) << c;

    private static uint BitnumIntl(uint a, int b, int c) => ((a << b) & 0x80000000) >> c;

    private static int SboxBit(int a) => (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4);

    private static (uint S0, uint S1) InitialPermutation(byte[] input)
    {
        uint s0 = (uint)(Bitnum(input, 57, 31) | Bitnum(input, 49, 30) | Bitnum(input, 41, 29) | Bitnum(input, 33, 28) |
             Bitnum(input, 25, 27) | Bitnum(input, 17, 26) | Bitnum(input, 9, 25) | Bitnum(input, 1, 24) |
             Bitnum(input, 59, 23) | Bitnum(input, 51, 22) | Bitnum(input, 43, 21) | Bitnum(input, 35, 20) |
             Bitnum(input, 27, 19) | Bitnum(input, 19, 18) | Bitnum(input, 11, 17) | Bitnum(input, 3, 16) |
             Bitnum(input, 61, 15) | Bitnum(input, 53, 14) | Bitnum(input, 45, 13) | Bitnum(input, 37, 12) |
             Bitnum(input, 29, 11) | Bitnum(input, 21, 10) | Bitnum(input, 13, 9) | Bitnum(input, 5, 8) |
             Bitnum(input, 63, 7) | Bitnum(input, 55, 6) | Bitnum(input, 47, 5) | Bitnum(input, 39, 4) |
             Bitnum(input, 31, 3) | Bitnum(input, 23, 2) | Bitnum(input, 15, 1) | Bitnum(input, 7, 0));
        uint s1 = (uint)(Bitnum(input, 56, 31) | Bitnum(input, 48, 30) | Bitnum(input, 40, 29) | Bitnum(input, 32, 28) |
             Bitnum(input, 24, 27) | Bitnum(input, 16, 26) | Bitnum(input, 8, 25) | Bitnum(input, 0, 24) |
             Bitnum(input, 58, 23) | Bitnum(input, 50, 22) | Bitnum(input, 42, 21) | Bitnum(input, 34, 20) |
             Bitnum(input, 26, 19) | Bitnum(input, 18, 18) | Bitnum(input, 10, 17) | Bitnum(input, 2, 16) |
             Bitnum(input, 60, 15) | Bitnum(input, 52, 14) | Bitnum(input, 44, 13) | Bitnum(input, 36, 12) |
             Bitnum(input, 28, 11) | Bitnum(input, 20, 10) | Bitnum(input, 12, 9) | Bitnum(input, 4, 8) |
             Bitnum(input, 62, 7) | Bitnum(input, 54, 6) | Bitnum(input, 46, 5) | Bitnum(input, 38, 4) |
             Bitnum(input, 30, 3) | Bitnum(input, 22, 2) | Bitnum(input, 14, 1) | Bitnum(input, 6, 0));
        return (s0, s1);
    }

    private static byte[] InversePermutation(uint s0, uint s1)
    {
        var data = new byte[8];
        data[3] = (byte)(BitnumIntr(s1, 7, 7) | BitnumIntr(s0, 7, 6) | BitnumIntr(s1, 15, 5) |
                   BitnumIntr(s0, 15, 4) | BitnumIntr(s1, 23, 3) | BitnumIntr(s0, 23, 2) |
                   BitnumIntr(s1, 31, 1) | BitnumIntr(s0, 31, 0));
        data[2] = (byte)(BitnumIntr(s1, 6, 7) | BitnumIntr(s0, 6, 6) | BitnumIntr(s1, 14, 5) |
                   BitnumIntr(s0, 14, 4) | BitnumIntr(s1, 22, 3) | BitnumIntr(s0, 22, 2) |
                   BitnumIntr(s1, 30, 1) | BitnumIntr(s0, 30, 0));
        data[1] = (byte)(BitnumIntr(s1, 5, 7) | BitnumIntr(s0, 5, 6) | BitnumIntr(s1, 13, 5) |
                   BitnumIntr(s0, 13, 4) | BitnumIntr(s1, 21, 3) | BitnumIntr(s0, 21, 2) |
                   BitnumIntr(s1, 29, 1) | BitnumIntr(s0, 29, 0));
        data[0] = (byte)(BitnumIntr(s1, 4, 7) | BitnumIntr(s0, 4, 6) | BitnumIntr(s1, 12, 5) |
                   BitnumIntr(s0, 12, 4) | BitnumIntr(s1, 20, 3) | BitnumIntr(s0, 20, 2) |
                   BitnumIntr(s1, 28, 1) | BitnumIntr(s0, 28, 0));
        data[7] = (byte)(BitnumIntr(s1, 3, 7) | BitnumIntr(s0, 3, 6) | BitnumIntr(s1, 11, 5) |
                   BitnumIntr(s0, 11, 4) | BitnumIntr(s1, 19, 3) | BitnumIntr(s0, 19, 2) |
                   BitnumIntr(s1, 27, 1) | BitnumIntr(s0, 27, 0));
        data[6] = (byte)(BitnumIntr(s1, 2, 7) | BitnumIntr(s0, 2, 6) | BitnumIntr(s1, 10, 5) |
                   BitnumIntr(s0, 10, 4) | BitnumIntr(s1, 18, 3) | BitnumIntr(s0, 18, 2) |
                   BitnumIntr(s1, 26, 1) | BitnumIntr(s0, 26, 0));
        data[5] = (byte)(BitnumIntr(s1, 1, 7) | BitnumIntr(s0, 1, 6) | BitnumIntr(s1, 9, 5) |
                   BitnumIntr(s0, 9, 4) | BitnumIntr(s1, 17, 3) | BitnumIntr(s0, 17, 2) |
                   BitnumIntr(s1, 25, 1) | BitnumIntr(s0, 25, 0));
        data[4] = (byte)(BitnumIntr(s1, 0, 7) | BitnumIntr(s0, 0, 6) | BitnumIntr(s1, 8, 5) |
                   BitnumIntr(s0, 8, 4) | BitnumIntr(s1, 16, 3) | BitnumIntr(s0, 16, 2) |
                   BitnumIntr(s1, 24, 1) | BitnumIntr(s0, 24, 0));
        return data;
    }

    private static uint F(uint state, int[] key)
    {
        uint t1 = (BitnumIntl(state, 31, 0) | ((state & 0xf0000000) >> 1) | BitnumIntl(state, 4, 5) |
              BitnumIntl(state, 3, 6) | ((state & 0x0f000000) >> 3) | BitnumIntl(state, 8, 11) |
              BitnumIntl(state, 7, 12) | ((state & 0x00f00000) >> 5) | BitnumIntl(state, 12, 17) |
              BitnumIntl(state, 11, 18) | ((state & 0x000f0000) >> 7) | BitnumIntl(state, 16, 23));
        uint t2 = (BitnumIntl(state, 15, 0) | ((state & 0x0000f000) << 15) | BitnumIntl(state, 20, 5) |
              BitnumIntl(state, 19, 6) | ((state & 0x00000f00) << 13) | BitnumIntl(state, 24, 11) |
              BitnumIntl(state, 23, 12) | ((state & 0x000000f0) << 11) | BitnumIntl(state, 28, 17) |
              BitnumIntl(state, 27, 18) | ((state & 0x0000000f) << 9) | BitnumIntl(state, 0, 23));

        var lrg = new byte[]
        {
            (byte)((t1 >> 24) & 0xff), (byte)((t1 >> 16) & 0xff), (byte)((t1 >> 8) & 0xff),
            (byte)((t2 >> 24) & 0xff), (byte)((t2 >> 16) & 0xff), (byte)((t2 >> 8) & 0xff),
        };
        for (var i = 0; i < 6; i++) lrg[i] ^= (byte)key[i];

        uint s = ((uint)Sbox[0][SboxBit(lrg[0] >> 2)] << 28) |
                 ((uint)Sbox[1][SboxBit(((lrg[0] & 0x03) << 4) | (lrg[1] >> 4))] << 24) |
                 ((uint)Sbox[2][SboxBit(((lrg[1] & 0x0f) << 2) | (lrg[2] >> 6))] << 20) |
                 ((uint)Sbox[3][SboxBit(lrg[2] & 0x3f)] << 16) |
                 ((uint)Sbox[4][SboxBit(lrg[3] >> 2)] << 12) |
                 ((uint)Sbox[5][SboxBit(((lrg[3] & 0x03) << 4) | (lrg[4] >> 4))] << 8) |
                 ((uint)Sbox[6][SboxBit(((lrg[4] & 0x0f) << 2) | (lrg[5] >> 6))] << 4) |
                 (uint)Sbox[7][SboxBit(lrg[5] & 0x3f)];

        return (BitnumIntl(s, 15, 0) | BitnumIntl(s, 6, 1) | BitnumIntl(s, 19, 2) |
                BitnumIntl(s, 20, 3) | BitnumIntl(s, 28, 4) | BitnumIntl(s, 11, 5) |
                BitnumIntl(s, 27, 6) | BitnumIntl(s, 16, 7) | BitnumIntl(s, 0, 8) |
                BitnumIntl(s, 14, 9) | BitnumIntl(s, 22, 10) | BitnumIntl(s, 25, 11) |
                BitnumIntl(s, 4, 12) | BitnumIntl(s, 17, 13) | BitnumIntl(s, 30, 14) |
                BitnumIntl(s, 9, 15) | BitnumIntl(s, 1, 16) | BitnumIntl(s, 7, 17) |
                BitnumIntl(s, 23, 18) | BitnumIntl(s, 13, 19) | BitnumIntl(s, 31, 20) |
                BitnumIntl(s, 26, 21) | BitnumIntl(s, 2, 22) | BitnumIntl(s, 8, 23) |
                BitnumIntl(s, 18, 24) | BitnumIntl(s, 12, 25) | BitnumIntl(s, 29, 26) |
                BitnumIntl(s, 5, 27) | BitnumIntl(s, 21, 28) | BitnumIntl(s, 10, 29) |
                BitnumIntl(s, 3, 30) | BitnumIntl(s, 24, 31));
    }

    private static byte[] Crypt(byte[] input, int[][] key)
    {
        var (s0, s1) = InitialPermutation(input);
        for (var idx = 0; idx < 15; idx++)
        {
            var prev = s1;
            s1 = F(s1, key[idx]) ^ s0;
            s0 = prev;
        }
        s0 = F(s1, key[15]) ^ s0;
        return InversePermutation(s0, s1);
    }

    private static int[][] KeySchedule(byte[] key, int mode)
    {
        var schedule = new int[16][];
        for (var i = 0; i < 16; i++) schedule[i] = new int[6];
        int[] keyRndShift = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };
        int[] keyPermC = { 56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35 };
        int[] keyPermD = { 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3 };
        int[] keyCompression = { 13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31 };

        // 28 位 C/D 寄存器必须用 uint：int 右移会算术符号扩展，污染旋转结果（QRC 解密失败的根因之一）
        uint c = 0;
        uint d = 0;
        for (var i = 0; i < 28; i++)
        {
            c |= (uint)Bitnum(key, keyPermC[i], 31 - i);
            d |= (uint)Bitnum(key, keyPermD[i], 31 - i);
        }

        for (var i = 0; i < 16; i++)
        {
            c = ((c << keyRndShift[i]) | (c >> (28 - keyRndShift[i]))) & 0xfffffff0u;
            d = ((d << keyRndShift[i]) | (d >> (28 - keyRndShift[i]))) & 0xfffffff0u;
            var togen = mode == DecryptMode ? 15 - i : i;
            for (var j = 0; j < 6; j++) schedule[togen][j] = 0;
            for (var j = 0; j < 24; j++) schedule[togen][j / 8] |= (int)BitnumIntr(c, keyCompression[j], 7 - (j % 8));
            for (var j = 24; j < 48; j++) schedule[togen][j / 8] |= (int)BitnumIntr(d, keyCompression[j] - 27, 7 - (j % 8));
        }
        return schedule;
    }

    private static int[][][] KeySetup(byte[] key, int mode)
    {
        if (mode == EncryptMode)
            return new[] { KeySchedule(key, EncryptMode), KeySchedule(key.AsSpan(8).ToArray(), DecryptMode), KeySchedule(key.AsSpan(16).ToArray(), EncryptMode) };
        return new[] { KeySchedule(key.AsSpan(16).ToArray(), DecryptMode), KeySchedule(key.AsSpan(8).ToArray(), EncryptMode), KeySchedule(key, DecryptMode) };
    }

    private static byte[] Crypt3(byte[] data, int[][][] schedule)
    {
        for (var i = 0; i < 3; i++) data = Crypt(data, schedule[i]);
        return data;
    }

    public static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        var result = new byte[encrypted.Length];
        var schedule = KeySetup(key, DecryptMode);
        for (var i = 0; i < encrypted.Length; i += 8)
        {
            var block = new byte[8];
            Array.Copy(encrypted, i, block, 0, Math.Min(8, encrypted.Length - i));
            var dec = Crypt3(block, schedule);
            Array.Copy(dec, 0, result, i, 8);
        }
        return result;
    }
}
