using System;
using System.Collections.Generic;
using System.Numerics;
using LyricsApp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;

namespace LyricsApp.WinUI.Controls;

/// <summary>
/// Win2D KTV 逐字染色歌词画布：当前行按字符时间轴渐变染色，其余行灰色，行变化时请求滚动。
/// CanvasControl 为 sealed，故用 Grid 组合内嵌实现。
/// </summary>
public sealed class LyricsCanvas : Grid
{
    private const float ItemHeight = 36f;
    public const float RowHeight = 36f;
    private const float SidePadding = 6f;
    // 翻译/发音子行（仅当前播放行展开显示）：字号小一号、行距紧凑，块与下一行间距稍大
    private const float SubOffset = 34f;      // 子行相对主行 top 的起始偏移
    private const float SubLineHeight = 22f;  // 每个子行高度
    private const float BlockGap = 10f;       // 展开块与下一行的额外间距
    private const float SubFontSize = 12f;    // 子行字号（主行 14/15）

    private readonly CanvasControl _canvas = new();
    private Windows.UI.Color _clearColor = Microsoft.UI.Colors.White;
    private List<LrcLine> _lines = new();
    private int _currentLine = -1;
    private TimeSpan _position;
    private float[] _tops = Array.Empty<float>();  // 每行 top 坐标（当前行展开时动态变化）

    private readonly CanvasTextFormat _formatNormal = new()
    {
        FontFamily = "Microsoft YaHei UI",
        FontSize = 14f,
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
    };
    private readonly CanvasTextFormat _formatHot = new()
    {
        FontFamily = "Microsoft YaHei UI",
        FontSize = 15f,  // 比未播放行（14）大 1 号
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
    };

    private CanvasTextLayout? _hotLayout;      // 当前行布局（缓存，行变化/宽度变化时重建）
    private float _hotLayoutWidth;
    private int[] _hotUnitOffsets = Array.Empty<int>();          // 每个单字符单元在带空格文本中的起始偏移
    private (int Start, int End)[] _hotCharRanges = Array.Empty<(int, int)>();  // 每个 KaraokeChar 覆盖的单元区间 [start,end)
    private int _hotSpacedLength;                                // 带空格文本总长
    private float _viewportOffset;
    private float _viewportHeight = 400f;

    private static readonly Color ColorIdle = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color ColorHot = Color.FromArgb(255, 0, 120, 215);

    /// <summary>当前行索引变化（含初始化为 -1→N）时触发，供外部滚动定位。</summary>
    public event Action<int>? CurrentLineChanged;

    /// <summary>点击某行歌词时触发（行索引），供外部跳转播放。</summary>
    public event Action<int>? LineClicked;

    /// <summary>画布清屏色（默认白色；悬浮层可设为透明以融入面板背景）。</summary>
    public Windows.UI.Color ClearBackground
    {
        get => _clearColor;
        set { _clearColor = value; _canvas.ClearColor = value; }
    }

