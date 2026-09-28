/* ghelperd - privilege-separated hardware helper for G-Helper.
 *
 * Runs as its own system account ("ghelper"). The desktop app runs unprivileged
 * and asks this daemon for the few hardware operations it needs:
 *
 *   ping                          liveness and protocol version
 *   write <path> <value>          write a sysfs attribute
 *   read <path>                   read a sysfs attribute (for group-only files)
 *   hidraw <path>                 open a vendor HID node, fd passed back
 *   hotkeys                       stream the ASUS hotkey events, typing filtered out
 *   exec <helper> <args...>       run a root helper (gpu-helper, ryzenadj,
 *                                 gpu-block-helper) with the client's stdio
 *
 * The boundary is layered. The udev rules decide which files this account may
 * touch at all; the kernel enforces them. The daemon adds: callers must be in
 * the GHELPERD_CLIENT_GROUP group, sysfs paths must resolve inside /sys, HID
 * nodes must not carry a keyboard collection, hotkeys are filtered to the codes
 * the app acts on, and exec is limited to the fixed helper paths, which sudoers
 * grants to this account alone.
 *
 * Socket-activated (fd 3, LISTEN_FDS=1). Without activation, --socket PATH
 * binds its own socket, for tests. Every request is logged to syslog
 * (journalctl -t ghelperd). "ghelperd --grant FILE..." is the udev side: it
 * hands the files to this account's group (see src/Install/HelperRules.cs). */

#define _GNU_SOURCE
#include "ghelperd.h"

#include <errno.h>
#include <fcntl.h>
#include <fnmatch.h>
#include <grp.h>
#include <limits.h>
#include <linux/input.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <syslog.h>
#include <unistd.h>

#define MAX_HOTKEY_DEVICES 8
#define MAX_GROUPS 256

/* Root helpers that exec may run, all reached through sudo -n; sudoers grants
 * them to the ghelper account only. */
static const char *const allowed_helpers[] = {
    "/opt/ghelper/gpu-helper",
    "/etc/ghelper/gpu-helper",
    "/usr/local/lib/ghelper/gpu-helper",
    "/opt/ghelper/ryzenadj",
    "/etc/ghelper/ryzenadj",
    "/usr/local/lib/ghelper/gpu-block-helper.sh",
    NULL,
};

static const char *const sudo_paths[] = {
    "/usr/bin/sudo", "/bin/sudo", "/usr/local/bin/sudo", "/run/wrappers/bin/sudo", NULL,
};

/* HID vendors the app talks to: ASUS (0b05), Logitech (046d), ITE (048d). */
static const unsigned int allowed_hid_vendors[] = { 0x0b05, 0x046d, 0x048d };

/* Key codes the app maps (LinuxAsusWmi.MapLinuxKeyToBindingName and
 * MapLinuxKeyToLegacyEvent). None is a typing key. */
static const unsigned short hotkey_codes[] = {
    148, 190, 202, 203, 229, 230, 530, 142, 212, 247, 224, 225, 407, 248, 505,
};

/* MSC_SCAN values the app maps (Windows WMI event codes). Forwarded only with a
 * key code outside the main keyboard block, so a colliding scan code on a
 * typing key never leaves the daemon. */
static const int hotkey_scans[] = { 56, 179, 174, 196, 197, 107, 108, 133, 136, 16, 32, 78, 124 };

#define TYPING_BLOCK_END 128
#define KEYD_DEVICE "keyd virtual keyboard"

static void glog(int prio, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    vsyslog(prio, fmt, ap);
    va_end(ap);
}

/* ---------- replies ---------- */

static int send_fields(int conn, const char *const *fields, int count, int fd_count, const int *fds)
{
    char buf[GHELPERD_MAX_PACKET];
    size_t len = 0;
    for (int i = 0; i < count; i++)
    {
        size_t flen = strlen(fields[i]);
        if (len + flen + 1 > sizeof(buf))
            return -1;
        memcpy(buf + len, fields[i], flen);
        len += flen;
        buf[len++] = '\0';
    }

    struct iovec iov = { .iov_base = buf, .iov_len = len };
    struct msghdr msg = { .msg_iov = &iov, .msg_iovlen = 1 };
    char control[CMSG_SPACE(sizeof(int) * 3)];
    if (fd_count > 0)
    {
        memset(control, 0, sizeof(control));
        msg.msg_control = control;
        msg.msg_controllen = CMSG_SPACE(sizeof(int) * fd_count);
        struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
        cmsg->cmsg_level = SOL_SOCKET;
        cmsg->cmsg_type = SCM_RIGHTS;
        cmsg->cmsg_len = CMSG_LEN(sizeof(int) * fd_count);
        memcpy(CMSG_DATA(cmsg), fds, sizeof(int) * fd_count);
    }
    return sendmsg(conn, &msg, MSG_NOSIGNAL) < 0 ? -1 : 0;
}

