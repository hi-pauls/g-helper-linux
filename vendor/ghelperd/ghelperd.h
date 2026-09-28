/* Shared protocol for ghelperd (privilege-separated hardware helper) and its clients.
 *
 * Transport: SOCK_SEQPACKET on GHELPERD_SOCKET. Every request is one packet of
 * NUL-separated fields, the first being the operation. Every reply starts with
 * "ok" or "err" (then errno and message fields); "exec" ends with "exit" and the
 * child's exit code, "hotkeys" streams struct input_event packets after "ok". */
#ifndef GHELPERD_H
#define GHELPERD_H

#define GHELPERD_SOCKET "/run/ghelper/ghelperd.sock"
#define GHELPERD_VERSION "1"
#define GHELPERD_CLIENT_GROUP "ghelperctl"
#define GHELPERD_ACCOUNT "ghelper"
#define GHELPERD_MAX_PACKET 8192
#define GHELPERD_MAX_FIELDS 64

#endif
