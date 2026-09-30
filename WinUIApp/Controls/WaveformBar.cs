using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NAudio.Wave;
using Windows.UI;

namespace LyricsApp.WinUI.Controls;

/// <summary>
/// 音频波形进度条：后台解码整首歌计算峰值包络，对称条形渲染；
/// 已播放部分绿色、未播放灰色，黄色竖线+三角为把柄；
/// 点击/拖动把柄从对应位置播放（按下即 Seek 并播放，松开落到最终位置）。
/// CanvasControl 为 sealed，与 LyricsCanvas 同样用 Grid 组合内嵌实现。
/// </summary>
public sealed class WaveformBar : Grid
{
    private const int BucketCount = 800;
    private static readonly Color ColorPlayed = Color.FromArgb(255, 0x35, 0xC9, 0x7E);
    private static readonly Color ColorIdle = Color.FromArgb(255, 0xC2, 0xCB, 0xD4);
    private static readonly Color ColorCenter = Color.FromArgb(255, 0x9A, 0xA5, 0xB1);
    private static readonly Color ColorHandle = Color.FromArgb(255, 0xF0, 0xB4, 0x29);

    private readonly CanvasControl _canvas = new();
    private float[] _peaks = [];
    private double _progress; // 播放进度 0~1
    private double _preview;  // 拖动中的预览位置 0~1
    private bool _dragging;

    /// <summary>按下时触发（按下即 Seek+播放），参数 0~1。</summary>
    public event Action<double>? SeekPressed;

    /// <summary>松开时触发（最终位置），参数 0~1。</summary>
    public event Action<double>? SeekReleased;

    public WaveformBar()
    {
        Height = 72;
        Children.Add(_canvas);
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += OnReleased;
        Unloaded += OnUnloaded;
    }

    // ===== 数据 =====

    /// <summary>后台解码整首歌并计算波形峰值；完成后自动重绘。</summary>
    public async Task LoadAsync(string? file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
        var peaks = await Task.Run(() => ComputePeaks(file));
        _peaks = peaks;
        Invalidate();
    }

    // MediaFoundation 解码 → 求绝对峰值 → BucketCount 桶 → 按最大值归一到 0~1
    private static float[] ComputePeaks(string file)
    {
        var peaks = new float[BucketCount];
        try
        {
            using var reader = new MediaFoundationReader(file);
            var sp = reader.ToSampleProvider();
            var channels = sp.WaveFormat.Channels;
            var total = (long)(reader.TotalTime.TotalSeconds * sp.WaveFormat.SampleRate) * channels;
            if (total <= 0) return peaks;
            var per = Math.Max(1, total / BucketCount);
            var buf = new float[sp.WaveFormat.SampleRate * channels]; // 1 秒缓冲
            long idx = 0;
            int read;
            while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var b = (int)Math.Min(BucketCount - 1, idx / per);
                    var a = Math.Abs(buf[i]);
                    if (a > peaks[b]) peaks[b] = a;
                    idx++;
                }
            }
            var max = 0f;
            foreach (var p in peaks)
                if (p > max) max = p;
            if (max > 0)
                for (var i = 0; i < BucketCount; i++) peaks[i] /= max;
        }
        catch
        {
            // 解码失败显示空波形，不影响进度控制功能
        }
        return peaks;
    }

    /// <summary>同步播放进度（0~1），由外部定时器驱动。</summary>
    public void SetProgress(double ratio)
    {
        _progress = Math.Clamp(ratio, 0, 1);
        Invalidate();
    }

    private void Invalidate() => _canvas.Invalidate();

    // ===== 绘制 =====
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var w = (float)sender.ActualWidth;
        var h = (float)sender.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ds.Clear(Microsoft.UI.Colors.Transparent);

        var ratio = _dragging ? _preview : _progress;
        var cutX = (float)(ratio * w);
        var mid = h / 2;

        ds.DrawLine(0, mid, w, mid, ColorCenter);

        var n = _peaks.Length;
        if (n > 0)
        {
            var barW = w / n;
            var drawW = Math.Max(1f, barW - 1f);
            for (var i = 0; i < n; i++)
            {
                var x = i * barW;
                var amp = _peaks[i] * (mid - 4);
                if (amp < 1f) amp = 1f;
                ds.FillRectangle(x, mid - amp, drawW, amp * 2, x + drawW <= cutX ? ColorPlayed : ColorIdle);
            }
        }

        // 把柄：竖线 + 底部三角
        ds.FillRectangle(cutX - 1.5f, 0, 3, h, ColorHandle);
        using var path = CanvasGeometry.CreatePolygon(sender, new[]
        {
            new Vector2(cutX - 6, h),
            new Vector2(cutX + 6, h),
            new Vector2(cutX, h - 8),
        });
        ds.FillGeometry(path, ColorHandle);
    }

    // ===== 交互 =====
    private double RatioOf(PointerRoutedEventArgs e) =>
        Math.Clamp(e.GetCurrentPoint(_canvas).Position.X / Math.Max(1, _canvas.ActualWidth), 0, 1);

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _preview = RatioOf(e);
        _canvas.CapturePointer(e.Pointer);
        SeekPressed?.Invoke(_preview);
        Invalidate();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _preview = RatioOf(e);
        Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _preview = RatioOf(e);
        _canvas.ReleasePointerCapture(e.Pointer);
        SeekReleased?.Invoke(_preview);
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _canvas.RemoveFromVisualTree();
    }
}
