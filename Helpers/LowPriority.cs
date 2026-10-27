using System.Runtime.InteropServices;

namespace WinOptimizer.Helpers;

/// <summary>
/// Runs heavy work (like reading thousands of files) on a thread in Windows "background mode", which lowers
/// both its CPU and disk priority so it doesn't make the PC or the app feel slow.
/// </summary>
public static class LowPriority
{
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    public static Task<T> Run<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            bool background = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
                result.SetResult(work());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
            finally
            {
                if (background) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            }
        })
        {
            IsBackground = true,
            Name = "WinOptimizer low-priority work",
        };
        thread.Start();
        return result.Task;
    }
}
