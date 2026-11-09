namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

/// <summary>
/// Pure hotkey grammar and conflict math (T-102): every function is
/// deterministic and side-effect free, so the goldens pin the exact
/// verdicts the editor and the service rely on. Canonicalization rides
/// Avalonia's own <see cref="KeyGesture"/> parser — a stored or typed
/// string and its parsed gesture always print the same spelling.
/// </summary>
public static class HotkeyConflicts
{
    /// <summary>
    /// Canonicalize a typed or stored gesture string. A null result is the
    /// editor's "unrecognized" verdict — the caller shows its own message
    /// and never persists the unparseable text.
    /// </summary>
    /// <param name="gestureText">The gesture text as typed or stored.</param>
    public static string? Normalize(string? gestureText)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
            return null;

        // Avalonia's parser accepts both "Ctrl" and "Control" spellings and
        // rounds the result through ToString() into one canonical form. The
        // parser refuses a non-gesture with FormatException — the catch is
        // the null verdict's whole point: typed or stored garbage is
        // refused, it never crashes the editor or the map reader.
        try
        {
            return KeyGesture.Parse(gestureText).ToString();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A parsed gesture's modifier verdict: a shortcut without a modifier
    /// (a bare letter or digit) is forbidden — typing in a text box would
    /// fire the action. The editor shows its own warning and never
    /// persists the plain gesture.
    /// </summary>
    /// <param name="canonicalGesture">The canonical gesture string.</param>
    public static bool IsPlain(string canonicalGesture) =>
        KeyGesture.Parse(canonicalGesture).KeyModifiers == KeyModifiers.None;

    /// <summary>
    /// The first gesture two actions share: the pair to blame in the
    /// editor's conflict message, or null when every gesture is unique.
    /// The scan is order-stable — the pair comes out in the input order,
    /// so the same map always diagnoses the same way.
    /// </summary>
    /// <param name="canonicalGesturesById">The gesture per action id, canonical.</param>
    public static (string FirstId, string SecondId)? FindDuplicateGesture(
        IReadOnlyList<(string Id, string Gesture)> canonicalGesturesById)
    {
        for (var i = 0; i < canonicalGesturesById.Count; i++)
        {
            for (var j = i + 1; j < canonicalGesturesById.Count; j++)
            {
                if (string.Equals(canonicalGesturesById[i].Gesture, canonicalGesturesById[j].Gesture, StringComparison.Ordinal))
                {
                    return (canonicalGesturesById[i].Id, canonicalGesturesById[j].Id);
                }
            }
        }

        return null;
    }
}
