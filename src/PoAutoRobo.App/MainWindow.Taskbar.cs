using System.Runtime.InteropServices;

namespace PoAutoRobo.App;

// Progress on the taskbar button, so a long job can be watched with the window hidden.
public sealed partial class MainWindow
{
    private readonly ITaskbarList3? _taskbar = NewTaskbar();

    private void ShowTaskbarProgress(double? percent)
    {
        try
        {
            _taskbar?.SetProgressState(App.WindowHandle, percent is null ? 0 : 2); // 0 clears the bar, 2 shows a normal one
            if (percent is { } value)
                _taskbar?.SetProgressValue(App.WindowHandle, (ulong)value, 100);
        }
        catch (COMException)
        {
            // The taskbar bar is a nicety; the panel in the window is the real progress display.
        }
    }

    private static ITaskbarList3? NewTaskbar()
    {
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
            return taskbar;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // The Windows taskbar's own interface. Methods must stay in this order: it is how the system finds them.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;
}
