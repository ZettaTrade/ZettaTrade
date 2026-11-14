namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

/// <summary>
/// The profile backup's (T-215) file dialogs: the backup's save picker and
/// the restore's open picker, both limited to the backup's own extension.
/// The helpers return the picked local path or null when the user cancelled
/// (a null never reaches the backup's methods — a cancelled dialog moves
/// nothing).
/// </summary>
internal static class ProfileBackupFilePicker
{
    /// <summary>The file extension the backup archives carry (the filters and the default).</summary>
    public const string ProfileBackupExtension = "ztprofile";

    /// <summary>Filter label inside the dialogs (platform types, not a UI string: the picker renders it).</summary>
    private const string ProfileBackupFilterLabel = "ZettaTrade profile backup";

    /// <summary>
    /// Show the save-file dialog and resolve the chosen path (the backup's destination).
    /// </summary>
    /// <param name="owner">The window hosting the dialog.</param>
    /// <param name="suggestedFileName">The pre-filled file name (no directory part).</param>
    /// <returns>The absolute path of the chosen file, or null when the user cancelled.</returns>
    public static async Task<string?> PickSaveAsync(Window owner, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = ProfileBackupExtension,
            FileTypeChoices =
            [
                new FilePickerFileType(ProfileBackupFilterLabel) { Patterns = ["*." + ProfileBackupExtension] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Show the open-file dialog and resolve the chosen path (the restore's source).
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
                new FilePickerFileType(ProfileBackupFilterLabel) { Patterns = ["*." + ProfileBackupExtension] },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
