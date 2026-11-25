using System.Net.Sockets;
using System.Text;

namespace NiriAutoStack;

/// <summary>
/// 直接连 niri 的 UNIX socket 收发 JSON。
///
/// 不走 niri msg 子进程:省掉每次 fork/exec 的开销(几十毫秒)和一个 Python 解释器常驻。
/// 连 StreamReader/StreamWriter 也不用,自己按字节切行 —— 少一整套 System.IO 文本机制。
///
/// 注意:一旦发出 EventStream 请求,这条连接就被事件流独占,不能再发别的请求。
/// 所以查询走另一条连接。
/// </summary>
internal sealed class NiriIpc : IDisposable
{
    private readonly Socket _socket;

    // 接收缓冲:[_start, _end) 之间是还没切走的字节。
    private byte[] _buf = new byte[16 * 1024];
    private int _start;
    private int _end;

    public NiriIpc(string socketPath)
    {
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        // 注意:UNIX domain socket 不支持 NoDelay(TCP_NODELAY),设了会报
        // SocketError.OperationNotSupported。
    }

    /// <summary>发一行请求(自带换行)。</summary>
    public void WriteLine(string s)
    {
        var body = Encoding.UTF8.GetBytes(s);
        var payload = new byte[body.Length + 1];
        body.CopyTo(payload, 0);
        payload[body.Length] = (byte)'\n';

        _socket.Send(payload, SocketFlags.None);
    }

    /// <summary>发一个请求并读回一行响应。</summary>
    public string Request(string json)
    {
        WriteLine(json);
        return ReadLine() ?? throw new IOException("niri IPC 关闭了连接");
    }

    /// <summary>读一行。对端关闭时返回 null。</summary>
    public string? ReadLine()
    {
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buf[i] != (byte)'\n')
                {
                    continue;
                }

                // UTF-8 的多字节序列不会出现 0x0A,所以按字节切行不会切坏字符。
                var line = Encoding.UTF8.GetString(_buf, _start, i - _start);
                _start = i + 1;
                return line;
            }

            if (_end == _buf.Length)
            {
                if (_start > 0)
                {
                    Array.Copy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }

                if (_end == _buf.Length)
                {
                    Array.Resize(ref _buf, _buf.Length * 2);
                }
            }

            var n = _socket.Receive(_buf, _end, _buf.Length - _end, SocketFlags.None);
            if (n == 0)
            {
                return null;
            }

            _end += n;
        }
    }

    /// <summary>发起事件流订阅。之后 ReadLine() 就会不断返回事件行。</summary>
    public void StartEventStream()
    {
        WriteLine("{\"EventStream\":null}");

        // 订阅确认行,先读掉。
        ReadLine();
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
