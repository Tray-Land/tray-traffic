using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using TrayTraffic.Controls;
using TrayTraffic.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;
using TitleBar = Microsoft.UI.Xaml.Controls.TitleBar;

namespace TrayTraffic.Views;

/// <summary>
/// Borderless, always-on-top popup shown against the tray icon, styled like the shell's own
/// flyouts. Slides and fades in, light-dismisses when focus leaves. Dismissing only hides it, so
/// reopening soon after is instant; after <see cref="CloseDelay"/> hidden it closes for real and
/// the next open builds a new window, so the idle app holds no XAML tree.
/// </summary>
public sealed partial class TrayFlyoutWindow : WindowEx
{
    private const int PopupWidth = 420;
    private const int PopupHeight = 600;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    // The backdrop paints behind the XAML content and ignores UIElement.Opacity, so the fade uses
    // per-window layered alpha driven by a frame timer, alongside a small slide.
    private const int ShowSlideDistance = 24;
    private const int HideSlideDistance = 12;
    private const double ShowDurationMs = 250;
    private const double HideDurationMs = 180;

    /// <summary>
    /// A tray click right after a light-dismiss is the same click that dismissed the popup (focus
    /// left to the taskbar); treat it as "close" instead of re-showing.
    /// </summary>
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan CloseDelay = TimeSpan.FromMinutes(1);

    private readonly HWND _hwnd;
    private readonly FlyoutPage _page = new();
    private readonly ShellBackdrop _backdrop = new();
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _closeTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();

    private bool _isShowing;
    private bool _isPopupVisible;
    private bool _allowClose;
    private bool _isClosed;
    private bool _isFlyoutMode = true;
    private bool _placed;
    private TitleBar? _titleBar;
    private DateTime _lastDismissedAtUtc = DateTime.MinValue;

    // Animation state: _baseX/_baseY is the resting position; the slide animates an offset below
    // it in physical pixels. _current* is where the last frame left off, so an animation that
    // interrupts another starts from there instead of snapping.
    private bool _isHiding;
    private int _baseX;
    private int _baseY;
    private int _fromOffset;
    private int _toOffset;
    private double _fromOpacity;
    private double _toOpacity;
    private double _durationMs = ShowDurationMs;
    private int _currentOffset;
    private double _currentOpacity;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        PageHost.Content = _page;
        _page.WindowOptionsChanged += OnWindowOptionsChanged;

