namespace LyricsApp;

/// <summary>
/// 应用级共享播放器：主窗口与匹配歌词窗口控制同一个播放流。
/// 各窗口 UI 通过定时器轮询 Engine 状态保持同步。
/// </summary>
public sealed class SharedPlayer
{
    public static SharedPlayer Instance { get; } = new();
    public PlayerEngine Engine { get; } = new();

    /// <summary>当前已加载的文件；相同文件不重复加载，避免打断播放。</summary>
    public string? CurrentFile { get; private set; }

    private SharedPlayer() { }

    public void LoadFile(string file)
    {
        if (CurrentFile == file) return;
        Engine.Load(file);
        CurrentFile = file;
    }
}
