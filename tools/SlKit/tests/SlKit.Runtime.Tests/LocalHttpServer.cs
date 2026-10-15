using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlKit.Runtime.Tests;

/// <summary>
/// A minimal HTTP/1.1 server on a loopback port. Raw sockets instead of HttpListener, so it needs
/// no URL reservation and behaves the same on every OS. It counts the requests that actually
/// ARRIVED per path, which is how a test tells "never sent" from "sent but slow".
/// Paths: <c>/slow/&lt;ms&gt;</c> answers after that many milliseconds, anything else at once.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly ConcurrentDictionary<string, int> _arrived = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    public string Url(string path) => "http://127.0.0.1:" + Port + path;

    /// <summary>Requests whose request line reached the server for <paramref name="path"/>.</summary>
    public int Arrived(string path) => _arrived.TryGetValue(path, out int n) ? n : 0;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // listener stopped
            }

            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (requestLine == null)
                {
                    return;
                }

                // Drain the headers; the tests send no bodies that matter.
                string header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                }

                string path = requestLine.Split(' ')[1];
                _arrived.AddOrUpdate(path, 1, (_, n) => n + 1);

                if (path.StartsWith("/slow/", StringComparison.Ordinal))
                {
                    await Task.Delay(int.Parse(path.Substring(6)), _stop.Token).ConfigureAwait(false);
                }

                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client gave up (timeout) or the server is stopping: nothing to answer.
            }
        }
    }
}
