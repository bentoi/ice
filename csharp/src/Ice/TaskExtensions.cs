//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    public static class TaskExtensions
    {
        public static async ValueTask WaitAsync(this ValueTask task, CancellationToken cancel = default)
        {
            if (!cancel.CanBeCanceled || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
            }
            else
            {
                await task.AsTask().WaitAsync(cancel).ConfigureAwait(false);
            }
        }

        public static async Task WaitAsync(this Task task, CancellationToken cancel = default)
        {
            if (cancel.CanBeCanceled)
            {
                cancel.ThrowIfCancellationRequested();
                var cancelTaskSource = new TaskCompletionSource<bool>();
                Task<bool> cancelTask = cancelTaskSource.Task;
                using (cancel.Register(() => cancelTaskSource.SetResult(true)))
                {
                    await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                }
            }
            await task.ConfigureAwait(false);
        }

        public static async ValueTask<T> WaitAsync<T>(this ValueTask<T> task, CancellationToken cancel = default)
        {
            if (!cancel.CanBeCanceled || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            else
            {
                return await task.AsTask().WaitAsync(cancel).ConfigureAwait(false);
            }
        }

        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancel = default)
        {
            if (!cancel.CanBeCanceled)
            {
                return await task.ConfigureAwait(false);
            }
            else
            {
                cancel.ThrowIfCancellationRequested();
                var cancelTaskSource = new TaskCompletionSource<bool>();
                Task<bool> cancelTask = cancelTaskSource.Task;
                using (cancel.Register(() => cancelTaskSource.SetResult(true)))
                {
                    await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                }
            }
            return await task.ConfigureAwait(false);
        }

        public static ValueTask WaitUntilDeadlineAsync(this ValueTask task, long deadline,
            CancellationToken cancel = default)
        {
            int timeout = 0;
            if (deadline > 0)
            {
                timeout = (int)(deadline - Time.CurrentMonotonicTimeMillis());
                if (timeout <= 0)
                {
                    throw new TimeoutException();
                }
            }
            return WaitWithTimeoutAsync(task, timeout, cancel);
        }

        public static Task WaitUntilDeadlineAsync(this Task task, long deadline, CancellationToken cancel = default)
        {
            int timeout = 0;
            if (deadline > 0)
            {
                timeout = (int)(deadline - Time.CurrentMonotonicTimeMillis());
                if (timeout <= 0)
                {
                    throw new TimeoutException();
                }
            }
            return WaitWithTimeoutAsync(task, timeout, cancel);
        }

        public static ValueTask<T> WaitUntilDeadlineAsync<T>(this ValueTask<T> task, long deadline,
            CancellationToken cancel = default)
        {
            int timeout = 0;
            if (deadline > 0)
            {
                timeout = (int)(deadline - Time.CurrentMonotonicTimeMillis());
                if (timeout <= 0)
                {
                    throw new TimeoutException();
                }
            }
            return WaitWithTimeoutAsync(task, timeout, cancel);
        }

        public static Task<T> WaitUntilDeadlineAsync<T>(this Task<T> task, long deadline, CancellationToken cancel = default)
        {
            int timeout = 0;
            if (deadline > 0)
            {
                timeout = (int)(deadline - Time.CurrentMonotonicTimeMillis());
                if (timeout <= 0)
                {
                    throw new TimeoutException();
                }
            }
            return WaitWithTimeoutAsync(task, timeout, cancel);
        }

        public static async ValueTask WaitWithTimeoutAsync(this ValueTask task, int timeout,
            CancellationToken cancel = default)
        {
            if ((timeout <= 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
            }
            else if (timeout <= 0)
            {
                await task.WaitAsync(cancel);
            }
            else
            {
                await task.AsTask().WaitWithTimeoutAsync(timeout, cancel).ConfigureAwait(false);
            }
        }

        public static async Task WaitWithTimeoutAsync(this Task task, int timeout, CancellationToken cancel = default)
        {
            if ((timeout <= 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
            }
            else if (timeout <= 0)
            {
                await task.WaitAsync(cancel);
            }
            else if (cancel.CanBeCanceled)
            {
                cancel.ThrowIfCancellationRequested();
                var cancelTaskSource = new TaskCompletionSource<bool>();
                Task<bool> cancelTask = cancelTaskSource.Task;
                using (cancel.Register(() => cancelTaskSource.SetResult(true)))
                {
                    if (await Task.WhenAny(task, cancelTask, Task.Delay(timeout)).ConfigureAwait(false) != task)
                    {
                        cancel.ThrowIfCancellationRequested();
                        throw new TimeoutException();
                    }
                }
            }
            else
            {
                if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
                {
                    throw new TimeoutException();
                }
            }

            await task.ConfigureAwait(false);
        }

        public static async ValueTask<T> WaitWithTimeoutAsync<T>(this ValueTask<T> task, int timeout,
            CancellationToken cancel = default)
        {
            if ((timeout <= 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            else if (timeout <= 0)
            {
                return await task.WaitAsync(cancel);
            }
            else
            {
                return await task.AsTask().WaitWithTimeoutAsync(timeout, cancel).ConfigureAwait(false);
            }
        }

        public static async Task<T> WaitWithTimeoutAsync<T>(this Task<T> task, int timeout,
            CancellationToken cancel = default)
        {
            if ((timeout <= 0 && !cancel.CanBeCanceled) || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            else if (timeout <= 0)
            {
                return await task.WaitAsync(cancel);
            }
            else if (cancel.CanBeCanceled)
            {
                cancel.ThrowIfCancellationRequested();
                var cancelTaskSource = new TaskCompletionSource<bool>();
                Task<bool> cancelTask = cancelTaskSource.Task;
                using (cancel.Register(() => cancelTaskSource.SetResult(true)))
                {
                    if (await Task.WhenAny(task, cancelTask, Task.Delay(timeout)).ConfigureAwait(false) != task)
                    {
                        cancel.ThrowIfCancellationRequested();
                        throw new TimeoutException();
                    }
                }
            }
            else
            {
                if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
                {
                    throw new TimeoutException();
                }
            }

            return await task.ConfigureAwait(false);
        }
    }
}