static void reply_ok(int conn, const char *data)
{
    const char *fields[] = { "ok", data };
    send_fields(conn, fields, data ? 2 : 1, 0, NULL);
}

static void reply_err(int conn, int err, const char *what)
{
    char code[16];
    snprintf(code, sizeof(code), "%d", err);
    const char *fields[] = { "err", code, what };
    send_fields(conn, fields, 3, 0, NULL);
}

/* ---------- caller check ---------- */

static int caller_allowed(int conn, struct ucred *cred)
{
    socklen_t len = sizeof(*cred);
    if (getsockopt(conn, SOL_SOCKET, SO_PEERCRED, cred, &len) < 0)
        return 0;
    if (cred->uid == 0)
        return 1;

    struct group *grp = getgrnam(GHELPERD_CLIENT_GROUP);
    struct passwd *pw = getpwuid(cred->uid);
    if (grp == NULL || pw == NULL)
        return 0;

    gid_t groups[MAX_GROUPS];
    int ngroups = MAX_GROUPS;
    if (getgrouplist(pw->pw_name, pw->pw_gid, groups, &ngroups) < 0)
        return 0;
    for (int i = 0; i < ngroups; i++)
        if (groups[i] == grp->gr_gid)
            return 1;
    return 0;
}

/* ---------- sysfs ---------- */

/* Resolve a requested path and require it to stay inside /sys. sysfs symlinks
 * are kernel-made, so resolving them is safe; ".." never is. */
static int resolve_sys_path(const char *path, char *resolved)
{
    if (strncmp(path, "/sys/", 5) != 0 || strstr(path, "/..") != NULL)
        return -EPERM;
    if (realpath(path, resolved) == NULL)
        return -errno;
    if (strncmp(resolved, "/sys/", 5) != 0)
        return -EPERM;
    return 0;
}

static void op_write(int conn, const char *path, const char *value, const struct ucred *cred)
{
    char resolved[PATH_MAX];
    int rc = resolve_sys_path(path, resolved);
    if (rc == 0)
    {
        int fd = open(resolved, O_WRONLY | O_CLOEXEC);
        if (fd < 0)
            rc = -errno;
        else
        {
            if (write(fd, value, strlen(value)) < 0)
                rc = -errno;
            close(fd);
        }
    }
    glog(rc == 0 ? LOG_INFO : LOG_WARNING, "uid %u write %s = %s: %s",
         (unsigned)cred->uid, path, value, rc == 0 ? "ok" : strerror(-rc));
    if (rc == 0)
        reply_ok(conn, NULL);
    else
        reply_err(conn, -rc, strerror(-rc));
}

static void op_read(int conn, const char *path)
{
    char resolved[PATH_MAX];
    int rc = resolve_sys_path(path, resolved);
    char data[4096];
    if (rc == 0)
    {
        int fd = open(resolved, O_RDONLY | O_CLOEXEC);
        if (fd < 0)
            rc = -errno;
        else
        {
            ssize_t n = read(fd, data, sizeof(data) - 1);
            if (n < 0)
                rc = -errno;
            else
                data[n] = '\0';
            close(fd);
        }
    }
    if (rc == 0)
        reply_ok(conn, data);
    else
        reply_err(conn, -rc, strerror(-rc));
}

/* ---------- hidraw ---------- */

static int read_small_file(const char *path, unsigned char *buf, size_t size)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
        return -1;
    ssize_t n = read(fd, buf, size);
    close(fd);
    return (int)n;
}

/* Walk the report descriptor: a Generic Desktop Keyboard or Keypad
 * application collection means the node delivers typing as input reports. */
