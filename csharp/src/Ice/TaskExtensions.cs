//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    internal static class TaskExtensions
    {
        internal static async ValueTask WaitAsync(this ValueTask task, CancellationToken cancel = default)
        {
            if (cancel.CanBeCanceled && !task.IsCompleted)
            {
                await Task.WhenAny(task.AsTask(), Task.Delay(-1, cancel)).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            await task.ConfigureAwait(false);
        }

        internal static async Task WaitAsync(this Task task, CancellationToken cancel = default)
        {
            if (cancel.CanBeCanceled && !task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(-1, cancel)).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            await task.ConfigureAwait(false);
        }

        internal static async ValueTask<T> WaitAsync<T>(this ValueTask<T> task, CancellationToken cancel = default)
        {
            if (cancel.CanBeCanceled && !task.IsCompleted)
            {
                await Task.WhenAny(task.AsTask(), Task.Delay(-1, cancel)).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            return await task.ConfigureAwait(false);
        }

        internal static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancel = default)
        {
            await ((Task)task).WaitAsync(cancel).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        internal static ValueTask WaitUntilDeadlineAsync(this ValueTask task, long deadline,
            CancellationToken cancel = default) => WaitWithTimeoutAsync(task, DeadlineToTimeout(deadline), cancel);

        internal static Task WaitUntilDeadlineAsync(this Task task, long deadline,
            CancellationToken cancel = default) => WaitWithTimeoutAsync(task, DeadlineToTimeout(deadline), cancel);

        internal static ValueTask<T> WaitUntilDeadlineAsync<T>(this ValueTask<T> task, long deadline,
            CancellationToken cancel = default) => WaitWithTimeoutAsync(task, DeadlineToTimeout(deadline), cancel);

        internal static Task<T> WaitUntilDeadlineAsync<T>(this Task<T> task, long deadline,
            CancellationToken cancel = default) => WaitWithTimeoutAsync(task, DeadlineToTimeout(deadline), cancel);

        internal static async ValueTask WaitWithTimeoutAsync(this ValueTask task, int timeout,
            CancellationToken cancel = default)
        {
            if ((timeout < 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
            }
            else if (timeout < 0)
            {
                await task.WaitAsync(cancel).ConfigureAwait(false);
            }
            else
            {
                await task.AsTask().WaitWithTimeoutAsync(timeout, cancel).ConfigureAwait(false);
            }
        }

        internal static async Task WaitWithTimeoutAsync(this Task task, int timeout, CancellationToken cancel = default)
        {
            if ((timeout < 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
            }
            else if (timeout < 0)
            {
                await task.WaitAsync(cancel).ConfigureAwait(false);
            }
            else
            {
                if (await Task.WhenAny(task, Task.Delay(timeout, cancel)).ConfigureAwait(false) != task)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new TimeoutException();
                }
                await task.ConfigureAwait(false);
            }
        }

        internal static async ValueTask<T> WaitWithTimeoutAsync<T>(this ValueTask<T> task, int timeout,
            CancellationToken cancel = default)
        {
            if ((timeout < 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            else if (timeout < 0)
            {
                return await task.WaitAsync(cancel).ConfigureAwait(false);
            }
            else
            {
                return await task.AsTask().WaitWithTimeoutAsync(timeout, cancel).ConfigureAwait(false);
            }
        }

        internal static async Task<T> WaitWithTimeoutAsync<T>(this Task<T> task, int timeout,
            CancellationToken cancel = default)
        {
            await WaitWithTimeoutAsync((Task)task, timeout, cancel).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        private static int DeadlineToTimeout(long deadline)
        {
            if (deadline > 0)
            {
                int timeout = (int)(deadline - Time.CurrentMonotonicTimeMillis());
                return timeout <= 0 ? 0 : timeout;
            }
            else
            {
                return -1;
            }
        }
    }
}
