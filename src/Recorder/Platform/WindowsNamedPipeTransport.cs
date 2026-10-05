using System.IO.Pipes;

namespace Recorder.Platform;

/// <summary>
/// Feeds ffmpeg through Windows named pipes, which it opens as <c>\\.\pipe\name</c>.
/// </summary>
public sealed class WindowsNamedPipeTransport : IPipeTransport
{
    public IPipeEndpoint Create(string name, int bufferSize) => new Endpoint(name, bufferSize);

    private sealed class Endpoint : IPipeEndpoint
    {
        private readonly NamedPipeServerStream _pipe;

        public Endpoint(string name, int bufferSize)
        {
            _pipe = new NamedPipeServerStream(
                name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: bufferSize);
            Path = $@"\\.\pipe\{name}";
        }

        public string Path { get; }

        public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
            _pipe.WaitForConnectionAsync(cancellationToken);

        public void Write(ReadOnlySpan<byte> data) => _pipe.Write(data);

        public void Dispose() => _pipe.Dispose();
    }
}
