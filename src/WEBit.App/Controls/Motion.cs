using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace WEBit.App.Controls;

/// <summary>Short, subtle entrance animations (fade + slide) for pages, tabs and notifications.</summary>
public static class Motion
{
    // One running animation per control: a new one replaces it (no jitter on quick repeats).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();

    public static void FadeIn(Visual? target, double offsetX = 0, double offsetY = 10, int milliseconds = 200)
    {
        if (target is not Animatable animatable)
        {
            return;
        }

        if (Running.TryGetValue(target, out var previous))
        {
            previous.Cancel();
            Running.Remove(target);
        }

        var cts = new CancellationTokenSource();
        Running.Add(target, cts);

        target.RenderTransform ??= new TranslateTransform();
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(TranslateTransform.XProperty, offsetX),
                        new Setter(TranslateTransform.YProperty, offsetY)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(TranslateTransform.XProperty, 0d),
                        new Setter(TranslateTransform.YProperty, 0d)
                    }
                }
            }
        };

        _ = animation.RunAsync(animatable, cts.Token);
    }
}
