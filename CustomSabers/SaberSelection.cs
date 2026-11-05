using System;
using CustomSabersLite.Models;

namespace CustomSabersLite;

/// <summary>
/// The custom saber currently selected in Custom Sabers Lite.
/// </summary>
public static class SaberSelection
{
    /// <summary>
    /// The SabersCore hash of the selected saber, or <see langword="null"/> when the mod is disabled
    /// or the default sabers are selected.
    /// </summary>
    public static string? SelectedSaberHash { get; private set; }

    /// <summary>
    /// Raised on the main thread with the new <see cref="SelectedSaberHash"/> whenever it changes.
    /// </summary>
    public static event Action<string?>? SelectedSaberChanged;

    /// <summary>
    /// Whether the settings menu currently shows the selected saber in the player's hands.
    /// </summary>
    public static bool HeldPreviewVisible { get; private set; }

    /// <summary>
    /// Raised on the main thread with the new <see cref="HeldPreviewVisible"/> whenever it changes.
    /// </summary>
    public static event Action<bool>? HeldPreviewChanged;

    internal static void Update(bool enabled, SaberValue selected)
    {
        var hash = enabled && selected is SaberHash saberHash ? saberHash.Hash : null;
        if (hash == SelectedSaberHash)
        {
            return;
        }

        SelectedSaberHash = hash;
        SelectedSaberChanged?.Invoke(hash);
    }

    internal static void SetHeldPreviewVisible(bool visible)
    {
        if (visible == HeldPreviewVisible)
        {
            return;
        }

        HeldPreviewVisible = visible;
        HeldPreviewChanged?.Invoke(visible);
    }
}
