/* Intel CPU undervolt via MSR 0x150 OC mailbox, and RAPL power limits, for gpu-helper. */
#include "gpu-helper.h"

/*
 * Intel voltage-offset undervolting via the OC mailbox MSR 0x150.
 *   plane 0 = CPU core, plane 2 = CPU cache/ring.
 *   write cmd hi32: 0x80000011 (core) / 0x80000211 (cache)
 *   read  cmd hi32: 0x80000010 (core) / 0x80000210 (cache)
 *   payload: round(mv * 1.024) as 11-bit twos complement, shifted to bits[31:21]
 * Offset is restricted to [-150, 0] mV: undervolt only, bounded magnitude.
 * Non-persistent (resets on reboot). Package-scoped, so cpu0 is sufficient.
 */

#define MSR_OC_MAILBOX 0x150
#define UV_MIN_MV (-150)
#define UV_MAX_MV 0

static uint32_t uv_encode(int mv)
{
    double d = mv * 1.024;
    int v = (int)(d < 0 ? d - 0.5 : d + 0.5);
    return ((uint32_t)(v & 0x7FF)) << 21;
}

static int uv_decode(uint32_t low)
{
    int o = (int)((low >> 21) & 0x7FF);
    if (o & 0x400)
        o -= 0x800;
    double mv = o / 1.024;
    return (int)(mv < 0 ? mv - 0.5 : mv + 0.5);
}

static void uv_allow_writes(void)
{
    int fd = open("/sys/module/msr/parameters/allow_writes", O_WRONLY);
    if (fd >= 0)
    {
        ssize_t w = write(fd, "on\n", 3);
        (void)w;
        close(fd);
    }
}

static void uv_ensure_msr(void)
{
    if (access("/dev/cpu/0/msr", F_OK) == 0)
        return;
    pid_t pid = fork();
    if (pid == 0)
    {
        execlp("modprobe", "modprobe", "msr", (char *)NULL);
        _exit(127);
    }
    else if (pid > 0)
    {
        int st;
        waitpid(pid, &st, 0);
    }
}

static int uv_msr_write(int fd, uint64_t v)
{
    return (pwrite(fd, &v, 8, MSR_OC_MAILBOX) == 8) ? 0 : -1;
}

static int uv_read_plane(int fd, int plane)
{
    uint64_t cmd = 0x8000001000000000ULL | ((uint64_t)plane << 40);
    if (uv_msr_write(fd, cmd) != 0)
        return -1000000;
    uint64_t val = 0;
    if (pread(fd, &val, 8, MSR_OC_MAILBOX) != 8)
        return -1000000;
    return uv_decode((uint32_t)(val & 0xFFFFFFFFULL));
}

int do_msr_uv(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: msr-uv <mv>  (integer in [%d,%d])\n", UV_MIN_MV, UV_MAX_MV);
        return 1;
    }
    char *end = NULL;
    long mv = strtol(argv[2], &end, 10);
    if (end == argv[2] || *end != '\0' || mv > UV_MAX_MV || mv < UV_MIN_MV)
    {
        glog(LOG_WARNING, "msr-uv: rejected offset '%s' (allowed [%d,%d])", argv[2], UV_MIN_MV, UV_MAX_MV);
        fprintf(stderr, "msr-uv: offset must be an integer in [%d,%d]\n", UV_MIN_MV, UV_MAX_MV);
        return 1;
    }

    uv_ensure_msr();
    uv_allow_writes();

    int fd = open("/dev/cpu/0/msr", O_RDWR);
    if (fd < 0)
    {
        glog(LOG_ERR, "msr-uv: open /dev/cpu/0/msr: %s", strerror(errno));
        fprintf(stderr, "msr-uv: open /dev/cpu/0/msr: %s (msr module loaded?)\n", strerror(errno));
        return 3;
    }

    uint32_t enc = uv_encode((int)mv);
    uint64_t core_w = 0x8000001100000000ULL | enc;
    uint64_t cache_w = 0x8000021100000000ULL | enc;

    int rc = 0;
    if (uv_msr_write(fd, core_w) != 0)
    {
        glog(LOG_ERR, "msr-uv: core (plane 0) write failed: %s", strerror(errno));
        fprintf(stderr, "msr-uv: core write failed: %s\n", strerror(errno));
        rc = 3;
    }
    else if (uv_msr_write(fd, cache_w) != 0)
    {
        glog(LOG_ERR, "msr-uv: cache (plane 2) write failed: %s", strerror(errno));
        fprintf(stderr, "msr-uv: cache write failed: %s\n", strerror(errno));
        rc = 3;
    }

    if (rc == 0)
    {
        int core_rb = uv_read_plane(fd, 0);
        int cache_rb = uv_read_plane(fd, 2);
        printf("core=%d cache=%d\n", core_rb, cache_rb);
        glog(LOG_INFO, "msr-uv: requested %ld mV -> readback core=%d cache=%d", mv, core_rb, cache_rb);
    }
    close(fd);
    return rc;
}

