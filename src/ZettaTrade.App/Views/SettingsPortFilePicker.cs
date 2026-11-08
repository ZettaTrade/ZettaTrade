namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

/// <summary>
/// The settings port's (T-100) file dialogs: the export's save picker and the
/// import's open picker, both limited to the port's own extension. The
/// helpers return the picked local path or null when the user cancelled (a
/// null never reaches the port's commands — a cancelled dialog moves
/// nothing).
/// </summary>
internal static class SettingsPortFilePicker
{
    /// <summary>The file extension the port files carry (the filters and the default).</summary>
    public const string PortExtension = "ztsettings";

    /// <summary>Filter label inside the dialogs (platform types, not a UI string: the picker renders it).</summary>
    private const string PortFilterLabel = "ZettaTrade settings";

    /// <summary>
    /// Show the save-file dialog and resolve the chosen path (the export's destination).
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <param name="suggestedFileName">The pre-filled file name (no directory part).</summary>
    /// <returns>The absolute path of the chosen file, or null when the user cancelled.</returns>
    public static async Task<string?> PickSaveAsync(Window owner, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = PortExtension,
            FileTypeChoices =
            [
                new FilePickerFileType(PortFilterLabel) { Patterns = ["*." + PortExtension] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Show the open-file dialog and resolve the chosen path (the import's source).
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <returns>The absolute path of the chosen file, or null when the user cancelled.</returns>
    public static async Task<string?> PickOpenAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(PortFilterLabel) { Patterns = ["*." + PortExtension] },
                new FilePickerFileType(PortFilterLabel) { Patterns = ["*.json"] },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
