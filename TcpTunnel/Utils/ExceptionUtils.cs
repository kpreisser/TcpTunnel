using System;
using System.Threading.Tasks;

namespace TcpTunnel.Utils;

internal static class ExceptionUtils
{
    /// <summary>
    /// Filters exceptions that shouldn't be caught. This method should be called from
    /// an exception filter when catching <see cref="Exception"/>.
    /// </summary>
    /// <param name="ex"></param>
    /// <remarks>
    /// <para>
    /// The main purpose of this method is to terminate the application in case of an
    /// <see cref="OutOfMemoryException"/> (by failing directly with
    /// <see cref="Environment.FailFast(string?)"/> instead of returning <c>false</c>)
    /// when using a catch-all handler, because an OOME might be thrown at any place
    /// where an allocation can occur, and in most cases we are not able to guarantee
    /// that the application is still in a "good" (well-defined) state when this happens.
    /// </para>
    /// <para>
    /// Additionally, calling this method from an exception filter instead of from a catch
    /// clause ensures that inner finally handlers (without a catch handler that would
    /// catch OOMEs) are not executed before terminating the app. This ensures that e.g.
    /// finally handlers don't release a lock that would other threads allow to enter the
    /// lock and see an undefined/incomplete state (when the OOME was thrown at an
    /// unexpected location while holding the lock).
    /// For this purpose, such a finally handler should be accompanied by a catch handler
    /// calling this method in its filter, but then rethrowing the exception in the catch
    /// block (if it shouldn't be handled there).
    /// </para>
    /// </remarks>
    /// <returns></returns>
    public static bool CanCatch(this Exception ex)
    {
        if (ex is OutOfMemoryException)
            Environment.FailFast(ex.Message, ex);

        return true;
    }

    public static void HandleUnhandledException(Exception ex)
    {
        Environment.FailFast("Unhandled Exception: " + ex.ToString(), ex);
    }

    /// <summary>
    /// Wraps a task to catch exceptions so that the app is terminated
    /// if an unhandled exception occurs.
    /// This should be used for long-runnning tasks which are not waited for.
    /// </summary>
    /// <param name="asyncFunc"></param>
    /// <returns></returns>
    public static async Task WrapTaskForHandlingUnhandledExceptions(Func<Task> asyncFunc)
    {
        try
        {
            await asyncFunc();
        }
        // By using ex.CanCatch(), we ensure that finally handlers aren't
        // executed when we will terminate due to an OOME.
        catch (Exception ex) when (ex.CanCatch())
        {
            HandleUnhandledException(ex);
        }
    }

    public static Task StartTask(Func<Task> asyncFunc)
    {
        return Task.Run(() => WrapTaskForHandlingUnhandledExceptions(asyncFunc));
    }
}
