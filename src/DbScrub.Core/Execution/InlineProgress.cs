namespace DbScrub.Core.Execution;

/// <summary>
/// Reports progress on the calling thread, immediately, in order.
///
/// The obvious choice here is <see cref="Progress{T}"/>, and it is the wrong
/// one. <see cref="Progress{T}"/> exists to marshal callbacks onto a UI thread:
/// it captures the <see cref="SynchronizationContext"/> at construction and
/// posts to it. A console application and a test host both have NO
/// synchronization context, so it falls back to the thread pool — meaning
/// `Report` queues the write and returns before it has happened.
///
/// Two consequences, both real:
///
/// 1. **Progress lines can print out of order, or after the line they were
///    meant to precede.** "Masking dbo.Person" can land after "Verify passed".
///    In a tool whose console output is the thing an operator reads before
///    approving a destructive run, a transcript that misrepresents the order of
///    events is worse than no transcript.
///
/// 2. **Anything reading the written output races the writer.** A test doing
///    `writer.ToString()` after the command returns can catch a queued callback
///    mid-write, and `StringBuilder` is not thread-safe: the read throws
///    `ArgumentOutOfRangeException (chunkLength)`. That was a real intermittent
///    failure at roughly 3% of runs, on a different test each time, identical on
///    net8.0 and net10.0 — see DECISIONS.md D31.
///
/// Reporting inline fixes both. There is nothing to marshal to, the work is
/// already on the thread the caller cares about, and ordering becomes exactly
/// what the reader expects.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler
        ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
