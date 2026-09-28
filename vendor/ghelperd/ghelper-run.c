/* ghelper-run - stand-in for "sudo -n <helper> <args>" when ghelperd runs.
 *
 * Passes its own stdin, stdout and stderr to ghelperd, which runs the helper on
 * them, so callers read output and write input exactly as with sudo (the
 * ec-fanctl session included). Exits with the helper's exit code. A refusal is
 * reported on stderr prefixed "ghelper-run:" with exit code 126, which callers
 * treat like sudo refusing. */

#define _GNU_SOURCE
#include "ghelperd.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define EXIT_REFUSED 126

int main(int argc, char **argv)
{
    if (argc < 2)
    {
        fprintf(stderr, "usage: ghelper-run <helper> [args...]\n");
        return 2;
    }

    const char *path = getenv("GHELPERD_SOCKET");
    if (path == NULL)
        path = GHELPERD_SOCKET;
    int sock = socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0);
    struct sockaddr_un addr = { .sun_family = AF_UNIX };
    snprintf(addr.sun_path, sizeof(addr.sun_path), "%s", path);
    if (sock < 0 || connect(sock, (struct sockaddr *)&addr, sizeof(addr)) < 0)
    {
        fprintf(stderr, "ghelper-run: cannot reach ghelperd at %s: %s\n", path, strerror(errno));
        return EXIT_REFUSED;
    }

    char buf[GHELPERD_MAX_PACKET];
    size_t len = 0;
    const char *op = "exec";
    memcpy(buf, op, strlen(op) + 1);
    len = strlen(op) + 1;
    for (int i = 1; i < argc; i++)
    {
        size_t alen = strlen(argv[i]) + 1;
        if (len + alen > sizeof(buf))
        {
            fprintf(stderr, "ghelper-run: arguments exceed %d bytes\n", GHELPERD_MAX_PACKET);
            return EXIT_REFUSED;
        }
        memcpy(buf + len, argv[i], alen);
        len += alen;
    }

    int fds[3] = { 0, 1, 2 };
    char control[CMSG_SPACE(sizeof(fds))];
    memset(control, 0, sizeof(control));
    struct iovec iov = { .iov_base = buf, .iov_len = len };
    struct msghdr msg = { .msg_iov = &iov, .msg_iovlen = 1, .msg_control = control, .msg_controllen = sizeof(control) };
    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    cmsg->cmsg_level = SOL_SOCKET;
    cmsg->cmsg_type = SCM_RIGHTS;
    cmsg->cmsg_len = CMSG_LEN(sizeof(fds));
    memcpy(CMSG_DATA(cmsg), fds, sizeof(fds));
    if (sendmsg(sock, &msg, 0) < 0)
    {
        fprintf(stderr, "ghelper-run: send to ghelperd failed: %s\n", strerror(errno));
        return EXIT_REFUSED;
    }

    char reply[GHELPERD_MAX_PACKET + 1];
    ssize_t n = recv(sock, reply, GHELPERD_MAX_PACKET, 0);
    if (n <= 0)
    {
        fprintf(stderr, "ghelper-run: ghelperd closed the connection\n");
        return EXIT_REFUSED;
    }
    reply[n] = '\0';

    if (strcmp(reply, "exit") == 0 && (size_t)n > strlen("exit") + 1)
        return atoi(reply + strlen("exit") + 1);

    /* "err\0<errno>\0<message>" */
    const char *code = reply + strlen(reply) + 1;
    const char *message = (code < reply + n) ? code + strlen(code) + 1 : "";
    fprintf(stderr, "ghelper-run: refused by ghelperd: %s\n", message < reply + n ? message : reply);
    return EXIT_REFUSED;
}