/*
 * Intel RAPL package power limits through the powercap sysfs interface.
 * Some ASUS Intel models (e.g. Flow Z13 GZ301V) accept the WMI PPT writes but
 * never apply them on Linux - on Windows Intel DTT does that - so the limit is
 * written to RAPL directly. The CPU enforces the lower of the MMIO and MSR
 * copies, so both are set. pl1 = long_term, pl2 = short_term.
 */

#define RAPL_MIN_W 5
#define RAPL_MAX_W 250

static const char *const rapl_zones[] = {
    "/sys/class/powercap/intel-rapl-mmio:0",
    "/sys/class/powercap/intel-rapl:0",
};

/* Index of the constraint named name in zone, or -1. */
static int rapl_find_constraint(const char *zone, const char *name)
{
    for (int i = 0; i < 4; i++)
    {
        char path[PATH_BUF_SIZE];
        char buf[32] = "";
        snprintf(path, sizeof(path), "%s/constraint_%d_name", zone, i);
        FILE *f = fopen(path, "r");
        if (f == NULL)
            return -1;
        char *got = fgets(buf, sizeof(buf), f);
        fclose(f);
        if (got != NULL && strncmp(buf, name, strlen(name)) == 0 && (buf[strlen(name)] == '\n' || buf[strlen(name)] == '\0'))
            return i;
    }
    return -1;
}

int do_rapl_limit(int argc, char **argv)
{
    if (argc != 4 || (strcmp(argv[2], "pl1") != 0 && strcmp(argv[2], "pl2") != 0))
    {
        fprintf(stderr, "usage: rapl-limit <pl1|pl2> <watts>  (integer in [%d,%d])\n", RAPL_MIN_W, RAPL_MAX_W);
        return 1;
    }
    char *end = NULL;
    long watts = strtol(argv[3], &end, 10);
    if (end == argv[3] || *end != '\0' || watts < RAPL_MIN_W || watts > RAPL_MAX_W)
    {
        glog(LOG_WARNING, "rapl-limit: rejected %s '%s' (allowed [%d,%d])", argv[2], argv[3], RAPL_MIN_W, RAPL_MAX_W);
        fprintf(stderr, "rapl-limit: watts must be an integer in [%d,%d]\n", RAPL_MIN_W, RAPL_MAX_W);
        return 1;
    }
    const char *constraint = strcmp(argv[2], "pl1") == 0 ? "long_term" : "short_term";

    int written = 0;
    int rc = 0;
    for (size_t z = 0; z < sizeof(rapl_zones) / sizeof(rapl_zones[0]); z++)
    {
        int index = rapl_find_constraint(rapl_zones[z], constraint);
        if (index < 0)
            continue;
        char path[PATH_BUF_SIZE];
        char value[32];
        snprintf(path, sizeof(path), "%s/constraint_%d_power_limit_uw", rapl_zones[z], index);
        snprintf(value, sizeof(value), "%ld", watts * 1000000L);
        int fd = open(path, O_WRONLY);
        if (fd < 0 || write(fd, value, strlen(value)) < 0)
        {
            glog(LOG_ERR, "rapl-limit: write %s: %s", path, strerror(errno));
            fprintf(stderr, "rapl-limit: write %s: %s\n", path, strerror(errno));
            rc = 3;
        }
        else
            written++;
        if (fd >= 0)
            close(fd);
    }
    if (written == 0 && rc == 0)
    {
        fprintf(stderr, "rapl-limit: no intel-rapl zone with a %s constraint\n", constraint);
        return 1;
    }
    if (rc == 0)
    {
        glog(LOG_INFO, "rapl-limit: %s (%s) = %ld W in %d zone(s)", argv[2], constraint, watts, written);
        printf("%s=%ld zones=%d\n", argv[2], watts, written);
    }
    return rc;
}