    public LyricsCanvas()
    {
        _canvas.ClearColor = _clearColor;
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnCanvasPointerPressed;
        Children.Add(_canvas);
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(_canvas).Position.Y;
        // 行高不均匀（当前行展开子行），按 _tops 反查行索引
        var idx = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (y >= _tops[i] && y < _tops[i + 1]) { idx = i; break; }
        }
        if (idx >= 0 && idx < _lines.Count)
            LineClicked?.Invoke(idx);
    }

    /// <summary>由外部 ScrollViewer 同步可视区域，Draw 只绘制可见行。</summary>
    public void SetViewport(float offset, float viewportHeight)
    {
        _viewportOffset = offset;
        _viewportHeight = viewportHeight <= 0 ? 400f : viewportHeight;
    }

    public void SetData(List<LrcLine> lines)
    {
        _lines = lines;
        _currentLine = -1;
        _hotLayout = null;
        RebuildLayout();
        CurrentLineChanged?.Invoke(-1);
        _canvas.Invalidate();
    }

    /// <summary>播放位置驱动：内部定位当前行并请求重绘。</summary>
    public void SetPosition(TimeSpan position)
    {
        _position = position;
        var idx = FindCurrentLine(position);
        if (idx != _currentLine)
        {
            _currentLine = idx;
            _hotLayout = null;
            RebuildLayout(); // 当前行展开子行会改变布局
            CurrentLineChanged?.Invoke(idx);
        }
        _canvas.Invalidate();
    }

    /// <summary>某行在画布中的 top 坐标（供外部滚动定位，替代 idx * RowHeight）。</summary>
    public float GetLineTop(int idx) => idx >= 0 && idx < _tops.Length ? _tops[idx] : 0;

    // 行占用高度：普通行 36；当前行展开子行时 = 36 + 子行区高度 + 块间距
    private float RowSpan(int i)
    {
        if (i != _currentLine || i < 0 || i >= _lines.Count) return ItemHeight;
        var line = _lines[i];
        if (!line.HasSubLines) return ItemHeight;
        var subCount = (line.Translation != null ? 1 : 0) + (line.Romanization != null ? 1 : 0);
        return SubOffset + subCount * SubLineHeight + BlockGap;
    }

    private void RebuildLayout()
    {
        _tops = new float[_lines.Count + 1];
        for (var i = 0; i < _lines.Count; i++)
            _tops[i + 1] = _tops[i] + RowSpan(i);
        Height = Math.Max(1, _tops[^1]);
    }

    private int FindCurrentLine(TimeSpan pos)
    {
        int lo = 0, hi = _lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[mid].Time <= pos)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(_clearColor);
        if (_lines.Count == 0) return;

        var width = (float)sender.ActualWidth;
        if (width < 20) return;

        using var idleBrush = new CanvasSolidColorBrush(sender, ColorIdle);
        using var hotBrush = new CanvasSolidColorBrush(sender, ColorHot);

        // 按动态行高（_tops）绘制可视窗口内的行；当前行展开翻译/发音子行
        var viewBottom = _viewportOffset + _viewportHeight;
        for (var i = 0; i < _lines.Count; i++)
        {
            var top = _tops[i];
            var span = _tops[i + 1] - top;
            if (top + span < _viewportOffset) continue;
            if (top > viewBottom) break;
            var rect = new Rect(SidePadding, top, width - SidePadding * 2, ItemHeight);

            if (i != _currentLine)
            {
                ds.DrawText(_lines[i].Text, rect, idleBrush,
                    new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = 14f });
                continue;
            }

            DrawCurrentLine(sender, ds, _lines[i], rect, idleBrush, hotBrush);

            // 子行：翻译/发音整句小一号显示（未播放到的行不显示）
            var line = _lines[i];
            var subY = top + SubOffset;
            if (line.Translation != null)
            {
                ds.DrawText(line.Translation,
                    new Rect(SidePadding + 4, subY, width - SidePadding * 2 - 4, SubLineHeight),
                    idleBrush,
                    new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = SubFontSize });
                subY += SubLineHeight;
            }
            if (line.Romanization != null)
            {
                ds.DrawText(line.Romanization,
                    new Rect(SidePadding + 4, subY, width - SidePadding * 2 - 4, SubLineHeight),
                    idleBrush,
                    new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = SubFontSize });
            }
        }
    }

    // 当前行：灰色整行 + 蓝色已唱部分（Layer 裁剪 + 字内渐变）
    private void DrawCurrentLine(CanvasControl sender, CanvasDrawingSession ds, LrcLine line,
        Rect rect, CanvasSolidColorBrush idle, CanvasSolidColorBrush hot)
    {
        var widthPx = (float)rect.Width;

        EnsureHotLayout(sender, line, widthPx);
        if (_hotLayout == null) return;

        // 整行灰色
        ds.DrawTextLayout(_hotLayout, new Vector2((float)rect.X, (float)rect.Y), idle);

        // 已唱宽度：整行已过 → 全宽；行内按字
        var progressPx = ComputeProgressWidth(line, widthPx);
        if (progressPx <= 0) return;
        if (progressPx > widthPx) progressPx = widthPx;

        var original = ds.Transform;
        ds.Transform = Matrix3x2.Multiply(Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y), original);
        using (ds.CreateLayer(1.0f, new Rect(0, 0, progressPx, rect.Height)))
        {
            ds.DrawTextLayout(_hotLayout, new Vector2(0, 0), hot);
        }
        ds.Transform = original;
    }

    private void EnsureHotLayout(CanvasControl sender, LrcLine line, float widthPx)
    {
        if (_hotLayout != null && _hotLayoutWidth == widthPx) return;
        _hotLayout?.Dispose();

        // 把每个 KaraokeChar 展开为单字符单元，相邻字符之间都插一个空格
        //（网络歌词最后两个字可能共用一个时间标签，按整体渲染会漏空格，必须拆到单字符）
        var units = new List<string>();
        var unitOffsets = new List<int>();
        var ranges = new List<(int Start, int End)>();
        var offset = 0;
        foreach (var c in line.Chars)
        {
            var start = units.Count;
            foreach (var ch in c.Text)
            {
                unitOffsets.Add(offset);
                units.Add(ch.ToString());
                offset += 2; // 字符本身 + 其后空格
            }
            ranges.Add((start, units.Count));
        }
        var spaced = string.Join(" ", units);
        _hotSpacedLength = spaced.Length;

        _hotLayout = new CanvasTextLayout(sender, spaced, _formatHot, widthPx, ItemHeight)
        {
            Options = CanvasDrawTextOptions.Default,
        };
        _hotLayoutWidth = widthPx;
        _hotUnitOffsets = unitOffsets.ToArray();
        _hotCharRanges = ranges.ToArray();
    }

    /// <summary>按字符时间轴计算已染色像素宽度（最后一个字内做线性渐变）。</summary>
    private float ComputeProgressWidth(LrcLine line, float widthPx)
    {
        if (_hotLayout == null) return 0;

        var chars = line.Chars;
        var n = chars.Count;
        var index = -1;
        for (var i = 0; i < n; i++)
        {
            if (_position >= chars[i].Time)
                index = i;
            else
                break;
        }
        if (index < 0) return 0;

        // 字符起点 x（基于带空格的单字符单元渲染文本）
        var startX = 0f;
        var endX = widthPx;
        var fraction = 1.0;

        if (index < _hotCharRanges.Length)
        {
            var (uStart, uEnd) = _hotCharRanges[index];
            if (uStart < uEnd)
            {
                var p0 = _hotLayout.GetCaretPosition(_hotUnitOffsets[uStart], false);
                // 该字结束位置：下一个单元的前导空格左边界；若是最后一个单元则为文本末尾
                var endCaret = uEnd < _hotUnitOffsets.Length ? _hotUnitOffsets[uEnd] - 1 : _hotSpacedLength;
                var p1 = _hotLayout.GetCaretPosition(endCaret, false);
                startX = p0.X;
                endX = p1.X;
            }
            else return 0;
        }

        var ch = chars[index];
        if (ch.Duration > TimeSpan.Zero && _position < ch.Time + ch.Duration)
            fraction = (_position - ch.Time).TotalMilliseconds / ch.Duration.TotalMilliseconds;

        return (float)(startX + (endX - startX) * Math.Clamp(fraction, 0, 1));
    }
}
