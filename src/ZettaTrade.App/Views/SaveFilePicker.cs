namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

/// <summary>
/// The save-file dialog for every export surface (T-094 generalized the
/// CSV-only picker): Avalonia's storage provider limiting the choice to one
/// extension. The helper returns the picked local path or null when the user
/// cancelled (a null never reaches an export command as a destination — a
/// cancelled dialog exports nothing).
/// </summary>
internal static class SaveFilePicker
{
    /// <summary>
    /// Show the save-file dialog and resolve the chosen path.
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <param name="suggestedFileName">The pre-filled file name (no directory part).</param>
    /// <param name="extension">The one extension the choice limits to (the filter and the default).</param>
    /// <param name="filterLabel">Filter label inside the dialog (platform types, not a UI string: the picker renders it).</param>
    /// <returns>The absolute path of the chosen file, or null when the user
    /// cancelled or the pick produced no filesystem path.</returns>
    public static async Task<string?> PickAsync(
        Window owner,
        string suggestedFileName,
        string extension,
        string filterLabel)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(filterLabel) { Patterns = ["*." + extension] },
            ],
        });

        return file?.TryGetLocalPath();
    }
}
