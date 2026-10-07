using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Rasa.Web
{
    /// <summary>What the browser asked for.</summary>
    public class WebRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public IPAddress RemoteAddress { get; set; }

        private readonly Dictionary<string, string> _query = new(StringComparer.OrdinalIgnoreCase);

        public void SetQueryValue(string name, string value) => _query[name] = value;

        /// <summary>A value from after the '?', or null if it wasn't sent.</summary>
        public string GetQueryValue(string name) => _query.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>What we send back.</summary>
    public class WebResponse
    {
        public int StatusCode { get; set; } = 200;
        public string Body { get; set; } = "";
        public string RedirectLocation { get; set; }

        public static WebResponse Page(string html) => new WebResponse { Body = html };
        public static WebResponse Error(int statusCode, string message) => new WebResponse { StatusCode = statusCode, Body = WebUtility.HtmlEncode(message) };
        public static WebResponse Redirect(string location) => new WebResponse { StatusCode = 302, RedirectLocation = location };
    }

    public class WebServer
    {
        private const int MaxRequestBytes = 8192;
        private const int TimeoutMs = 5000;

        private readonly Dictionary<string, Func<WebRequest, WebResponse>> _routes = new(StringComparer.OrdinalIgnoreCase);
        private TcpListener _listener;
        private CancellationTokenSource _stopSource;

        /// <summary>Call the handler for any request to this path, e.g. "/oauth/callback".</summary>
        public void AddRoute(string path, Func<WebRequest, WebResponse> handler) => _routes[path] = handler;

        public bool Start(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (SocketException e)
            {
                Logger.WriteLog(LogType.Error, $"Web server could not open port {port}: {e.Message}");
                _listener = null;
                return false;
            }

            _stopSource = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _stopSource.Token);

            Logger.WriteLog(LogType.Network, "*** Web server listening on port {0}", port);
            return true;
        }

        public void Stop()
        {
            _stopSource?.Cancel();
            _listener?.Stop();
            _listener = null;
        }

        private async Task AcceptLoop(TcpListener listener, CancellationToken stopToken)
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(stopToken);
                }
                catch (Exception)
                {
                    return; // Stop() was called
                }

                _ = Task.Run(() => HandleConnection(client));
            }
        }

        private void HandleConnection(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = TimeoutMs;
                    client.SendTimeout = TimeoutMs;

                    var stream = client.GetStream();
                    var request = ReadRequest(stream, (client.Client.RemoteEndPoint as IPEndPoint)?.Address);

                    WriteResponse(stream, AnswerRequest(request));
                }
                catch (IOException)
                {
                    // A browser's spare connection that was never used, or one that went away:
                    // it timed out or closed without asking for anything. Nothing to answer.
                }
                catch (Exception e)
                {
                    Logger.WriteLog(LogType.Error, $"Web server: {e.Message}");
                }
            }
        }

        private WebResponse AnswerRequest(WebRequest request)
        {
            if (request == null)
                return WebResponse.Error(400, "Bad request");

            if (!_routes.TryGetValue(request.Path, out var handler))
                return WebResponse.Error(404, "Not found");

            try
            {
                return handler(request);
            }
            catch (Exception e)
            {
                Logger.WriteLog(LogType.Error, $"Web server: {request.Path} failed: {e}");
                return WebResponse.Error(500, "Something went wrong");
            }
        }

        private static WebRequest ReadRequest(NetworkStream stream, IPAddress remoteAddress)
        {
            // Read until the blank line that ends the headers.
            var buffer = new byte[MaxRequestBytes];
            var length = 0;

            while (length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                    break;

                length += read;

                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n"))
                    break;
            }

            // Closed without sending a single byte: the same unused spare connection, closed instead of left open.
            if (length == 0)
                throw new IOException("Connection closed without a request.");

            // First line looks like: GET /oauth/callback?code=abc&state=xyz HTTP/1.1
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0)
                return null;

            var parts = text.Substring(0, firstLineEnd).Split(' ');
            if (parts.Length != 3)
                return null;

            var request = new WebRequest { Method = parts[0], RemoteAddress = remoteAddress };
            var target = parts[1];
            var questionMark = target.IndexOf('?');

            request.Path = questionMark < 0 ? target : target.Substring(0, questionMark);

            if (questionMark >= 0)
            {
                foreach (var pair in target.Substring(questionMark + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = pair.IndexOf('=');
                    var name = equals < 0 ? pair : pair.Substring(0, equals);
                    var value = equals < 0 ? "" : pair.Substring(equals + 1);

                    request.SetQueryValue(Decode(name), Decode(value));
                }
            }

            return request;
        }

        private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

        private static void WriteResponse(NetworkStream stream, WebResponse response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body ?? "");
            var head = new StringBuilder();

            head.Append($"HTTP/1.1 {response.StatusCode} {StatusText(response.StatusCode)}\r\n");
            head.Append("Content-Type: text/html; charset=utf-8\r\n");
            head.Append($"Content-Length: {body.Length}\r\n");
            if (response.RedirectLocation != null)
                head.Append($"Location: {response.RedirectLocation}\r\n");
            head.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static string StatusText(int statusCode) => statusCode switch
        {
            200 => "OK",
            302 => "Found",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Internal Server Error"
        };
    }
}
