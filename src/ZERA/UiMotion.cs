using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZERA
{
    /// <summary>Short, render-only transitions on dedicated template surfaces.</summary>
    public static class UiMotion
    {
        public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
            "Interactive", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnInteractiveChanged));
        public static bool GetInteractive(DependencyObject target) => (bool)target.GetValue(InteractiveProperty);
        public static void SetInteractive(DependencyObject target, bool value) => target.SetValue(InteractiveProperty, value);

        public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
            "Reveal", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnRevealChanged));
        public static bool GetReveal(DependencyObject target) => (bool)target.GetValue(RevealProperty);
        public static void SetReveal(DependencyObject target, bool value) => target.SetValue(RevealProperty, value);

        private static DoubleAnimation Transition(double to, int milliseconds)
        {
            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(
                SystemParameters.ClientAreaAnimation ? milliseconds : 0))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animation, 60);
            return animation;
        }

        private static void OnInteractiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is FrameworkElement surface)) return;
            if ((bool)e.NewValue)
            {
                // Only use on new template roots; never replace a control's content transform.
                surface.RenderTransformOrigin = new Point(.5, .5);
                surface.RenderTransform = new ScaleTransform(1, 1);
                surface.MouseEnter += Hover;
                surface.MouseLeave += Leave;
                surface.PreviewMouseLeftButtonDown += Press;
                surface.PreviewMouseLeftButtonUp += Release;
                surface.LostMouseCapture += Release;
                surface.IsEnabledChanged += EnabledChanged;
                surface.Unloaded += Reset;
            }
            else
            {
                surface.MouseEnter -= Hover;
                surface.MouseLeave -= Leave;
                surface.PreviewMouseLeftButtonDown -= Press;
                surface.PreviewMouseLeftButtonUp -= Release;
                surface.LostMouseCapture -= Release;
                surface.IsEnabledChanged -= EnabledChanged;
                surface.Unloaded -= Reset;
                Scale(surface, 1, 0);
            }
        }

        private static void Scale(FrameworkElement surface, double value, int duration)
        {
            if (!(surface.RenderTransform is ScaleTransform scale)) return;
            if (!SystemParameters.ClientAreaAnimation) value = 1;
            var x = Transition(value, duration); x.From = scale.ScaleX;
            var y = Transition(value, duration); y.From = scale.ScaleY;
            x.Completed += (s, e) => scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            y.Completed += (s, e) => scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = value; scale.ScaleY = value;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, x, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, y, HandoffBehavior.SnapshotAndReplace);
        }

        private static void Hover(object sender, MouseEventArgs e) => Scale((FrameworkElement)sender, 1.012, 160);
        private static void Leave(object sender, MouseEventArgs e) => Scale((FrameworkElement)sender, 1, 180);
        private static void Press(object sender, MouseButtonEventArgs e) => Scale((FrameworkElement)sender, .975, 90);
        private static void Release(object sender, MouseEventArgs e)
        {
            var surface = (FrameworkElement)sender;
            Scale(surface, surface.IsMouseOver && surface.IsEnabled ? 1.012 : 1, 180);
        }
        private static void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Scale((FrameworkElement)sender, 1, 0);
        private static void Reset(object sender, RoutedEventArgs e) => Scale((FrameworkElement)sender, 1, 0);

        private static void OnRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is FrameworkElement surface)) return;
            if ((bool)e.NewValue)
            {
                surface.IsVisibleChanged += Reveal;
                surface.Unloaded += ClearReveal;
            }
            else
            {
                surface.IsVisibleChanged -= Reveal;
                surface.Unloaded -= ClearReveal;
                surface.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }

        private static void Reveal(object sender, DependencyPropertyChangedEventArgs e)
        {
            var surface = (FrameworkElement)sender;
            if (surface.IsVisible && SystemParameters.ClientAreaAnimation)
            {
                var animation = Transition(1, 190); animation.From = .45;
                animation.Completed += (s, args) => surface.BeginAnimation(UIElement.OpacityProperty, null);
                surface.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            else surface.BeginAnimation(UIElement.OpacityProperty, null);
        }
        private static void ClearReveal(object sender, RoutedEventArgs e) => ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);
    }
}