static int descriptor_has_keyboard(const unsigned char *d, int len)
{
    unsigned int usage_page = 0;
    unsigned int usage = 0;
    int i = 0;
    while (i < len)
    {
        unsigned char prefix = d[i];
        if (prefix == 0xFE)
        {
            /* Long item: skip its data. */
            if (i + 1 >= len)
                break;
            i += 3 + d[i + 1];
            continue;
        }
        int size = prefix & 0x03;
        if (size == 3)
            size = 4;
        if (i + 1 + size > len)
            break;
        unsigned int data = 0;
        for (int b = 0; b < size; b++)
            data |= (unsigned int)d[i + 1 + b] << (8 * b);
        int type = (prefix >> 2) & 0x03;
        int tag = (prefix >> 4) & 0x0F;

        if (type == 1 && tag == 0)
            usage_page = data;
        else if (type == 2 && tag == 0)
            usage = size == 4 ? (data & 0xFFFF) : data;
        else if (type == 0 && tag == 0xA)
        {
            if (data == 0x01 && usage_page == 0x01 && (usage == 0x06 || usage == 0x07))
                return 1;
            usage = 0;
        }
        else if (type == 0)
            usage = 0;
        i += 1 + size;
    }
    return 0;
}

static int hidraw_allowed(const char *path, char *why, size_t why_size)
{
    unsigned int num;
    char tail;
    if (sscanf(path, "/dev/hidraw%u%c", &num, &tail) != 1)
    {
        snprintf(why, why_size, "not a hidraw node");
        return 0;
    }

    char sys[PATH_MAX];
    unsigned char buf[4096];
    snprintf(sys, sizeof(sys), "/sys/class/hidraw/hidraw%u/device/uevent", num);
    int n = read_small_file(sys, buf, sizeof(buf) - 1);
    if (n <= 0)
    {
        snprintf(why, why_size, "no uevent for hidraw%u", num);
        return 0;
    }
    buf[n] = '\0';
    char *hid_id = strstr((char *)buf, "HID_ID=");
    unsigned int bus, vendor, product;
    if (hid_id == NULL || sscanf(hid_id, "HID_ID=%x:%x:%x", &bus, &vendor, &product) != 3)
    {
        snprintf(why, why_size, "no HID_ID for hidraw%u", num);
        return 0;
    }
    int vendor_ok = 0;
    for (size_t i = 0; i < sizeof(allowed_hid_vendors) / sizeof(allowed_hid_vendors[0]); i++)
        if (vendor == allowed_hid_vendors[i])
            vendor_ok = 1;
    if (!vendor_ok)
    {
        snprintf(why, why_size, "vendor %04x not served", vendor);
        return 0;
    }

    snprintf(sys, sizeof(sys), "/sys/class/hidraw/hidraw%u/device/report_descriptor", num);
    n = read_small_file(sys, buf, sizeof(buf));
    if (n <= 0)
    {
        snprintf(why, why_size, "no report descriptor for hidraw%u", num);
        return 0;
    }
    if (descriptor_has_keyboard(buf, n))
    {
        snprintf(why, why_size, "hidraw%u carries a keyboard collection", num);
        return 0;
    }
    return 1;
}

static void op_hidraw(int conn, const char *path, const struct ucred *cred)
{
    char why[128];
    if (!hidraw_allowed(path, why, sizeof(why)))
    {
        glog(LOG_WARNING, "uid %u hidraw %s refused: %s", (unsigned)cred->uid, path, why);
        reply_err(conn, EPERM, why);
        return;
    }
    int fd = open(path, O_RDWR | O_CLOEXEC);
    if (fd < 0)
        fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
    {
        int err = errno;
        glog(LOG_WARNING, "uid %u hidraw %s: %s", (unsigned)cred->uid, path, strerror(err));
        reply_err(conn, err, strerror(err));
        return;
    }
    glog(LOG_INFO, "uid %u hidraw %s passed", (unsigned)cred->uid, path);
    const char *fields[] = { "ok" };
    send_fields(conn, fields, 1, 1, &fd);
    close(fd);
}

/* ---------- hotkeys ---------- */

/* Same selection as LinuxAsusWmi.FindAsusInputDevices: ASUS sections of
 * /proc/bus/input/devices that are a keyboard, WMI hotkeys or vendor 0b05. */
