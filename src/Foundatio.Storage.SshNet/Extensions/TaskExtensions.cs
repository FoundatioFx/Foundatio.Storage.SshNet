﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Foundatio.AsyncEx;

namespace Foundatio.Extensions;

internal static class TaskExtensions
{
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this Task<TResult> task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ConfiguredTaskAwaitable AnyContext(this Task task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this AwaitableDisposable<TResult> task) where TResult : IDisposable
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ConfiguredCancelableAsyncEnumerable<TResult> AnyContext<TResult>(this IAsyncEnumerable<TResult> task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }
}
