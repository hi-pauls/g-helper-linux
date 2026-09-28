using System.Runtime.InteropServices;

namespace GHelper.Linux.Peripherals.Logitech.HidPP;

/// <summary>
/// Raw hidraw stream with poll-based read timeout. Bypasses HidSharp for
/// devices that HidSharp cannot enumerate (Bluetooth HID).
/// </summary>
internal sealed class HidrawStream : Stream
{
    private const int O_RDWR = 0x02;
    private const int O_NONBLOCK = 0x800;
    private const short POLLIN = 0x01;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, uint nfds, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private int _fd = -1;
    private int _readTimeout = 2000;
    private int _writeTimeout = 2000;

    public override bool CanRead => _fd >= 0;
    public override bool CanWrite => _fd >= 0;
    public override bool CanSeek => false;
    public override bool CanTimeout => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout
    {
        get => _readTimeout;
        set => _readTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _writeTimeout;
        set => _writeTimeout = value;
    }

    /// <summary>Opens the hidraw device at the given path (e.g. /dev/hidraw8).</summary>
    public HidrawStream(string devicePath)
    {
        _fd = GHelper.Linux.USB.HidrawHelper.OpenDevice(devicePath, O_RDWR | O_NONBLOCK);
        if (_fd < 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Failed to open {devicePath}: errno {err}");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);

        var pfd = new PollFd { fd = _fd, events = POLLIN };
        int ret = poll(ref pfd, 1, _readTimeout);
        if (ret < 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"poll failed: errno {err}");
        }
        if (ret == 0)
            return 0; // timeout

        if ((pfd.revents & POLLIN) == 0)
            return 0;

        // Read into a temporary buffer, then copy to the caller's buffer at offset.
        byte[] tmp = new byte[count];
        nint n = read(_fd, tmp, (nuint)count);
        if (n < 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 11) // EAGAIN
                return 0;
            throw new IOException($"read failed: errno {err}");
        }

        Array.Copy(tmp, 0, buffer, offset, (int)n);
        return (int)n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);

        byte[] tmp;
        if (offset == 0)
        {
            tmp = buffer;
        }
        else
        {
            tmp = new byte[count];
            Array.Copy(buffer, offset, tmp, 0, count);
        }

        nint n = write(_fd, tmp, (nuint)count);
        if (n < 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"write failed: errno {err}");
        }
        if (n != count)
            throw new IOException($"Short write: {n}/{count}");
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_fd >= 0)
        {
            close(_fd);
            _fd = -1;
        }
        base.Dispose(disposing);
    }
}
