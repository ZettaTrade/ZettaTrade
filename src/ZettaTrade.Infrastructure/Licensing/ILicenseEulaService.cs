namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The per-module EULA surface of the activation flow (T-132): a
/// proprietary EULA text never lands in the public repo — the wording
/// ships inside the closed module's package as
/// <c>{pluginId}.EULA.txt</c> next to its DLL, and the activation
/// dialog reads it from the plugins directory at activation time. The
/// acceptance fact (the customer's once-acceptance) persists per plugin in
/// settings, so the text surfaces exactly at the FIRST activation.
/// </summary>
public interface ILicenseEulaService
{
    /// <summary>
    /// Reads the module's EULA text for one plugin id from the plugins
    /// directory; <c>null</c> means no EULA text exists (an open module or
    /// a plugin whose package carries none) and the dialog needs no
    /// acceptance step. A read fault degrades to <c>null</c> and is logged
    /// — the EULA surface is best-effort presentation; the signature check
    /// is the entitlement device.
    /// </summary>
    /// <param name="pluginId">The plugin id the text belongs to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<string?> GetTextAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the plugin id's EULA was already accepted in an earlier activation (default <c>false</c>).</summary>
    /// <param name="pluginId">The plugin id the acceptance fact belongs to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<bool> IsAcceptedAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    /// <summary>Persists the once-acceptance for one plugin id (called by the activation dialog after the customer ticks the accept box).</summary>
    /// <param name="pluginId">The plugin id the acceptance fact belongs to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task MarkAcceptedAsync(
        string pluginId,
        CancellationToken cancellationToken = default);
}
