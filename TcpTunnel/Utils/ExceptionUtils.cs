using System;
using System.Runtime.ExceptionServices;
using System.Threading;
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

    /// <summary>
    /// Handles an exception by simulating an unhandled exception in a thread pool
    /// worker thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This can be used for long running tasks, to ensure such exceptions will
    /// be observed immediately, not just when waiting for the task.
    /// </para>
    /// <para>
    /// Note that this method may return before the application is actually
    /// terminated.
    /// </para>
    /// </remarks>
    /// <param name="exception"></param>
    public static void HandleUnhandledException(Exception exception)
    {
        // Throw the exception in a thread pool worker and return.
        // This is the same mechanism done by Task.ThrowAsync(), which is used e.g.
        // when an exception is thrown in an "async void" method.
        ThreadPool.QueueUserWorkItem(
            static state => ((ExceptionDispatchInfo)state!).Throw(),
            ExceptionDispatchInfo.Capture(exception));
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
            await asyncFunc().ConfigureAwait(false);
        }
        // Using an exception filter to call CanCatch() wouldn't work here since we are
        // calling an async method, whose finally handlers would be executed before our
        // exception filter.
        catch (Exception ex)
        {
            try
            {
                HandleUnhandledException(ex);

                // Ensure the task won't be set to completed until the application terminates.
                await new TaskCompletionSource().Task.ConfigureAwait(false);
            }
            catch (Exception ex2)
            {
                // Ensure the new exception isn't swallowed or delayed.
                Environment.FailFast(ex2.Message, ex2);
            }
        }
    }

    public static Task StartTask(Func<Task> asyncFunc)
    {
        return Task.Run(() => WrapTaskForHandlingUnhandledExceptions(asyncFunc));
    }

    /// <summary>
    /// Registers a first-chance exception handler that will ensure to terminate the
    /// application when an <see cref="OutOfMemoryException"/> occurs as a first-chance
    /// exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method should be called once on application start-up (e.g. in the <c>Main</c>
    /// method).
    /// </para>
    /// <para>
    /// Similar as to <see cref="CanCatch(Exception)"/>, this handler ensures that an
    /// <see cref="OutOfMemoryException"/> cannot be caught, and prevents finally
    /// handlers from running in such a case. This is because an OOME might be thrown
    /// at any place where an allocation can occur, and in most cases we are not able
    /// to guarantee that the application is still in a "good" (well-defined) state when
    /// this happens.
    /// Additionally, running finally handlers in case of an OOME (even if the OOME itself
    /// isn't caught) might cause unexpected behavior, like waiting for some task that
    /// will never complete, or releasing a lock in the current thread which might allow
    /// other threads to enter the lock (shortly before terminating the app) and see an
    /// inconsistent state.
    /// </para>
    /// <para>
    /// In contrast to using <see cref="CanCatch(Exception)"/> in an exception filter,
    /// registering a first-chance exception handler ensures that an
    /// <see cref="OutOfMemoryException"/> is also not caught in external code (like
    /// the .NET BCL or external libraries), as those might also not be prepared to
    /// handle them, or they might handle them in a way we don't expect (e.g. they
    /// might catch the exception so that the app doesn't crash, but then some
    /// functionality might no longer work).
    /// </para>
    /// <para>
    /// Additionally, this allows us to save us from reasoning about whether there are
    /// any <c>finally</c> handlers which we need to prevent from running in case of an
    /// <see cref="OutOfMemoryException"/>, by using an exception filter calling
    /// <see cref="CanCatch(Exception)"/>.
    /// </para>
    /// <para>
    /// Effectively, this means we will treat an <see cref="OutOfMemoryException"/> the
    /// same as other corrupted state exceptions like <see cref="StackOverflowException"/>
    /// and <see cref="AccessViolationException"/>, which also can't be caught in a .NET
    /// application.
    /// </para>
    /// </remarks>
    public static void RegisterFirstChanceOutOfMemoryExceptionHandler()
    {
        AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
        {
            // Ensure that an OOME will terminate the process.
            // Note: When the system is low on memory, the runtime might even
            // fail to allocate the OutOfMemoryException object (or the
            // FirstChanceExceptionEventArgs object).
            // We rely on the runtime terminating the app by itself in such a case.
            if (e.Exception is OutOfMemoryException)
                CanCatch(e.Exception);
        };
    }
}
