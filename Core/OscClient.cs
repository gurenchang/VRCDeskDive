using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VRCDeskDive.Core;

/// <summary>VRChat へ OSC メッセージを UDP で送る最小実装。</summary>
public sealed class OscClient : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly object _lock = new();
    private readonly IPEndPoint _target;

    public OscClient(string host, int port)
    {
        _target = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback, port);
    }

    public string Target => _target.ToString();

    public void Send(string address, params object[] args)
    {
        var packet = Encode(address, args);
        lock (_lock)
        {
            try
            {
                _udp.Send(packet, packet.Length, _target);
            }
            catch (SocketException)
            {
                // VRChat 未起動などで送れなくても無視する
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public static byte[] Encode(string address, object[] args)
    {
        using var ms = new MemoryStream();
        WriteString(ms, address);

        var tags = new StringBuilder(",");
        foreach (var arg in args)
        {
            tags.Append(arg switch
            {
                int => 'i',
                float => 'f',
                string => 's',
                bool b => b ? 'T' : 'F',
                _ => throw new ArgumentException($"未対応の OSC 引数型: {arg.GetType()}"),
            });
        }
        WriteString(ms, tags.ToString());

        Span<byte> buf = stackalloc byte[4];
        foreach (var arg in args)
        {
            switch (arg)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(buf, i);
                    ms.Write(buf);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(buf, f);
                    ms.Write(buf);
                    break;
                case string s:
                    WriteString(ms, s);
                    break;
            }
        }
        return ms.ToArray();
    }

    // OSC 文字列は NUL 終端 + 4 バイト境界までパディング
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.Write(new byte[4 - bytes.Length % 4]);
    }

    public void Dispose()
    {
        lock (_lock) _udp.Dispose();
    }
}