        SystemBackdrop = _backdrop;
        ApplyShellTheme();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureWindowChrome();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(150);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideIfFocusLeftWindow();

        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.Interval = CloseDelay;
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => CloseIfStillHidden();

        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(15);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += AnimationTimer_Tick;
    }

    /// <summary>Shows the popup, or hides it if already visible (tray-icon click behavior).</summary>
    public void Toggle()
    {
        if (_isPopupVisible)
        {
            if (_isFlyoutMode)
            {
                HidePopup();
            }
            else
            {
                // A window stays open; the tray icon brings it to the front instead.
                Activate();
                PInvoke.SetForegroundWindow(_hwnd);
            }
            return;
        }

        if (DateTime.UtcNow - _lastDismissedAtUtc < RecentDismissWindow)
        {
            return;
        }

        ShowPopup();
    }

    public void ShowPopup()
    {
        // Position before showing so the window never flashes at a stale location. A window mode
        // keeps the spot and size the user gave it once it has been placed.
        if (_isFlyoutMode || !_placed)
        {
            if (!TryRestoreBounds())
            {
                WindowPlacementService.PositionNearTray(this, PopupWidth, PopupHeight);
            }
            _placed = true;
        }
        _baseX = AppWindow.Position.X;
        _baseY = AppWindow.Position.Y;

        _hideTimer.Stop();
        _closeTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        // Re-read here too: not every taskbar setting change raises ColorValuesChanged.
        ApplyShellTheme();
        PlayShowAnimation();

        this.Show();
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);

        _page.OnShown();
    }

    /// <summary>True when the popup light-dismisses, so opening another window should hide it.</summary>
    public bool HidesWhenAnotherWindowOpens => SettingsService.WindowMode != WindowMode.Window;

    public void HidePopup()
    {
        _hideTimer.Stop();
        _isShowing = false;

        if (!_isPopupVisible)
        {
            return;
        }

        SaveBounds();
        _isPopupVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _page.OnHidden();
        PlayHideAnimation();

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>
    /// Closes the window for real: stops its timers and unsubscribes from app-lifetime events so
    /// nothing keeps the window alive.
    /// </summary>
    public void CloseWindow()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _hideTimer.Stop();
        _closeTimer.Stop();
        _animationTimer.Stop();
        _page.OnHidden();
        _page.Dispose();

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        Activated -= OnWindowActivated;
        AppWindow.Closing -= OnAppWindowClosing;

        _allowClose = true;
        Close();
    }

    private void CloseIfStillHidden()
    {
        if (!_isPopupVisible)
        {
            CloseWindow();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Alt+F4 hides like any other dismissal.
        if (!_allowClose)
        {
            args.Cancel = true;
            HidePopup();
        }
    }

    // Raised off the UI thread when the accent color or light/dark mode changes.
    private void OnColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyShellTheme);

    /// <summary>Matches the taskbar's mode and accent tint, like the shell's own flyouts.</summary>
    private void ApplyShellTheme()
    {
        bool isLight = SystemThemeService.IsLight;
        PopupRoot.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        _backdrop.Update(isLight, SystemThemeService.ShowsAccentColor ? _uiSettings.GetColorValue(UIColorType.AccentDark2) : null);
    }

    private void PlayShowAnimation()
    {
        _isHiding = false;
        _durationMs = ShowDurationMs;
        _fromOffset = ScaleForDpi(ShowSlideDistance);
        _toOffset = 0;
        _fromOpacity = 0;
        _toOpacity = 1;

        ApplyFrame(_fromOffset, 0);
        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void PlayHideAnimation()
    {
        _isHiding = true;
        _durationMs = HideDurationMs;
        // The user may have dragged a window mode; slide from wherever it is now.
        _baseX = AppWindow.Position.X;
        _baseY = AppWindow.Position.Y - _currentOffset;
        _fromOffset = _currentOffset;
        _toOffset = ScaleForDpi(HideSlideDistance);
        _fromOpacity = _currentOpacity;
        _toOpacity = 0;

        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        double progress = Math.Min(1, _animationClock.Elapsed.TotalMilliseconds / _durationMs);

        // Cubic ease-out on the way in, ease-in on the way out; linear fade.
        double eased = _isHiding ? progress * progress * progress : 1 - Math.Pow(1 - progress, 3);
        int offset = _fromOffset + (int)Math.Round((_toOffset - _fromOffset) * eased);
        double opacity = _fromOpacity + ((_toOpacity - _fromOpacity) * progress);
        ApplyFrame(offset, opacity);

        if (progress < 1)
        {
            return;
        }

        _animationTimer.Stop();
        _animationClock.Reset();
        if (_isHiding)
        {
            // Rest at the base position so a window the user moved reopens where it was left.
            AppWindow.Move(new PointInt32(_baseX, _baseY));
            this.Hide();
        }
    }

    private void ApplyFrame(int offset, double opacity)
    {
        _currentOffset = offset;
        _currentOpacity = opacity;

        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        _ = PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        AppWindow.Move(new PointInt32(_baseX, _baseY + offset));
    }

    private int ScaleForDpi(int logical)
    {
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        return (int)Math.Round(logical * (dpi == 0 ? 96 : dpi) / 96.0);
    }

    private void ConfigureWindowChrome()
    {
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        ApplyWindowMode();

        unsafe
        {
            DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }
    }

    /// <summary>
    /// Styles the window for the chosen mode. A flyout is borderless, fixed, topmost and off the
    /// taskbar; the window modes get a title bar and a taskbar button, can be resized, and stay on
    /// top only in "always on top". Minimize is left off so a hidden window never keeps polling.
    /// </summary>
    private void ApplyWindowMode()
    {
        WindowMode mode = SettingsService.WindowMode;
        bool flyout = mode == WindowMode.Flyout;
        _isFlyoutMode = flyout;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            bool resizable = !flyout && SettingsService.WindowResizable;
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
            presenter.IsAlwaysOnTop = mode != WindowMode.Window;
            presenter.SetBorderAndTitleBar(true, !flyout);
        }

        // The window modes draw a WinUI title bar in place of the system one. It only exists in the
        // tree then: a flyout must not carry a title bar at all, even a collapsed one.
        if (flyout)
        {
            _page.MoveHeaderTo(null);
            if (_titleBar is not null)
            {
                PopupRoot.Children.Remove(_titleBar);
            }
            if (ExtendsContentIntoTitleBar)
            {
                ExtendsContentIntoTitleBar = false;
            }
        }
        else
        {
            _titleBar ??= new TitleBar
            {
                Title = "Tray Traffic",
                IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri("ms-appx:///Assets/StoreLogo.png")) },
            };
            if (!PopupRoot.Children.Contains(_titleBar))
            {
                PopupRoot.Children.Insert(0, _titleBar);
            }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_titleBar);
            _page.MoveHeaderTo(_titleBar);
        }
        AppWindow.IsShownInSwitchers = !flyout;

        // WS_EX_LAYERED enables the fade; WS_EX_TOOLWINDOW keeps a flyout off the taskbar and Alt-Tab.
        int exStyle = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) | WS_EX_LAYERED;
        exStyle = flyout ? exStyle | WS_EX_TOOLWINDOW : exStyle & ~WS_EX_TOOLWINDOW;
        _ = PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);
    }

    /// <summary>Moves a window mode back to its remembered bounds, if kept and still on a display.</summary>
    private bool TryRestoreBounds()
    {
        if (_isFlyoutMode || !SettingsService.RememberWindowBounds || SettingsService.WindowBounds is not { } bounds
            || bounds.Width < 200 || bounds.Height < 200
            || DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None) is null)
        {
            return false;
        }

        AppWindow.MoveAndResize(bounds);
        return true;
    }

    private void SaveBounds()
    {
        if (!_isFlyoutMode && SettingsService.RememberWindowBounds)
        {
            // A show or hide slide in progress offsets the window; save its resting spot.
            SettingsService.WindowBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y - _currentOffset, AppWindow.Size.Width, AppWindow.Size.Height);
        }
    }

    private void OnWindowOptionsChanged(object? sender, EventArgs args)
    {
        if ((SettingsService.WindowMode == WindowMode.Flyout) != _isFlyoutMode)
        {
            // Extending into the title bar, the tool-window style and the borderless frame only
            // settle on a new window, so crossing between flyout and window rebuilds it.
            SaveBounds();
            bool reopen = _isPopupVisible;
            DispatcherQueue queue = DispatcherQueue;
            CloseWindow();
            if (reopen)
            {
                queue.TryEnqueue(DispatcherQueuePriority.Low, App.Current.ShowFlyout);
            }
            return;
        }

        ApplyWindowMode();
        if (_isPopupVisible)
        {
            SaveBounds();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isShowing = false;
            _hideTimer.Stop();
            return;
        }

        // Only a flyout light-dismisses; the window modes stay until closed.
        if (!_isFlyoutMode || _isShowing || !_isPopupVisible)
        {
            return;
        }

        // Debounce: focus bounces briefly while XAML popups (tooltips, menus, dropdowns) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        _hideTimer.Stop();
        if (_isPopupVisible && !ShouldRemainVisible(PInvoke.GetForegroundWindow()))
        {
            HidePopup();
        }
    }

    private bool ShouldRemainVisible(HWND foreground)
    {
        if (foreground == HWND.Null)
        {
            return false;
        }

        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
        {
            return true;
        }

        // WinUI flyouts and ComboBox dropdowns open in their own popup HWNDs; classic menus use #32768.
        Span<char> buffer = stackalloc char[64];
        int length = PInvoke.GetClassName(foreground, buffer);
        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> className = buffer[..length];
        return className.SequenceEqual("Xaml_WindowedPopupClass") || className.SequenceEqual("#32768");
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_page.TryCloseSearch() && _isFlyoutMode)
        {
            HidePopup();
        }
    }
}
