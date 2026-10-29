using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace TrayTraffic.Controls;

/// <summary>
/// Small, consistent motion for flyout content, so parts of the view fade and slide instead of
/// popping. Honors the Windows "Animation effects" setting.
/// </summary>
internal static class Motion
{
    private const float SlideDistance = -8;
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly UISettings Settings = new();

    public static bool Enabled => Settings.AnimationsEnabled;

    /// <summary>Fades and slides an element in and out whenever its Visibility changes.</summary>
    public static void AnimateVisibility(UIElement element)
    {
        if (!Enabled)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Compositor compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        ElementCompositionPreview.SetImplicitShowAnimation(element, FadeAndSlide(compositor, 0, 1, SlideDistance, 0, ShowDuration));
        ElementCompositionPreview.SetImplicitHideAnimation(element, FadeAndSlide(compositor, 1, 0, 0, SlideDistance, HideDuration));
    }

    private static CompositionAnimationGroup FadeAndSlide(
        Compositor compositor, float fromOpacity, float toOpacity, float fromY, float toY, TimeSpan duration)
    {
        // Fluent's decelerate curve: quick to respond, gentle to settle.
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, fromOpacity);
        fade.InsertKeyFrame(1, toOpacity, easing);
        fade.Duration = duration;

        Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        slide.InsertKeyFrame(1, new Vector3(0, toY, 0), easing);
        slide.Duration = duration;

        CompositionAnimationGroup group = compositor.CreateAnimationGroup();
        group.Add(fade);
        group.Add(slide);
        return group;
    }
}
