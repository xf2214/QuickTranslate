namespace QuickTranslate.Core.Selection;

public enum AppState
{
    Idle = 0,
    Capturing = 1,
    Ocr = 2,
    Selecting = 3,
    OverlayVisible = 4,
    Translating = 5,
    Displaying = 6
}
