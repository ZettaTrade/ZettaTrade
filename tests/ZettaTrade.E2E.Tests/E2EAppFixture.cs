namespace ZettaTrade.E2E.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;
using OperatingSystem = System.OperatingSystem;

/// <summary>
/// The E2E collection's shared app process (T-176): one sandboxed launch
/// serves the whole lane. The sandbox is a private profile directory (the
/// <c>ZETTATRADE_PROFILE_DIR</c> seam the production resolver owns), seeded
/// so the first-run disclaimer and wizard never interrupt a flow, with the
/// demo adapter allow-listed - the automation never touches a real
/// exchange. The single-instance claim and handoff pipe are per-profile
/// (they hash the config directory), so the sandboxed process never meets
/// the developer's own running instance.
/// </summary>
public sealed class E2EAppFixture : IAsyncLifetime
{
    private UIA3Automation? _automation;
    private Application? _app;
    private ITreeWalker? _rawWalker;

    /// <summary>The sandboxed profile the process runs against.</summary>
    public string SandboxDir { get; } = Path.Combine(
        Path.GetTempPath(),
        "zettatrade-e2e",
        Guid.NewGuid().ToString("N"));

    /// <summary>The compiled app the project reference stages into this bin.</summary>
    public string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "ZettaTrade.App.exe");

    /// <summary>The main window, attached once the process shows it.</summary>
    public Window? MainWindow { get; private set; }

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var configDir = Path.Combine(SandboxDir, "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "appsettings.json"),
            """
            {
              "Workspace": { "ExchangeId": "demo" },
              "UI": { "DisclaimerVersion": 1, "FirstRunWizardShown": true }
            }
            """);

        // The launch inherits the test process's environment: the profile
        // override and the demo-only adapter allow-list ride in first.
        Environment.SetEnvironmentVariable(
            "ZETTATRADE_PROFILE_DIR", SandboxDir, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            "ZETTATRADE_ADAPTERS", "demo", EnvironmentVariableTarget.Process);

        _app = Application.Launch(ExePath);
        _automation = new UIA3Automation();
        _rawWalker = _automation.TreeWalkerFactory.GetRawViewWalker();

        MainWindow = Retry.WhileNull(
                TryGetMainWindow,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250))
            .Result
            ?? throw new InvalidOperationException(
                $"The app's main window never appeared; sandbox and log dir: {SandboxDir}");
    }

    private Window? TryGetMainWindow()
    {
        try
        {
            return _app!.GetMainWindow(_automation!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Avalonia exposes panels, owned dialogs and menu popups through the
    /// raw view only: the control view drops the decorative panels (the
    /// workspace hosts) and FlaUI's desktop-window enumeration misses the
    /// owned popup windows (dialogs, menu popups). Every lookup in this
    /// lane therefore walks the raw tree - bounded, so a runaway provider
    /// never hangs the lane.
    /// </summary>
    private AutomationElement? FindByAutomationIdInRawTree(
        AutomationElement root,
        string automationId,
        int maxNodes,
        int maxDepth)
    {
        var walker = _rawWalker!;
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0 && visited < maxNodes)
        {
            var (element, depth) = stack.Pop();
            visited++;

            // Avalonia's decorative chrome nodes do not support the
            // AutomationId property at all, so the plain property read
            // throws - read through ValueOrDefault, which yields null
            // instead, and never let a read failure prune the subtree
            // below: the walk resumes through it either way.
            try
            {
                if (element.Properties.AutomationId.ValueOrDefault == automationId)
                {
                    return element;
                }
            }
            catch (Exception)
            {
                // A node dying mid-walk (a closing popup) skips out; the
                // retry around every lookup re-runs the walk.
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                var child = walker.GetFirstChild(element);
                while (child is not null)
                {
                    stack.Push((child, depth + 1));
                    child = walker.GetNextSibling(child);
                }
            }
            catch (Exception)
            {
                // The subtree collapsed mid-walk; the lookup's retry owns it.
            }
        }

        return null;
    }

    /// <summary>Find a descendant by AutomationId over the raw view, polling while the tree settles.</summary>
    public AutomationElement? FindDescendantById(AutomationElement root, string automationId) =>
        Retry.WhileNull(
                () => FindByAutomationIdInRawTree(root, automationId, maxNodes: 4000, maxDepth: 30),
                TimeSpan.FromSeconds(6),
                TimeSpan.FromMilliseconds(150))
            .Result;

    /// <summary>
    /// Every top-level subtree the app's process owns: the main window plus
    /// the raw-view desktop children whose process is ours - dialogs, menu
    /// popups and combo popups surface there (the desktop-children-by-pid
    /// API route misses them, the raw walk does not). Other processes'
    /// trees (the IDE, the shell) stay out of every bound.
    /// </summary>
    private List<AutomationElement> AppSubtrees()
    {
        var walker = _rawWalker!;
        var roots = new List<AutomationElement>();
        if (MainWindow is not null)
        {
            roots.Add(MainWindow);
        }

        var child = walker.GetFirstChild(_automation!.GetDesktop());
        while (child is not null)
        {
            try
            {
                if (child.Properties.ProcessId.ValueOrDefault == _app!.ProcessId)
                {
                    roots.Add(child);
                }
            }
            catch (Exception)
            {
                // A top-level window dying mid-enumeration skips out.
            }

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (Exception)
            {
                // The desktop enumeration collapsed; what we hold is enough.
                break;
            }
        }

        return roots;
    }

    /// <summary>One full sweep of the app's own subtrees, with no retry.</summary>
    private AutomationElement? SingleShotEverywhere(string automationId)
    {
        foreach (var root in AppSubtrees())
        {
            if (FindByAutomationIdInRawTree(root, automationId, maxNodes: 4000, maxDepth: 30)
                is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Find any element of the process by AutomationId across the whole
    /// process: the main window's subtree, its owned dialogs and the
    /// menu/combo popups all surface here.
    /// </summary>
    public AutomationElement? FindElementEverywhere(string automationId) =>
        Retry.WhileNull(
                () => SingleShotEverywhere(automationId),
                TimeSpan.FromSeconds(6),
                TimeSpan.FromMilliseconds(150))
            .Result;

    /// <summary>Find a top-level window by AutomationId, polling while it opens.</summary>
    public Window? FindTopLevelWindowById(string automationId) =>
        FindElementEverywhere(automationId) is { } element
            ? new Window(element.FrameworkAutomationElement)
            : null;

    /// <summary>
    /// The app process's liveness, readable by the tests: an assert on a
    /// missing window first tells whether the process died under the click
    /// (a crash report beats a starving lookup message).
    /// </summary>
    public bool AppProcessHasExited => _app?.HasExited ?? true;

    /// <summary>Poll until a top-level window with the id is gone.</summary>
    public bool WaitForTopLevelWindowGone(string automationId) =>
        Retry.WhileTrue(
                () => SingleShotEverywhere(automationId) is not null,
                TimeSpan.FromSeconds(6),
                TimeSpan.FromMilliseconds(150))
            .Success;

    /// <summary>
    /// Bring the window to the foreground the way the user's activation
    /// does: typed hotkeys land on the foreground window, and the UIA
    /// SetFocus route alone does not win the foreground race on every host.
    /// </summary>
    public void BringToFront(Window window)
    {
        try
        {
            var handle = window.Properties.NativeWindowHandle.Value;
            if (handle != 0)
            {
                WindowsApis.SetForegroundWindow(handle);
            }
        }
        catch (Exception)
        {
            // A host without the native handle property falls back to the
            // UIA focus below.
        }

        window.Focus();
    }

    /// <summary>
    /// Open a top-level menu by click and fire one of its items: the
    /// mouse route is the user's route (the popup opens on the click, the
    /// item click fires the command and closes it).
    /// </summary>
    public void ClickMenuItem(string menuId, string itemId)
    {
        var menu = FindDescendantById(MainWindow!, menuId)
            ?? throw new InvalidOperationException($"The {menuId} menu never rendered.");
        menu.Click();

        var item = FindElementEverywhere(itemId)
            ?? throw new InvalidOperationException($"The {itemId} menu item never rendered.");
        item.Click();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                _app.Close();
            }
            catch (Exception)
            {
                // A hung window dies through the kill below; never mask the teardown.
            }

            if (!_app.HasExited)
            {
                _app.Kill();
            }

            _app.Dispose();
            _app = null;
        }

        _automation?.Dispose();
        _automation = null;
        _rawWalker = null;
        MainWindow = null;

        Environment.SetEnvironmentVariable("ZETTATRADE_PROFILE_DIR", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("ZETTATRADE_ADAPTERS", null, EnvironmentVariableTarget.Process);

        // The dying process may hold a log handle for a beat; a bounded
        // delete retry keeps the temp root clean without spinning forever.
        for (var attempt = 0; attempt < 3 && Directory.Exists(SandboxDir); attempt++)
        {
            try
            {
                Directory.Delete(SandboxDir, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(400);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(400);
            }
        }
    }

    /// <summary>The two user32 activation calls the lane needs (test-only).</summary>
    private static class WindowsApis
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
