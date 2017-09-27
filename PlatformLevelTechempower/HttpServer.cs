using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Libuv;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Utf8Json;

namespace PlatformLevelTechempower
{
    public class HttpServer : IConnectionHandler
    {
        private static readonly byte[] _headerConnection = Encoding.ASCII.GetBytes("Connection");
        private static readonly byte[] _headerConnectionKeepAlive = Encoding.ASCII.GetBytes("keep-alive");
        //private static readonly byte[] _cheatersResponse = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!");

        private readonly Handler _handler;

        public HttpServer(Handler handler)
        {
            _handler = handler;
        }

        public async Task RunAsync(int port)
        {
            var lifetime = new ApplicationLifetime(NullLoggerFactory.Instance.CreateLogger<ApplicationLifetime>());

            Console.CancelKeyPress += (sender, e) => lifetime.StopApplication();

            var libuvOptions = new LibuvTransportOptions();
            var libuvTransport = new LibuvTransportFactory(
                Options.Create(libuvOptions),
                lifetime,
                NullLoggerFactory.Instance);

            var binding = new IPEndPointInformation(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));

            var transport = libuvTransport.Create(binding, this);
            await transport.BindAsync();

            Console.WriteLine($"Server listening on http://localhost:{port}");

            lifetime.ApplicationStopping.WaitHandle.WaitOne();

            await transport.UnbindAsync();
            await transport.StopAsync();
        }

        IConnectionContext IConnectionHandler.OnConnection(IConnectionInformation connectionInfo)
        {
            var inputOptions = new PipeOptions { WriterScheduler = connectionInfo.InputWriterScheduler };
            var outputOptions = new PipeOptions { ReaderScheduler = connectionInfo.OutputReaderScheduler };

            var context = new HttpConnectionContext(_handler)
            {
                ConnectionId = Guid.NewGuid().ToString(),
                Input = connectionInfo.PipeFactory.Create(inputOptions),
                Output = connectionInfo.PipeFactory.Create(outputOptions)
            };

            _ = context.ExecuteAsync();

            return context;
        }

        private class HttpConnectionContext : IConnectionContext, IHttpHeadersHandler, IHttpRequestLineHandler
        {
            private readonly Handler _handler;

            private State _state;

            private HttpMethod _method;
            private byte[] _path;
            private byte[] _query;
            private bool _keepAlive;

            public HttpConnectionContext(Handler handler)
            {
                _handler = handler;
            }

            public string ConnectionId { get; set; }

            public IPipe Input { get; set; }

            public IPipe Output { get; set; }

            IPipeWriter IConnectionContext.Input => Input.Writer;

            IPipeReader IConnectionContext.Output => Output.Reader;

            public void Abort(Exception ex)
            {

            }

            public void OnConnectionClosed(Exception ex)
            {

            }

            public async Task ExecuteAsync()
            {
                try
                {
                    var parser = new HttpParser<HttpConnectionContext>();

                    while (true)
                    {
                        var result = await Input.Reader.ReadAsync();
                        var inputBuffer = result.Buffer;
                        var consumed = inputBuffer.Start;
                        var examined = inputBuffer.End;

                        try
                        {
                            if (inputBuffer.IsEmpty && result.IsCompleted)
                            {
                                break;
                            }

                            ParseHttpRequest(parser, inputBuffer, out consumed, out examined);

                            if (_state != State.Body && result.IsCompleted)
                            {
                                // Bad request
                                break;
                            }

                            if (_state == State.Body)
                            {
                                var outputBuffer = Output.Writer.Alloc();

                                var status = await _handler.ProcessAsync(_method, _path, _query, _keepAlive, outputBuffer);

                                await outputBuffer.FlushAsync();

                                _state = State.StartLine;
                            }
                        }
                        finally
                        {
                            Input.Reader.Advance(consumed, examined);
                        }
                    }

                    Input.Reader.Complete();
                }
                catch (Exception ex)
                {
                    Input.Reader.Complete(ex);
                }
                finally
                {
                    Output.Writer.Complete();
                }
            }

            private void ParseHttpRequest(HttpParser<HttpConnectionContext> parser, ReadableBuffer inputBuffer, out ReadCursor consumed, out ReadCursor examined)
            {
                consumed = inputBuffer.Start;
                examined = inputBuffer.End;

                if (_state == State.StartLine)
                {
                    if (parser.ParseRequestLine(this, inputBuffer, out consumed, out examined))
                    {
                        _state = State.Headers;
                        inputBuffer = inputBuffer.Slice(consumed);
                    }
                }

                if (_state == State.Headers)
                {
                    if (parser.ParseHeaders(this, inputBuffer, out consumed, out examined, out int consumedBytes))
                    {
                        _state = State.Body;
                    }
                }
            }

            public void OnStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
            {
                _method = method;
                _path = path.ToArray();
                _query = query.ToArray();
            }

            public void OnHeader(Span<byte> name, Span<byte> value)
            {
                if (name.SequenceEqual(_headerConnection) && value.SequenceEqual(_headerConnectionKeepAlive))
                {
                    _keepAlive = true;
                }

                _handler.OnHeader(name, value);
            }

            private enum State
            {
                StartLine,
                Headers,
                Body
            }
        }

