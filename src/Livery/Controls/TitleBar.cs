namespace Livery.Controls;

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

/// <summary>
/// The styled title-bar strip the chrome windows draw in place of the native
/// one: the title mirrors the host window's <see cref="Window.Title"/> live
/// (the reactive localization binding re-renders it on a language switch), the
/// empty strip area drag-moves the window and double-click toggles the
/// maximized state, and the minimize / maximize / close icons run the stock
/// window commands. The decorative buttons collapse on the
/// <see cref="ShowMinimizeButton"/> / <see cref="ShowMaximizeButton"/>
/// properties — modal, fixed-size dialogs ride the compact chrome with the
/// close button only. Every color comes from the theme palette keys, so a
/// theme switch retints the strip live.
/// </summary>
public class TitleBar : TemplatedControl
{
    #region Constants

    /// <summary>Template part names resolved by <see cref="OnApplyTemplate"/>.</summary>
    private const string TitlePartName = "PART_Title";
    private const string MinimizeButtonPartName = "PART_MinimizeButton";
    private const string MaximizeButtonPartName = "PART_MaximizeButton";
    private const string RestoreButtonPartName = "PART_RestoreButton";
    private const string CloseButtonPartName = "PART_CloseButton";

    /// <summary>
    /// The Alt+F4 close gesture. With BorderOnly decorations the window has no
    /// system menu, so the OS-level gesture has nothing to ride: the strip
    /// tunnels window-wide key events and restores the documented gesture.
    /// </summary>
    private static readonly KeyGesture CloseGesture = new(Key.F4, KeyModifiers.Alt);

    #endregion Constants

    #region Styled Properties

    /// <summary>Identifies the <see cref="ShowMinimizeButton"/> property.</summary>
    public static readonly StyledProperty<bool> ShowMinimizeButtonProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMinimizeButton), defaultValue: true);

    /// <summary>Identifies the <see cref="ShowMaximizeButton"/> property.</summary>
    public static readonly StyledProperty<bool> ShowMaximizeButtonProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMaximizeButton), defaultValue: true);

    /// <summary>
    /// Whether the strip shows the minimize button. False on compact chrome
    /// (modal, fixed-size dialogs): a minimized ShowInTaskbar=false window
    /// would drop out of reach, so those windows carry the close button only.
    /// </summary>
    public bool ShowMinimizeButton
    {
        get => GetValue(ShowMinimizeButtonProperty);
        set => SetValue(ShowMinimizeButtonProperty, value);
    }

    /// <summary>
    /// Whether the strip shows the maximize button (and honors a double-click
    /// maximize). False on compact chrome: it would fullscreen a fixed-size
    /// dialog and bury its pinned buttons.
    /// </summary>
    public bool ShowMaximizeButton
    {
        get => GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    #endregion Styled Properties

    #region Fields & Properties

    private Window? _window;
    private TextBlock? _title;
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _restoreButton;
    private Button? _closeButton;

    #endregion Fields & Properties

    #region Template

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _title = e.NameScope.Find<TextBlock>(TitlePartName);
        ReplaceButton(ref _minimizeButton, e.NameScope, MinimizeButtonPartName);
        ReplaceButton(ref _maximizeButton, e.NameScope, MaximizeButtonPartName);
        ReplaceButton(ref _restoreButton, e.NameScope, RestoreButtonPartName);
        ReplaceButton(ref _closeButton, e.NameScope, CloseButtonPartName);

        HookWindow(TopLevel.GetTopLevel(this) as Window);
    }

    /// <summary>
    /// Wire the decorative-button click handler to a freshly applied template
    /// part (a template re-application must not stack duplicate
    /// subscriptions).
    /// </summary>
    private void ReplaceButton(ref Button? field, INameScope nameScope, string partName)
    {
        field?.Click -= OnDecorativeButtonClick;

        field = nameScope.Find<Button>(partName);
        if (field is { } fresh)
        {
            fresh.Click += OnDecorativeButtonClick;
        }
    }

    #endregion Template

    #region Window Wiring

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookWindow(TopLevel.GetTopLevel(this) as Window);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HookWindow(null);
    }

    private void HookWindow(Window? window)
    {
        if (ReferenceEquals(_window, window))
        {
            RefreshState();
            return;
        }

        if (_window is { } leaving)
        {
            leaving.PropertyChanged -= OnWindowPropertyChanged;
            leaving.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
        }

        _window = window;
        if (_window is { } entered)
        {
            entered.PropertyChanged += OnWindowPropertyChanged;
            // Tunneling reaches every key event the window sees, wherever the
            // focus sits inside it: the Alt+F4 close gesture works from any
            // control in the client area.
            entered.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        }

        RefreshState();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (CloseGesture.Matches(e) && _window is { } window)
        {
            e.Handled = true;
            window.Close();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TitleProperty)
        {
            RefreshTitle();
        }
        else if (e.Property == Window.WindowStateProperty)
        {
            RefreshDecorativeButtons();
        }
    }

    #endregion Window Wiring

    #region View State

    private void RefreshState()
    {
        RefreshTitle();
        RefreshDecorativeButtons();
    }

    private void RefreshTitle()
    {
        if (_title is { } title)
        {
            title.Text = _window?.Title ?? string.Empty;
        }
    }

    /// <summary>
    /// Property-driven visibility of the decorative buttons: the minimize
    /// button follows its show property; the maximize/restore pair share one
    /// window-state slot (one of them hides with the other showing, both
    /// collapse on compact chrome).
    /// </summary>
    private void RefreshDecorativeButtons()
    {
        var maximized = _window is { } window && window.WindowState == WindowState.Maximized;

        if (_minimizeButton is { } minimize)
        {
            minimize.IsVisible = ShowMinimizeButton;
        }

        if (_maximizeButton is { } maximize)
        {
            maximize.IsVisible = ShowMaximizeButton && !maximized;
        }

        if (_restoreButton is { } restore)
        {
            restore.IsVisible = ShowMaximizeButton && maximized;
        }
    }

    #endregion View State

    #region Interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_window is not { } window || e.Source is not Visual source)
        {
            return;
        }

        // The decorative buttons consume their own presses; only the empty
        // strip area is a drag surface / double-click maximize toggle.
        if (source.GetSelfAndVisualAncestors().OfType<Button>().Any())
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The native-like double-click: maximize an unmaximized strip, restore
        // a maximized one. Everything else is a plain move-drag — exactly the
        // OS title-bar gesture the strip replaces.
        if (ShowMaximizeButton && e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
        }
        else
        {
            window.BeginMoveDrag(e);
        }
    }

    private void ToggleMaximize()
    {
        if (_window is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnDecorativeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_window is not { } window || sender is not Button button)
        {
            return;
        }

        if (ReferenceEquals(button, _minimizeButton))
        {
            window.WindowState = WindowState.Minimized;
        }
        else if (ReferenceEquals(button, _maximizeButton) || ReferenceEquals(button, _restoreButton))
        {
            ToggleMaximize();
        }
        else if (ReferenceEquals(button, _closeButton))
        {
            window.Close();
        }
    }

    #endregion Interaction
}
