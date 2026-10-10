using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GreenLuma_Manager.Utilities;

/// <summary>
/// Smoothly scrolls a <see cref="ScrollViewer"/> so a descendant element becomes visible,
/// animating the vertical offset instead of jumping.
/// </summary>
public static class SmoothScroll
{
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedOffset",
            typeof(double),
            typeof(SmoothScroll),
            new FrameworkPropertyMetadata(0d, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    /// <summary>Animates the nearest ancestor ScrollViewer to reveal the given element.</summary>
    public static void BringIntoView(FrameworkElement? element)
    {
        if (element == null)
            return;

        var scrollViewer = FindAncestor<ScrollViewer>(element);
        if (scrollViewer == null)
        {
            element.BringIntoView();
            return;
        }

        double position;
        try
        {
            position = element.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        const double margin = 24;
        var target = scrollViewer.VerticalOffset;
        var elementHeight = element.ActualHeight;
        var viewportHeight = scrollViewer.ViewportHeight;

        if (position < margin)
            target = scrollViewer.VerticalOffset + position - margin;
        else if (position + elementHeight > viewportHeight - margin)
            target = scrollViewer.VerticalOffset + position + elementHeight - viewportHeight + margin;

        target = Math.Max(0, Math.Min(target, scrollViewer.ScrollableHeight));

        var animation = new DoubleAnimation
        {
            From = scrollViewer.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        scrollViewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