static int find_hotkey_devices(char paths[][32], int max)
{
    FILE *f = fopen("/proc/bus/input/devices", "re");
    if (f == NULL)
        return 0;

    int count = 0;
    char line[1024];
    char section[8192];
    size_t section_len = 0;
    int done = 0;
    while (!done)
    {
        int eof = fgets(line, sizeof(line), f) == NULL;
        if (!eof && line[0] != '\n' && section_len + strlen(line) < sizeof(section))
        {
            memcpy(section + section_len, line, strlen(line) + 1);
            section_len += strlen(line);
            continue;
        }
        if (section_len > 0 && count < max)
        {
            int asus = strcasestr(section, "asus") != NULL || strcasestr(section, "Vendor=0b05") != NULL;
            int keyboard = strcasestr(section, "keyboard") != NULL || strcasestr(section, "Vendor=0b05") != NULL;
            int wmi = strcasestr(section, "wmi") != NULL;
            /* A remapper such as keyd grabs the ASUS keyboard and re-emits every key
             * on its own device; the hotkeys only arrive there. The filter keeps the
             * typing it carries inside this daemon. */
            if (strstr(section, "Name=\"" KEYD_DEVICE "\"") != NULL)
                asus = keyboard = 1;
            char *handlers = strstr(section, "H: Handlers=");
            char *event = handlers != NULL ? strstr(handlers, "event") : NULL;
            unsigned int num;
            if (asus && (keyboard || wmi) && event != NULL && sscanf(event, "event%u", &num) == 1)
                snprintf(paths[count++], 32, "/dev/input/event%u", num);
        }
        section_len = 0;
        section[0] = '\0';
        done = eof;
    }
    fclose(f);
    return count;
}

static int is_hotkey(unsigned short code, int scan)
{
    for (size_t i = 0; i < sizeof(hotkey_codes) / sizeof(hotkey_codes[0]); i++)
        if (code == hotkey_codes[i])
            return 1;
    if (code < TYPING_BLOCK_END)
        return 0;
    for (size_t i = 0; i < sizeof(hotkey_scans) / sizeof(hotkey_scans[0]); i++)
        if (scan == hotkey_scans[i])
            return 1;
    return 0;
}

static void op_hotkeys(int conn, const struct ucred *cred)
{
    char paths[MAX_HOTKEY_DEVICES][32];
    int count = find_hotkey_devices(paths, MAX_HOTKEY_DEVICES);
    struct pollfd fds[MAX_HOTKEY_DEVICES + 1];
    int scans[MAX_HOTKEY_DEVICES];
    int open_count = 0;
    for (int i = 0; i < count; i++)
    {
        int fd = open(paths[i], O_RDONLY | O_CLOEXEC);
        if (fd < 0)
        {
            glog(LOG_WARNING, "hotkeys: open %s: %s", paths[i], strerror(errno));
            continue;
        }
        fds[open_count].fd = fd;
        fds[open_count].events = POLLIN;
        scans[open_count] = -1;
        open_count++;
    }
    if (open_count == 0)
    {
        reply_err(conn, ENODEV, "no ASUS hotkey device readable");
        return;
    }
    glog(LOG_INFO, "uid %u hotkeys: streaming from %d device(s)", (unsigned)cred->uid, open_count);
    reply_ok(conn, NULL);

    fds[open_count].fd = conn;
    fds[open_count].events = POLLIN;
    for (;;)
    {
        if (poll(fds, open_count + 1, -1) < 0 && errno != EINTR)
            break;
        if (fds[open_count].revents & (POLLHUP | POLLERR | POLLIN))
            break;
        for (int i = 0; i < open_count; i++)
        {
            if (!(fds[i].revents & POLLIN))
                continue;
            struct input_event ev;
            if (read(fds[i].fd, &ev, sizeof(ev)) != (ssize_t)sizeof(ev))
                goto done;
            if (ev.type == EV_MSC && ev.code == MSC_SCAN)
            {
                scans[i] = ev.value;
                continue;
            }
            if (ev.type != EV_KEY)
                continue;
            int scan = scans[i];
            scans[i] = -1;
            if (!is_hotkey(ev.code, scan))
                continue;
            /* Scan first, as the kernel orders them; the app pairs them. */
            if (scan >= 0)
            {
                struct input_event msc = ev;
                msc.type = EV_MSC;
                msc.code = MSC_SCAN;
                msc.value = scan;
                if (send(conn, &msc, sizeof(msc), MSG_NOSIGNAL) < 0)
                    goto done;
            }
            if (send(conn, &ev, sizeof(ev), MSG_NOSIGNAL) < 0)
                goto done;
        }
    }
done:
    for (int i = 0; i < open_count; i++)
        close(fds[i].fd);
}

/* ---------- exec ---------- */

static const char *find_sudo(void)
{
    for (int i = 0; sudo_paths[i] != NULL; i++)
        if (access(sudo_paths[i], X_OK) == 0)
            return sudo_paths[i];
    return NULL;
}

static int helper_allowed(const char *path)
{
    for (int i = 0; allowed_helpers[i] != NULL; i++)
        if (strcmp(path, allowed_helpers[i]) == 0)
            return 1;
    return 0;
}

