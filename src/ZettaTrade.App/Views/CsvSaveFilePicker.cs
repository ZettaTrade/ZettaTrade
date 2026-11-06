namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

/// <summary>
/// The CSV save-file dialog for every export surface: Avalonia's storage
/// provider limiting the choice to CSV files. The helper returns the picked
/// local path or null when the user cancelled (a null never reaches the
/// export command as a destination — a cancelled dialog exports nothing).
/// </summary>
internal static class CsvSaveFilePicker
{
    #region Constants

    /// <summary>The file extension every export writes (the filter and the default).</summary>
    private const string CsvExtension = "csv";

    /// <summary>Filter label inside the dialog (platform types, not a UI string: the picker renders it). English is the export flow's common face.</summary>
    private const string CsvFilterLabel = "CSV";

    #endregion Constants

    #region Public API

    /// <summary>
    /// Show the save-file dialog and resolve the chosen path.
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <param name="suggestedFileName">The pre-filled file name (no directory part).</param>
    /// <returns>The absolute path of the chosen file, or null when the user
    /// cancelled or the pick produced no filesystem path.</returns>
    public static async Task<string?> PickAsync(Window owner, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = CsvExtension,
            FileTypeChoices =
            [
                new FilePickerFileType(CsvFilterLabel) { Patterns = ["*." + CsvExtension] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    #endregion Public API
}
