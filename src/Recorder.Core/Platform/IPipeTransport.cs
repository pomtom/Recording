namespace Recorder.Platform;

/// <summary>Creates the one-way channels that carry raw media into ffmpeg.</summary>
public interface IPipeTransport
{
    /// <summary>
    /// Creates a channel ffmpeg can open by path. It exists as soon as this returns, so it can be
    /// created before ffmpeg starts.
    /// </summary>
    /// <param name="name">A name unique to this recording.</param>
    /// <param name="bufferSize">Suggested write-side buffer, in bytes.</param>
    IPipeEndpoint Create(string name, int bufferSize);
}

/// <summary>The writing end of one channel into ffmpeg.</summary>
/// <remarks>Disposing closes the channel, which ffmpeg reads as end-of-stream.</remarks>
public interface IPipeEndpoint : IDisposable
{
    /// <summary>What to pass to ffmpeg's <c>-i</c> so it opens this channel.</summary>
    string Path { get; }

    /// <summary>Completes once ffmpeg has opened the reading end.</summary>
    Task WaitForConnectionAsync(CancellationToken cancellationToken);

    /// <summary>Writes all of <paramref name="data"/>, blocking while ffmpeg catches up.</summary>
    /// <remarks>Throws once ffmpeg has closed its end.</remarks>
    void Write(ReadOnlySpan<byte> data);
}