static void op_exec(int conn, char **argv, int argc, const int *fds, int fd_count, const struct ucred *cred)
{
    const char *sudo = find_sudo();
    if (argc < 1 || !helper_allowed(argv[0]) || fd_count != 3 || sudo == NULL)
    {
        const char *why = argc < 1 || !helper_allowed(argv[0]) ? "helper not allowed"
                        : fd_count != 3                         ? "stdio descriptors missing"
                                                                : "sudo not found";
        glog(LOG_WARNING, "uid %u exec %s refused: %s", (unsigned)cred->uid, argc > 0 ? argv[0] : "-", why);
        reply_err(conn, EPERM, why);
        return;
    }

    char *args[GHELPERD_MAX_FIELDS + 3];
    int n = 0;
    args[n++] = (char *)"sudo";
    args[n++] = (char *)"-n";
    for (int i = 0; i < argc && n < GHELPERD_MAX_FIELDS + 2; i++)
        args[n++] = argv[i];
    args[n] = NULL;

    char joined[1024] = "";
    for (int i = 1; i < argc && strlen(joined) + strlen(argv[i]) + 2 < sizeof(joined); i++)
    {
        strcat(joined, " ");
        strcat(joined, argv[i]);
    }
    glog(LOG_INFO, "uid %u exec %s%s", (unsigned)cred->uid, argv[0], joined);

    pid_t child = fork();
    if (child < 0)
    {
        reply_err(conn, errno, strerror(errno));
        return;
    }
    if (child == 0)
    {
        for (int i = 0; i < 3; i++)
            dup2(fds[i], i);
        closefrom(3);
        execv(sudo, args);
        _exit(127);
    }

    /* Wait for the child, but end it when the client goes away: the fan
     * control session lives exactly as long as its client. */
    int pidfd = (int)syscall(SYS_pidfd_open, child, 0);
    struct pollfd pfd[2] = { { .fd = pidfd, .events = POLLIN }, { .fd = conn, .events = POLLIN } };
    int status = 0;
    for (;;)
    {
        if (pidfd >= 0 && poll(pfd, 2, -1) < 0 && errno != EINTR)
            break;
        if (pidfd < 0 || (pfd[0].revents & POLLIN))
        {
            waitpid(child, &status, 0);
            break;
        }
        if (pfd[1].revents & (POLLHUP | POLLERR | POLLIN))
        {
            kill(child, SIGTERM);
            waitpid(child, &status, 0);
            break;
        }
    }
    if (pidfd >= 0)
        close(pidfd);

    char code[16];
    snprintf(code, sizeof(code), "%d", WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status));
    const char *fields[] = { "exit", code };
    send_fields(conn, fields, 2, 0, NULL);
}

/* ---------- connection ---------- */

static void serve(int conn)
{
    struct ucred cred;
    if (!caller_allowed(conn, &cred))
    {
        glog(LOG_WARNING, "refused pid %d uid %u: not in group %s",
             (int)cred.pid, (unsigned)cred.uid, GHELPERD_CLIENT_GROUP);
        reply_err(conn, EACCES, "caller not in group " GHELPERD_CLIENT_GROUP);
        return;
    }

    char buf[GHELPERD_MAX_PACKET + 1];
    char control[CMSG_SPACE(sizeof(int) * 3)];
    struct iovec iov = { .iov_base = buf, .iov_len = GHELPERD_MAX_PACKET };
    struct msghdr msg = { .msg_iov = &iov, .msg_iovlen = 1, .msg_control = control, .msg_controllen = sizeof(control) };
    ssize_t len = recvmsg(conn, &msg, MSG_CMSG_CLOEXEC);
    if (len <= 0)
        return;
    buf[len] = '\0';

    int fds[3];
    int fd_count = 0;
    for (struct cmsghdr *c = CMSG_FIRSTHDR(&msg); c != NULL; c = CMSG_NXTHDR(&msg, c))
        if (c->cmsg_level == SOL_SOCKET && c->cmsg_type == SCM_RIGHTS)
        {
            int n = (int)((c->cmsg_len - CMSG_LEN(0)) / sizeof(int));
            for (int i = 0; i < n; i++)
            {
                int fd;
                memcpy(&fd, CMSG_DATA(c) + i * sizeof(int), sizeof(int));
                if (fd_count < 3)
                    fds[fd_count++] = fd;
                else
                    close(fd);
            }
        }

    char *fields[GHELPERD_MAX_FIELDS];
    int count = 0;
    for (char *p = buf; p < buf + len && count < GHELPERD_MAX_FIELDS; p += strlen(p) + 1)
        fields[count++] = p;

    if (count >= 1 && strcmp(fields[0], "ping") == 0)
        reply_ok(conn, GHELPERD_VERSION);
    else if (count == 3 && strcmp(fields[0], "write") == 0)
        op_write(conn, fields[1], fields[2], &cred);
    else if (count == 2 && strcmp(fields[0], "read") == 0)
        op_read(conn, fields[1]);
    else if (count == 2 && strcmp(fields[0], "hidraw") == 0)
        op_hidraw(conn, fields[1], &cred);
    else if (count == 1 && strcmp(fields[0], "hotkeys") == 0)
        op_hotkeys(conn, &cred);
    else if (count >= 2 && strcmp(fields[0], "exec") == 0)
        op_exec(conn, fields + 1, count - 1, fds, fd_count, &cred);
    else
        reply_err(conn, EINVAL, "unknown request");

    for (int i = 0; i < fd_count; i++)
        close(fds[i]);
}

