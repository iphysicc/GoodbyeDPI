using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GoodbyeDPI.Panel
{
    /// <summary>Three dots lighting up in turn: the busy state inside a button.</summary>
    public sealed class LoadingDots : StackPanel
    {
        public LoadingDots(Brush fill)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            for (int i = 0; i < 3; i++)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = fill, Margin = new Thickness(2.5, 0, 2.5, 0), Opacity = 0.3 };
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(900),
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                double start = i * 0.15;
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.15))));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.4))));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
                dot.BeginAnimation(OpacityProperty, anim);
                Children.Add(dot);
            }
        }
    }
}
