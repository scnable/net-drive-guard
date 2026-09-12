namespace NetDriveGuard.App;

internal static class WindowPlacement
{
    private const int WorkAreaMargin = 16;

    public static void FitToWorkingArea(Form window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var workingArea = Screen.FromControl(window).WorkingArea;
        var availableWidth = Math.Max(1, workingArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(1, workingArea.Height - (WorkAreaMargin * 2));
        var width = Math.Min(window.Width, availableWidth);
        var height = Math.Min(window.Height, availableHeight);
        var left = workingArea.Left + Math.Max(WorkAreaMargin, (workingArea.Width - width) / 2);
        var top = workingArea.Top + Math.Max(WorkAreaMargin, (workingArea.Height - height) / 2);

        window.MinimumSize = new Size(
            Math.Min(window.MinimumSize.Width, availableWidth),
            Math.Min(window.MinimumSize.Height, availableHeight));
        window.SetBounds(left, top, width, height);
    }
}
