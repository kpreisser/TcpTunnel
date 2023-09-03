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
    /// in the follwing cases:
    /// </para>
    /// <para>
    /// 1) when using a catch-all handler (because an OOME might be thrown at any place
    /// where an allocation can occur, and in most cases we are not able to guarantee
    /// that the application is still in a "good" (well-defined) state when this happens)
    /// </para>
    /// <para>
    /// 2) when a code has a <c>finally</c> handler that e.g. releases a lock, even if it
    /// wouldn't have a "catch-all" handler (because when the OOME was thrown in the middle
    /// of a code that updates a state, releasing the lock would allow other threads to
    /// enter that lock and potentially see the inconsistent state, even if the app would
    /// terminate shortly after that due to the unhandled OOME).
    /// </para>
    /// <para>
    /// For point 2):
    /// Calling this method from an exception filter instead of from a <c>catch</c> clause
    /// ensures that inner <c>finally</c> handlers in non-async methods (without a
    /// <c>catch</c> handler that would catch OOMEs) are not executed before terminating
    /// the app.
    /// For this purpose, such a <c>finally</c> handler should be accompanied by a
    /// <c>catch</c> handler calling this method in its filter, but then rethrowing the
    /// exception in the <c>catch</c> block (if it shouldn't be handled there).
    /// </para>
    /// <para>
    /// Note: When calling this method from an exception filter in an <c>async</c> method,
    /// and that method calls another <c>async</c> method that has a <c>finally</c> handler,
    /// the <c>finally</c> handler of the inner async method would be executed before the
    /// exception filter of the outer method. In such case, you should also add a 
    /// <c>catch</c> clause with an exception filter calling <see cref="CanCatch(Exception)"/>
    /// to the <c>try-finally</c> block of the inner <c>async</c> method (where the
    /// <c>catch</c> block just rethrows the exception; otherwise, you would need
    /// to ensure that the <c>catch</c> block again has a <c>try</c>-), to prevent the <c>finally</c>
    /// handler from being called in case of an OOME.
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
        // Using an exception filter to call CanCatch() wouldn't work here since we are
        // calling an async method, whose finally handlers would be executed before our
        // exception filter.
        catch (Exception ex)
        {
            HandleUnhandledException(ex);
        }
    }

    public static Task StartTask(Func<Task> asyncFunc)
    {
        return Task.Run(() => WrapTaskForHandlingUnhandledExceptions(asyncFunc));
    }
}