        public class IPEndPointInformation : IEndPointInformation
        {
            public IPEndPointInformation(System.Net.IPEndPoint endPoint)
            {
                IPEndPoint = endPoint;
            }

            public ListenType Type => ListenType.IPEndPoint;

            public System.Net.IPEndPoint IPEndPoint { get; set; }

            public string SocketPath => null;

            public ulong FileHandle => 0;

            public bool NoDelay { get; set; } = true;

            public FileHandleType HandleType { get; set; } = FileHandleType.Tcp;

            public override string ToString()
            {
                return IPEndPoint?.ToString();
            }
        }
    }

    public abstract class Handler
    {
        private static readonly byte[] _crlf = Encoding.ASCII.GetBytes("\r\n");
        private static readonly byte[] _http11StartLine = Encoding.ASCII.GetBytes("HTTP/1.1 ");

        private static readonly byte[] _headerServer = Encoding.ASCII.GetBytes("Server: Custom");
        private static readonly byte[] _headerDate = Encoding.ASCII.GetBytes("Date: ");
        private static readonly byte[] _headerContentLength = Encoding.ASCII.GetBytes("Content-Length: ");
        private static readonly byte[] _headerContentType = Encoding.ASCII.GetBytes("Content-Type: ");
        private static readonly byte[] _headerContentLengthZero = Encoding.ASCII.GetBytes("0");
        private static readonly byte[] _headerConnectionKeepAlive = Encoding.ASCII.GetBytes("Connection: keep-alive");

        private static readonly DateHeaderValueManager _dateHeaderValueManager = new DateHeaderValueManager();

        public virtual void OnHeader(Span<byte> name, Span<byte> value)
        {

        }

        public abstract Task<HttpStatus> ProcessAsync(HttpMethod method, byte[] path, byte[] query, bool keepAlive, WritableBuffer output);

        public bool PathMatch(byte[] path, byte[] target)
        {
            var pathSpan = new Span<byte>(path);

            return pathSpan.SequenceEqual(target);
        }

        public HttpStatus Ok(WritableBuffer output, bool keepAlive, byte[] body, MediaType mediaType)
        {
            WriteStartLine(output, HttpStatus.Ok);

            WriteCommonHeaders(output, keepAlive);

            WriteHeader(output, _headerContentType, mediaType.Value);
            WriteHeader(output, _headerContentLength, Encoding.ASCII.GetBytes(body.Length.ToString()));

            output.Write(_crlf);
            output.Write(body);

            return HttpStatus.Ok;
        }

        public HttpStatus Json<T>(WritableBuffer output, bool keepAlive, T value)
        {
            WriteStartLine(output, HttpStatus.Ok);

            WriteCommonHeaders(output, keepAlive);

            WriteHeader(output, _headerContentType, MediaType.ApplicationJson.Value);

            var body = JsonSerializer.Serialize(value);

            WriteHeader(output, _headerContentLength, Encoding.ASCII.GetBytes(body.Length.ToString()));

            output.Write(_crlf);
            output.Write(body);

            return HttpStatus.Ok;
        }

        public HttpStatus NotFound(WritableBuffer output, bool keepAlive)
        {
            return WriteResponse(output, keepAlive, HttpStatus.NotFound);
        }

        public HttpStatus BadRequest(WritableBuffer output, bool keepAlive)
        {
            return WriteResponse(output, keepAlive, HttpStatus.BadRequest);
        }

        public void WriteHeader(WritableBuffer output, byte[] name, byte[] value)
        {
            output.Write(name);
            output.Write(value);
            output.Write(_crlf);
        }

        private HttpStatus WriteResponse(WritableBuffer output, bool keepAlive, HttpStatus status)
        {
            WriteStartLine(output, status);

            WriteCommonHeaders(output, keepAlive);

            WriteHeader(output, _headerContentLength, _headerContentLengthZero);

            output.Write(_crlf);

            return status;
        }

        private static void WriteStartLine(WritableBuffer output, HttpStatus status)
        {
            output.Write(_http11StartLine);
            output.Write(status.Value);
            output.Write(_crlf);
        }

        private void WriteCommonHeaders(WritableBuffer output, bool keepAlive)
        {
            // Server headers
            output.Write(_headerServer);

            // Date header
            output.Write(_dateHeaderValueManager.GetDateHeaderValues().Bytes);
            output.Write(_crlf);

            if (keepAlive)
            {
                output.Write(_headerConnectionKeepAlive);
                output.Write(_crlf);
            }
        }
    }

    public struct HttpStatus
    {
        public static HttpStatus Ok = new HttpStatus(200, "OK");
        public static HttpStatus BadRequest = new HttpStatus(400, "BAD REQUEST");
        public static HttpStatus NotFound = new HttpStatus(404, "NOT FOUND");
        // etc.

        private readonly byte[] _value;

        private HttpStatus(int code, string message)
        {
            _value = Encoding.ASCII.GetBytes(code.ToString() + " " + message);
        }

        public byte[] Value => _value;
    }

    public struct MediaType
    {
        public static MediaType TextPlain = new MediaType("text/plain");
        public static MediaType ApplicationJson = new MediaType("application/json");
        // etc.

        private readonly byte[] _value;

        private MediaType(string value)
        {
            _value = Encoding.ASCII.GetBytes(value);
        }

        public byte[] Value => _value;
    }
}
