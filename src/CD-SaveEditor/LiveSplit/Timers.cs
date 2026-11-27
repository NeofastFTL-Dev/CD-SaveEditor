using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CDSaveEditor.LiveSplit;

/// <summary>Matches LiveSplit's control protocol: splitOrStart and reset.</summary>
public interface ITimer : IDisposable
{
    void Send(string command);
}

/// <summary>Prints the commands instead of sending them.</summary>
public sealed class PrinterTimer : ITimer
{
    public void Send(string command) => Console.WriteLine($"{Splitter.Stamp()}  -> {command}");

    public void Dispose()
    {
    }
}

/// <summary>LiveSplit's internal server (Control -> Start TCP Server).</summary>
public sealed class TcpTimer : ITimer
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["splitOrStart"] = "startorsplit",
        ["reset"] = "reset",
    };

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private bool _warned;

    public TcpTimer(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Send(string command)
    {
        var line = Encoding.ASCII.GetBytes(Names[command] + "\n");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_client is null)
                {
                    var client = new TcpClient();
                    if (!client.ConnectAsync(_host, _port).Wait(TimeSpan.FromSeconds(3)))
                    {
                        client.Dispose();
                        throw new IOException("connection timed out");
                    }
                    _client = client;
                }
                var stream = _client.GetStream();
                stream.Write(line);
                stream.Flush();
                _warned = false;
                return;
            }
            catch (Exception exception) when (exception is SocketException or IOException
                                                  or AggregateException or ObjectDisposedException)
            {
                _client?.Dispose();
                _client = null;
                if (attempt == 1 && !_warned)
                {
                    Console.WriteLine($"livesplit: cannot reach {_host}:{_port} ({exception.Message})");
                    _warned = true;
                }
            }
        }
    }

    public void Dispose() => _client?.Dispose();
}

/// <summary>WebSocket server for LiveSplit One's "Connect to Server".</summary>
public sealed class WebSocketTimer : ITimer
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();
    private volatile bool _stopped;

    public WebSocketTimer(string host, int port)
    {
        var address = IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;
        _listener = new TcpListener(address, port);
        _listener.Start(4);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Console.WriteLine($"waiting for LiveSplit One on ws://{host}:{Port}");

        var accept = new Thread(AcceptLoop) { IsBackground = true };
        accept.Start();
    }

    /// <summary>The port that was actually bound (useful when 0 was requested).</summary>
    public int Port { get; }

    public bool WaitForClient(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            lock (_lock)
            {
                if (_clients.Count > 0)
                {
                    return true;
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }

    public void Send(string command)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command }));
        TcpClient[] clients;
        lock (_lock)
        {
            clients = _clients.ToArray();
        }
        foreach (var client in clients)
        {
            try
            {
                WriteFrame(client.GetStream(), payload);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                Drop(client);
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // already closed
        }
        TcpClient[] clients;
        lock (_lock)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                                  or InvalidOperationException)
            {
                return;
            }
            var worker = new Thread(() => Serve(client)) { IsBackground = true };
            worker.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[4096];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    client.Dispose();
                    return;
                }
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            var key = Regex.Match(request.ToString(), @"Sec-WebSocket-Key:\s*(\S+)");
            if (!key.Success)
            {
                client.Dispose();
                return;
            }
            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key.Groups[1].Value + WebSocketGuid)));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            stream.Write(response);
            stream.Flush();
            lock (_lock)
            {
                _clients.Add(client);
            }
            Console.WriteLine("LiveSplit One connected");
            Drain(stream);
        }
        catch (Exception exception) when (exception is IOException or SocketException
                                              or ObjectDisposedException)
        {
            // the timer went away
        }
        finally
        {
            Drop(client);
        }
    }

    /// <summary>Read frames until the timer disconnects (commands only ever go out).</summary>
    private void Drain(Stream stream)
    {
        while (true)
        {
            var head = new byte[2];
            if (!ReadExact(stream, head))
            {
                return;
            }
            var opcode = head[0] & 0x0F;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                var extended = new byte[2];
                if (!ReadExact(stream, extended))
                {
                    return;
                }
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                var extended = new byte[8];
                if (!ReadExact(stream, extended))
                {
                    return;
                }
                length = 0;
                foreach (var b in extended)
                {
                    length = (length << 8) | b;
                }
            }
            var mask = new byte[4];
            if ((head[1] & 0x80) != 0 && !ReadExact(stream, mask))
            {
                return;
            }
            var payload = new byte[length];
            if (length > 0 && !ReadExact(stream, payload))
            {
                return;
            }
            if ((head[1] & 0x80) != 0)
            {
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i % 4];
                }
            }
            if (opcode == 0x8)
            {
                return;
            }
            if (opcode == 0x9)
            {
                WriteFrame(stream, payload, 0xA);
            }
        }
    }

    private static void WriteFrame(Stream stream, byte[] payload, byte opcode = 0x1)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126)
        {
            header.Add((byte)payload.Length);
        }
        else if (payload.Length < 65536)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            header.Add(127);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                header.Add((byte)((long)payload.Length >> shift));
            }
        }
        stream.Write(header.ToArray());
        stream.Write(payload);
        stream.Flush();
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = stream.Read(buffer, done, buffer.Length - done);
            if (read == 0)
            {
                return false;
            }
            done += read;
        }
        return true;
    }

    private void Drop(TcpClient client)
    {
        lock (_lock)
        {
            _clients.Remove(client);
        }
        client.Dispose();
    }
}
