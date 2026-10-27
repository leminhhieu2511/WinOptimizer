using System.Windows;

namespace WinOptimizer.Helpers;

/// <summary>
/// Tells a page when it is actually on screen: shown in the window and the window isn't minimized.
/// Pages use it to pause timers so nothing runs in the background when you can't see it.
/// </summary>
public static class PageActivity
{
    public static void Track(FrameworkElement page, Action onActive, Action onInactive)
    {
        Window? window = null;
        bool active = false;

        void Update()
        {
            bool now = page.IsVisible && window is { WindowState: not WindowState.Minimized };
            if (now == active) return;
            active = now;
            if (active) onActive();
            else onInactive();
        }

        void StateChanged(object? sender, EventArgs e) => Update();

        page.Loaded += (_, _) =>
        {
            if (window is not null) return;
            window = Window.GetWindow(page);
            if (window is not null) window.StateChanged += StateChanged;
            Update();
        };
        page.IsVisibleChanged += (_, _) => Update();
    }
}
