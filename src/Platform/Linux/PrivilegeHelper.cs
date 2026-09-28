using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Client for ghelperd (vendor/ghelperd), the optional privilege-separated
/// helper. When it is installed the app runs without any hardware permissions of
/// its own: sysfs writes, vendor HID nodes, ASUS hotkeys and the root helpers all
/// go through the daemon, which runs as its own account and is the only one the
/// udev rules and sudoers grant anything to. Without it the app keeps upstream's
/// direct access, so call sites try direct access first and ask the helper only
/// when the kernel refuses.
/// </summary>
public static class PrivilegeHelper
{
    public const string SocketPath = "/run/ghelper/ghelperd.sock";
    public const string RunPath = "/opt/ghelper/ghelper-run";

    /// <summary>Stderr prefix of ghelper-run refusals, treated like "sudo:".</summary>
    public const string RefusalPrefix = "ghelper-run:";

    private static bool? _available;
    private static readonly object _lock = new();

    /// <summary>Whether ghelperd is installed and answers. Checked once; call
    /// <see cref="Invalidate"/> after installing or removing it.</summary>
    public static bool Available
    {
        get
        {
            lock (_lock)
            {
                _available ??= File.Exists(SocketPath) && Ping();
                return _available.Value;
            }
        }
    }

    public static void Invalidate()
    {
        lock (_lock)
            _available = null;
    }

    /// <summary>
    /// The argv that runs a root helper: through ghelper-run when the daemon is
    /// available, else upstream's sudo -n.
    /// </summary>
    public static (string file, string[] args) Escalate(string command, string[] args)
    {
        if (Available && File.Exists(RunPath))
            return (RunPath, [command, .. args]);
        return (SysfsHelper.SudoPath, ["-n", command, .. args]);
    }

    public static bool TryWrite(string path, string value, out string error)
    {
        var reply = Request("write", path, value);
        error = reply.Length >= 3 && reply[0] == "err" ? reply[2] : reply.Length == 0 ? "no reply" : "";
        return reply.Length >= 1 && reply[0] == "ok";
    }

    public static string? Read(string path)
    {
        var reply = Request("read", path);
        return reply.Length >= 2 && reply[0] == "ok" ? reply[1] : null;
    }

    /// <summary>A hotkey stream: 24-byte input_event packets, typing filtered
    /// out by the daemon. Null when the daemon has no hotkey device.</summary>
    public static Stream? OpenHotkeys()
    {
        var socket = Connect();
        if (socket == null)
            return null;
        try
        {
            socket.Send(Encoding.UTF8.GetBytes("hotkeys"));
            var reply = Receive(socket);
            if (reply.Length >= 1 && reply[0] == "ok")
                return new PacketStream(socket);
            Helpers.Logger.WriteLine($"PrivilegeHelper: hotkeys refused: {string.Join(" ", reply.Skip(2))}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("PrivilegeHelper: hotkeys failed", ex);
        }
        socket.Dispose();
        return null;
    }

    // Enumeration probes every node, read-write then read-only; a refusal
    // (foreign vendor, keyboard collection) does not change, so ask once.
    private static readonly HashSet<string> _refusedHidraw = new();

    /// <summary>Open a vendor HID node through the daemon. Returns the fd, or -1
    /// when refused (keyboard collections and foreign vendors are).</summary>
    public static int OpenHidraw(string path)
    {
        lock (_refusedHidraw)
            if (_refusedHidraw.Contains(path))
                return -1;
        using var socket = Connect();
        if (socket == null)
            return -1;
        try
        {
            socket.Send(Encoding.UTF8.GetBytes("hidraw\0" + path));
            var (reply, fd) = ReceiveWithFd(socket);
            if (reply.Length >= 1 && reply[0] == "ok" && fd >= 0)
                return fd;
            lock (_refusedHidraw)
                _refusedHidraw.Add(path);
            Helpers.Logger.WriteLine($"PrivilegeHelper: hidraw {path} refused: {string.Join(" ", reply.Skip(2))}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"PrivilegeHelper: hidraw {path} failed", ex);
        }
        return -1;
    }

    private static bool Ping()
    {
        var reply = Request("ping");
        bool ok = reply.Length >= 1 && reply[0] == "ok";
        Helpers.Logger.WriteLine(ok
            ? $"PrivilegeHelper: ghelperd {reply.ElementAtOrDefault(1)} available"
            : $"PrivilegeHelper: {SocketPath} present but ghelperd refused: {string.Join(" ", reply.Skip(2))}");
        return ok;
    }

    private static string[] Request(params string[] fields)
    {
        using var socket = Connect();
        if (socket == null)
            return [];
        try
        {
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\0', fields)));
            return Receive(socket);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"PrivilegeHelper: {fields[0]} failed", ex);
            return [];
        }
    }

    private static Socket? Connect()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Seqpacket, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static string[] Receive(Socket socket)
    {
        var buffer = new byte[8192];
        int n = socket.Receive(buffer);
        return Split(buffer, n);
    }

    private static string[] Split(byte[] buffer, int length) =>
        length <= 0 ? [] : Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\0').Split('\0');

    /// <summary>Read-only stream over a SOCK_SEQPACKET socket; NetworkStream
    /// accepts stream sockets only. One Read returns at most one packet.</summary>
    private sealed class PacketStream(Socket socket) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => socket.Receive(buffer, offset, count, SocketFlags.None);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                socket.Dispose();
            base.Dispose(disposing);
        }
    }

    #region SCM_RIGHTS receive

    // x86_64 / aarch64 LP64 layouts of struct msghdr, iovec and cmsghdr.
    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public nint Name;
        public uint NameLength;
        public nint Iov;
        public nuint IovLength;
        public nint Control;
        public nuint ControlLength;
        public int Flags;
    }

    private const int SolSocket = 1;
    private const int ScmRights = 1;
    private const int CmsgHeaderSize = 16;
    private const int CmsgSpaceOneFd = 24;

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(nint sockfd, ref MsgHdr msg, int flags);

    private static unsafe (string[] reply, int fd) ReceiveWithFd(Socket socket)
    {
        var buffer = new byte[8192];
        var control = new byte[CmsgSpaceOneFd];
        fixed (byte* data = buffer)
        fixed (byte* ctrl = control)
        {
            var iov = new IoVec { Base = (nint)data, Length = (nuint)buffer.Length };
            var msg = new MsgHdr
            {
                Iov = (nint)(&iov),
                IovLength = 1,
                Control = (nint)ctrl,
                ControlLength = (nuint)control.Length,
            };
            nint n = recvmsg(socket.Handle, ref msg, 0);
            if (n <= 0)
                return ([], -1);

            int fd = -1;
            long cmsgLength = BitConverter.ToInt64(control, 0);
            int level = BitConverter.ToInt32(control, 8);
            int type = BitConverter.ToInt32(control, 12);
            if (msg.ControlLength >= CmsgHeaderSize + sizeof(int) && cmsgLength >= CmsgHeaderSize + sizeof(int)
                && level == SolSocket && type == ScmRights)
                fd = BitConverter.ToInt32(control, CmsgHeaderSize);
            return (Split(buffer, (int)n), fd);
        }
    }

    #endregion
}