/* udev RUN target replacing upstream's "chmod 0666": the daemon's group gets
 * read-write, others lose write but keep read, so the unprivileged app still
 * displays values it no longer writes. Missing files are skipped, as the
 * upstream rules' guards do. */
static int grant(int count, char **files)
{
    struct group *grp = getgrnam(GHELPERD_ACCOUNT);
    if (grp == NULL)
    {
        fprintf(stderr, "ghelperd --grant: group %s does not exist\n", GHELPERD_ACCOUNT);
        return 1;
    }
    int rc = 0;
    for (int i = 0; i < count; i++)
    {
        struct stat st;
        if (stat(files[i], &st) < 0)
            continue;
        if (chown(files[i], (uid_t)-1, grp->gr_gid) < 0
            || chmod(files[i], ((st.st_mode & 07777) & ~(mode_t)0002) | 0060) < 0)
        {
            fprintf(stderr, "ghelperd --grant %s: %s\n", files[i], strerror(errno));
            rc = 1;
        }
    }
    return rc;
}

static int bind_socket(const char *path)
{
    int fd = socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0);
    if (fd < 0)
        return -1;
    struct sockaddr_un addr = { .sun_family = AF_UNIX };
    snprintf(addr.sun_path, sizeof(addr.sun_path), "%s", path);
    unlink(path);
    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0 || listen(fd, 16) < 0)
    {
        close(fd);
        return -1;
    }
    return fd;
}

int main(int argc, char **argv)
{
    if (argc >= 2 && strcmp(argv[1], "--grant") == 0)
        return grant(argc - 2, argv + 2);

    openlog("ghelperd", LOG_PID, LOG_DAEMON);

    int listen_fd = -1;
    const char *pid_env = getenv("LISTEN_PID");
    const char *fds_env = getenv("LISTEN_FDS");
    if (pid_env != NULL && fds_env != NULL && atoi(pid_env) == getpid() && atoi(fds_env) == 1)
        listen_fd = 3;
    else if (argc == 3 && strcmp(argv[1], "--socket") == 0)
        listen_fd = bind_socket(argv[2]);
    else
    {
        fprintf(stderr, "ghelperd: expected socket activation (LISTEN_FDS=1) or --socket PATH\n");
        return 2;
    }
    if (listen_fd < 0)
    {
        fprintf(stderr, "ghelperd: cannot listen: %s\n", strerror(errno));
        return 1;
    }

    /* Children are reaped by the kernel; each connection runs in its own. */
    struct sigaction sa = { .sa_handler = SIG_IGN, .sa_flags = SA_NOCLDWAIT };
    sigaction(SIGCHLD, &sa, NULL);
    glog(LOG_INFO, "ghelperd %s listening", GHELPERD_VERSION);

    for (;;)
    {
        int conn = accept4(listen_fd, NULL, NULL, SOCK_CLOEXEC);
        if (conn < 0)
        {
            if (errno == EINTR)
                continue;
            glog(LOG_ERR, "accept: %s", strerror(errno));
            return 1;
        }
        pid_t child = fork();
        if (child == 0)
        {
            close(listen_fd);
            /* exec children must be waited for; restore default SIGCHLD. */
            signal(SIGCHLD, SIG_DFL);
            serve(conn);
            _exit(0);
        }
        close(conn);
    }
}
