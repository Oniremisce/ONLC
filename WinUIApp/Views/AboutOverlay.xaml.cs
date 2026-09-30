using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace LyricsApp.WinUI.Views;

/// <summary>
/// 关于悬浮层：软件信息 + slogan + 开源信息 + 仓库/更新/反馈链接。
/// 实现方式与其他悬浮层一致（遮罩 + 居中面板 + 开关动画）。
/// </summary>
public sealed partial class AboutOverlay : UserControl
{
    private const string RepoUrl = "https://github.com/Oniremisce/ONLC";
    private const string ReleaseUrl = "https://github.com/Oniremisce/ONLC/releases/latest";
    private const string IssueUrl = "https://github.com/Oniremisce/ONLC/issues/new";

    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;

    /// <summary>关闭时完成。</summary>
    public Task<bool> Completion => _completion.Task;

    public AboutOverlay()
    {
        InitializeComponent();

        // 版本号：csproj InformationalVersion（V1.00）
        var ver = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = $"版本：{(string.IsNullOrEmpty(ver) ? "V1.00" : ver)}";

        // 软件图标（icon.png 随构建复制到输出目录）
        var png = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(png))
            IconImage.Source = new BitmapImage(new Uri(png));
    }

    public void Show()
    {
        Visibility = Visibility.Visible;
        var sb = new Storyboard();
        AddFade(sb, MaskBorder, 0, 1, 180, null);
        AddFade(sb, PanelBorder, 0, 1, 220, EasingMode.EaseOut);
        AddSlide(sb, PanelTransform, 48, 0, 220, EasingMode.EaseOut);
        sb.Begin();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void Close()
    {
        if (_closing) return;
        _closing = true;
        await PlayHideAnimationAsync();
        _completion.TrySetResult(true);
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
        Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
    }

    // 链接按钮：跳转系统默认浏览器
    private void OnRepoClick(object sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(RepoUrl));
    private void OnUpdateClick(object sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(ReleaseUrl));
    private void OnIssueClick(object sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(IssueUrl));
}
