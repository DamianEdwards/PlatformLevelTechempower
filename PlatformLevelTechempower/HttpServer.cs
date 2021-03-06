﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    public class HttpServer<THandler> : IConnectionHandler, IServerApplication where THandler : HttpHandler, new()
    {
        private static readonly byte[] _headerConnection = Encoding.ASCII.GetBytes("Connection");
        private static readonly byte[] _headerConnectionKeepAlive = Encoding.ASCII.GetBytes("keep-alive");

        public static readonly int DefaultThreadCount = Environment.ProcessorCount;

        public Task RunAsync(int port) => RunAsync(port, DefaultThreadCount);

        public async Task RunAsync(int port, int threadCount)
        {
            var lifetime = new ApplicationLifetime(NullLoggerFactory.Instance.CreateLogger<ApplicationLifetime>());

            Console.CancelKeyPress += (sender, e) => lifetime.StopApplication();

            var libuvOptions = new LibuvTransportOptions
            {
                ThreadCount = threadCount
            };

            var libuvTransport = new LibuvTransportFactory(
                Options.Create(libuvOptions),
                lifetime,
                NullLoggerFactory.Instance);

            var binding = new IPEndPointInformation(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));

            var transport = libuvTransport.Create(binding, this);
            await transport.BindAsync();

            Console.WriteLine($"Server listening on http://*:{port} with {libuvOptions.ThreadCount} thread(s)");

            lifetime.ApplicationStopping.WaitHandle.WaitOne();

            await transport.UnbindAsync();
            await transport.StopAsync();
        }

        IConnectionContext IConnectionHandler.OnConnection(IConnectionInformation connectionInfo)
        {
            var inputOptions = new PipeOptions { WriterScheduler = InlineScheduler.Default };
            var outputOptions = new PipeOptions { ReaderScheduler = InlineScheduler.Default };

            var context = new HttpConnectionContext<THandler>
            {
                ConnectionId = Guid.NewGuid().ToString(),
                Input = connectionInfo.PipeFactory.Create(inputOptions),
                Output = connectionInfo.PipeFactory.Create(outputOptions)
            };

            _ = context.ExecuteAsync();

            return context;
        }

        private class HttpConnectionContext<THandlerInner> : IConnectionContext, IHttpHeadersHandler, IHttpRequestLineHandler
            where THandlerInner : HttpHandler, new()
        {
            private HttpHandler _handler;

            private State _state;

            public HttpConnectionContext()
            {
                _handler = new THandlerInner();
            }

            public string ConnectionId { get; set; }

            public IPipe Input { get; set; }

            public IPipe Output { get; set; }

            IPipeWriter IConnectionContext.Input => Input.Writer;

            IPipeReader IConnectionContext.Output => Output.Reader;

            public void Abort(Exception ex)
            {
                _handler = null;
            }

            public void OnConnectionClosed(Exception ex)
            {
                _handler = null;
            }

            public async Task ExecuteAsync()
            {
                try
                {
                    var parser = new HttpParser<HttpConnectionContext<THandlerInner>>();

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

                                _handler.Output = outputBuffer;

                                await _handler.ProcessAsync();

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

            private void ParseHttpRequest(HttpParser<HttpConnectionContext<THandlerInner>> parser, ReadableBuffer inputBuffer, out ReadCursor consumed, out ReadCursor examined)
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
                _handler.HandleStartLine(method, version, target, path, query, customMethod, pathEncoded);
            }

            public void OnHeader(Span<byte> name, Span<byte> value)
            {
                if (name.SequenceEqual(_headerConnection) && value.SequenceEqual(_headerConnectionKeepAlive))
                {
                    _handler.KeepAlive = true;
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

    public abstract class HttpHandler
    {
        private static readonly byte[] _crlf = Encoding.ASCII.GetBytes("\r\n");
        private static readonly byte[] _http11StartLine = Encoding.ASCII.GetBytes("HTTP/1.1 ");

        private static readonly byte[] _headerServer = Encoding.ASCII.GetBytes("Server: Kestrel");
        private static readonly byte[] _headerContentLength = Encoding.ASCII.GetBytes("Content-Length: ");
        private static readonly byte[] _headerContentType = Encoding.ASCII.GetBytes("Content-Type: ");
        private static readonly byte[] _headerConnectionKeepAlive = Encoding.ASCII.GetBytes("Connection: keep-alive");

        private static readonly DateHeaderValueManager _dateHeaderValueManager = new DateHeaderValueManager();

        private readonly byte[] _pathFixedBuffer = new byte[128];
        private byte[] _pathLargeBuffer;
        private int _pathLength;

        private readonly byte[] _queryFixedBuffer = new byte[128];
        private byte[] _queryLargeBuffer;
        private int _queryLength;

        public HttpMethod Method { get; set; }

        public Span<byte> Path => _pathLargeBuffer != null ? _pathLargeBuffer.AsSpan() : new Span<byte>(_pathFixedBuffer, 0, _pathLength);

        public Span<byte> Query => _queryLargeBuffer != null ? _queryLargeBuffer.AsSpan() : new Span<byte>(_queryFixedBuffer, 0, _queryLength);

        public bool KeepAlive { get; set; }

        internal WritableBuffer Output { get; set; }

        internal void HandleStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
        {
            Method = method;

            if (path.Length > _pathFixedBuffer.Length)
            {
                _pathLargeBuffer = path.ToArray();
            }
            else
            {
                _pathLargeBuffer = null;
                Array.Clear(_pathFixedBuffer, 0, _pathLength);
                path.CopyTo(_pathFixedBuffer);
            }
            _pathLength = path.Length;

            if (query.Length > _queryFixedBuffer.Length)
            {
                _queryLargeBuffer = query.ToArray();
            }
            else
            {
                _queryLargeBuffer = null;
                Array.Clear(_queryFixedBuffer, 0, _queryLength);
                path.CopyTo(_queryFixedBuffer);
            }
            _queryLength = query.Length;

            OnStartLine(method, version, target, path, query, customMethod, pathEncoded);
        }

        public virtual void OnStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
        {

        }

        public virtual void OnHeader(Span<byte> name, Span<byte> value)
        {

        }

        public abstract Task ProcessAsync();

        public bool PathMatch(byte[] target)
        {
            return Path.SequenceEqual(target);
        }

        public void Ok(byte[] body, MediaType mediaType)
        {
            WriteStartLine(HttpStatus.Ok);

            WriteCommonHeaders();

            WriteHeader(_headerContentType, mediaType.Value);
            WriteHeader(_headerContentLength, (ulong)body.Length);

            Output.Write(_crlf);
            Output.Write(body);
        }

        public void Json<T>(T value)
        {
            WriteStartLine(HttpStatus.Ok);

            WriteCommonHeaders();

            WriteHeader(_headerContentType, MediaType.ApplicationJson.Value);

            var body = JsonSerializer.SerializeUnsafe(value);
            WriteHeader(_headerContentLength, (ulong)body.Count);

            Output.Write(_crlf);
            Output.Write(body.Array, body.Offset, body.Count);
        }

        public void NotFound()
        {
            WriteResponse(HttpStatus.NotFound);
        }

        public void BadRequest()
        {
            WriteResponse(HttpStatus.BadRequest);
        }

        public void WriteHeader(byte[] name, ulong value)
        {
            Output.Write(name);
            var output = new WritableBufferWriter(Output);
            PipelineExtensions.WriteNumeric(ref output, value);
            Output.Write(_crlf);
        }

        public void WriteHeader(byte[] name, byte[] value)
        {
            Output.Write(name);
            Output.Write(value);
            Output.Write(_crlf);
        }

        private void WriteResponse(HttpStatus status)
        {
            WriteStartLine(status);
            WriteCommonHeaders();
            WriteHeader(_headerContentLength, 0);
            Output.Write(_crlf);
        }

        private void WriteStartLine(HttpStatus status)
        {
            Output.Write(_http11StartLine);
            Output.Write(status.Value);
            Output.Write(_crlf);
        }

        private void WriteCommonHeaders()
        {
            // Server headers
            Output.Write(_headerServer);

            // Date header
            Output.Write(_dateHeaderValueManager.GetDateHeaderValues().Bytes);
            Output.Write(_crlf);

            if (KeepAlive)
            {
                Output.Write(_headerConnectionKeepAlive);
                Output.Write(_crlf);
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
