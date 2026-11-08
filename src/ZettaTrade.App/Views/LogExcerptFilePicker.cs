namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

/// <summary>
/// The log excerpt's save-file dialog (T-101): Avalonia's storage provider
/// limiting the choice to plain text files. The helper returns the picked
/// local path or null when the user cancelled (a null never reaches the
/// export command as a destination — a cancelled dialog exports nothing).
/// </summary>
internal static class LogExcerptFilePicker
{
    /// <summary>The file extension every excerpt writes (the filter and the default).</summary>
    private const string ExcerptExtension = "txt";

    /// <summary>Filter label inside the dialog (platform types, not a UI string: the picker renders it). English is the export flow's common face.</summary>
    private const string ExcerptFilterLabel = "Log excerpt";

    /// <summary>
    /// Show the save-file dialog and resolve the chosen path.
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <param name="suggestedFileName">The pre-filled file name (no directory part).</param>
    /// <returns>The absolute path of the chosen file, or null when the user cancelled.</returns>
    public static async Task<string?> PickAsync(Window owner, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = ExcerptExtension,
            FileTypeChoices =
            [
                new FilePickerFileType(ExcerptFilterLabel) { Patterns = ["*." + ExcerptExtension] },
            ],
        });

        return file?.TryGetLocalPath();
    }
}
