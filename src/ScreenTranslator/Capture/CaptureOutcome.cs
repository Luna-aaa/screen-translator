using System.Drawing;

namespace ScreenTranslator.Capture;

internal enum CaptureStatus
{
    Success,
    Cancelled,
    /// <summary>A capture was already in progress; this request was ignored.</summary>
    Busy,
    Failed,
}

/// <summary>
/// Result of one capture. On success the caller owns <see cref="Image"/> and must
/// dispose this object.
/// </summary>
internal sealed class CaptureOutcome : IDisposable
{
    public CaptureStatus Status { get; private init; }

    /// <summary>The cropped region. Non-null only when <see cref="Status"/> is Success.</summary>
    public Bitmap? Image { get; private set; }

    /// <summary>
    /// Hands the bitmap to a longer-lived owner (the result window keeps it around so
    /// recognition can be re-run after a language pack is installed). After this call
    /// disposing the outcome no longer disposes the image.
    /// </summary>
    public Bitmap? TakeImage()
    {
        var image = Image;
        Image = null;
        return image;
    }

    /// <summary>Where the selection sat on the desktop, in physical pixels.</summary>
    public Rectangle ScreenRect { get; private init; }

    /// <summary>Path of the saved PNG, when saving is enabled.</summary>
    public string? SavedPath { get; private init; }

    public string? Error { get; private init; }

    public static CaptureOutcome Success(Bitmap image, Rectangle screenRect, string? savedPath) => new()
    {
        Status = CaptureStatus.Success,
        Image = image,
        ScreenRect = screenRect,
        SavedPath = savedPath,
    };

    public static CaptureOutcome Cancelled() => new() { Status = CaptureStatus.Cancelled };

    public static CaptureOutcome Busy() => new() { Status = CaptureStatus.Busy };

    public static CaptureOutcome Failed(string error) => new() { Status = CaptureStatus.Failed, Error = error };

    public void Dispose() => Image?.Dispose();
}
