#if defined(__APPLE__)
#define _DARWIN_C_SOURCE 1
#endif
#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <libproc.h>
#include <sys/proc.h>
#endif

struct process_identity {
    uint64_t high;
    uint64_t low;
};

struct worker_facts {
    pid_t broker_pid;
    struct process_identity broker_identity;
    pid_t worker_pid;
    struct process_identity worker_identity;
};

static void close_quietly(int descriptor)
{
    if (descriptor >= 0) {
        (void)close(descriptor);
    }
}

static void write_full(int descriptor, const void *buffer, size_t length)
{
    const uint8_t *cursor = (const uint8_t *)buffer;
    while (length > 0U) {
        ssize_t written = write(descriptor, cursor, length);
        if (written < 0 && errno == EINTR) {
            continue;
        }
        if (written <= 0) {
            _exit(70);
        }
        cursor += (size_t)written;
        length -= (size_t)written;
    }
}

static void read_full(int descriptor, void *buffer, size_t length)
{
    uint8_t *cursor = (uint8_t *)buffer;
    while (length > 0U) {
        ssize_t received = read(descriptor, cursor, length);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received <= 0) {
            _exit(70);
        }
        cursor += (size_t)received;
        length -= (size_t)received;
    }
}

static void sleep_milliseconds(uint64_t milliseconds)
{
    struct timespec value;
    value.tv_sec = (time_t)(milliseconds / UINT64_C(1000));
    value.tv_nsec =
        (long)((milliseconds % UINT64_C(1000)) * UINT64_C(1000000));
    while (nanosleep(&value, &value) != 0 && errno == EINTR) {
    }
}

static bool identity_is_valid(struct process_identity identity)
{
    return identity.high != UINT64_C(0) || identity.low != UINT64_C(0);
}

#if defined(__linux__)
static bool read_process_identity(
    pid_t process_id,
    struct process_identity *identity,
    bool *is_zombie)
{
    char path[64];
    int path_length = snprintf(
        path,
        sizeof(path),
        "/proc/%jd/stat",
        (intmax_t)process_id);
    if (path_length <= 0 || (size_t)path_length >= sizeof(path)) {
        return false;
    }
    int descriptor = open(path, O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) {
        return false;
    }
    char buffer[4096];
    ssize_t received;
    do {
        received = read(descriptor, buffer, sizeof(buffer) - 1U);
    } while (received < 0 && errno == EINTR);
    close_quietly(descriptor);
    if (received <= 0) {
        return false;
    }
    buffer[(size_t)received] = '\0';
    char *end_name = strrchr(buffer, ')');
    if (end_name == NULL || end_name[1] != ' ') {
        return false;
    }
    char *cursor = end_name + 2;
    char state = '\0';
    for (int field = 3; field <= 22; ++field) {
        char *end = cursor;
        while (*end != '\0' && *end != ' ') {
            ++end;
        }
        if (field == 3) {
            state = *cursor;
        }
        if (field == 22) {
            char saved = *end;
            *end = '\0';
            errno = 0;
            char *parsed_end = NULL;
            unsigned long long parsed = strtoull(cursor, &parsed_end, 10);
            bool parsed_valid =
                errno == 0 && parsed_end != cursor && *parsed_end == '\0';
            *end = saved;
            if (!parsed_valid) {
                return false;
            }
            identity->high = UINT64_C(0);
            identity->low = (uint64_t)parsed;
            *is_zombie = state == 'Z';
            return identity_is_valid(*identity);
        }
        if (*end == '\0') {
            return false;
        }
        cursor = end + 1;
    }
    return false;
}
#elif defined(__APPLE__)
static bool read_process_identity(
    pid_t process_id,
    struct process_identity *identity,
    bool *is_zombie)
{
    struct proc_bsdinfo information;
    int received = proc_pidinfo(
        process_id,
        PROC_PIDTBSDINFO,
        0,
        &information,
        (int)sizeof(information));
    if (received != (int)sizeof(information)) {
        return false;
    }
    identity->high = (uint64_t)information.pbi_start_tvsec;
    identity->low = (uint64_t)information.pbi_start_tvusec;
    *is_zombie = information.pbi_status == SZOMB;
    return identity_is_valid(*identity);
}
#else
#error "The Unix registry host fixture supports only Linux and macOS."
#endif

static struct process_identity require_identity(pid_t process_id)
{
    for (int attempt = 0; attempt < 400; ++attempt) {
        struct process_identity identity;
        bool is_zombie = false;
        if (read_process_identity(process_id, &identity, &is_zombie) &&
            !is_zombie) {
            return identity;
        }
        sleep_milliseconds(UINT64_C(5));
    }
    _exit(70);
}

static const char *require_absolute_environment(const char *name)
{
    const char *value = getenv(name);
    if (value == NULL || value[0] != '/') {
        _exit(64);
    }
    return value;
}

static void wait_for_marker(const char *path)
{
    for (;;) {
        if (access(path, F_OK) == 0) {
            return;
        }
        if (errno != ENOENT) {
            _exit(70);
        }
        sleep_milliseconds(UINT64_C(5));
    }
}

static void write_marker(const char *path, const char *text, size_t length)
{
    int descriptor = open(path, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (descriptor < 0) {
        _exit(70);
    }
    write_full(descriptor, text, length);
    close_quietly(descriptor);
}

static void write_pid_marker(const char *path, pid_t process_id)
{
    char line[64];
    int length = snprintf(
        line,
        sizeof(line),
        "%jd\n",
        (intmax_t)process_id);
    if (length <= 0 || (size_t)length >= sizeof(line)) {
        _exit(70);
    }
    write_marker(path, line, (size_t)length);
}

static void wait_forever(void)
{
    for (;;) {
        pause();
    }
}

static void require_ignored_termination(void)
{
    if (signal(SIGTERM, SIG_IGN) == SIG_ERR ||
        signal(SIGHUP, SIG_IGN) == SIG_ERR) {
        _exit(70);
    }
}

static void worker_main(
    const char *arm_marker,
    const char *armed_marker,
    const char *release_marker,
    const char *descendant_marker)
{
    require_ignored_termination();
    wait_for_marker(arm_marker);
    if (setpgid(0, 0) != 0 || getpgrp() != getpid()) {
        _exit(70);
    }
    write_pid_marker(armed_marker, getpid());
    wait_for_marker(release_marker);

    pid_t descendant = fork();
    if (descendant < 0) {
        _exit(70);
    }
    if (descendant == 0) {
        require_ignored_termination();
        wait_forever();
    }
    write_pid_marker(descendant_marker, descendant);
    wait_forever();
}

static void worker_broker_main(
    int facts_write,
    const char *arm_marker,
    const char *armed_marker,
    const char *release_marker,
    const char *descendant_marker)
{
    require_ignored_termination();
    pid_t worker = fork();
    if (worker < 0) {
        _exit(70);
    }
    if (worker == 0) {
        close_quietly(facts_write);
        worker_main(
            arm_marker,
            armed_marker,
            release_marker,
            descendant_marker);
    }

    struct worker_facts facts;
    facts.broker_pid = getpid();
    facts.broker_identity = require_identity(facts.broker_pid);
    facts.worker_pid = worker;
    facts.worker_identity = require_identity(worker);
    write_full(facts_write, &facts, sizeof(facts));
    close_quietly(facts_write);
    wait_forever();
}

int main(int argc, char **argv)
{
    if (argc != 2 || strcmp(argv[1], "--host") != 0) {
        return 64;
    }
    const char *facts_marker =
        require_absolute_environment("PTK_UNIX_REGISTRY_FIXTURE_FACTS");
    const char *arm_marker =
        require_absolute_environment("PTK_UNIX_REGISTRY_FIXTURE_ARM");
    const char *armed_marker =
        require_absolute_environment("PTK_UNIX_REGISTRY_FIXTURE_ARMED");
    const char *release_marker =
        require_absolute_environment("PTK_UNIX_REGISTRY_FIXTURE_RELEASE");
    const char *descendant_marker =
        require_absolute_environment("PTK_UNIX_REGISTRY_FIXTURE_DESCENDANT");
    require_ignored_termination();

    int facts_pipe[2];
    if (pipe(facts_pipe) != 0) {
        return 70;
    }
    pid_t worker_broker = fork();
    if (worker_broker < 0) {
        return 70;
    }
    if (worker_broker == 0) {
        close_quietly(facts_pipe[0]);
        worker_broker_main(
            facts_pipe[1],
            arm_marker,
            armed_marker,
            release_marker,
            descendant_marker);
    }

    close_quietly(facts_pipe[1]);
    struct worker_facts facts;
    read_full(facts_pipe[0], &facts, sizeof(facts));
    close_quietly(facts_pipe[0]);
    if (facts.broker_pid != worker_broker ||
        !identity_is_valid(facts.broker_identity) ||
        !identity_is_valid(facts.worker_identity)) {
        return 70;
    }

    char line[256];
    int length = snprintf(
        line,
        sizeof(line),
        "%jd %jd %" PRIu64 " %" PRIu64 " %jd %" PRIu64 " %" PRIu64 "\n",
        (intmax_t)getpid(),
        (intmax_t)facts.broker_pid,
        facts.broker_identity.high,
        facts.broker_identity.low,
        (intmax_t)facts.worker_pid,
        facts.worker_identity.high,
        facts.worker_identity.low);
    if (length <= 0 || (size_t)length >= sizeof(line)) {
        return 70;
    }
    write_marker(facts_marker, line, (size_t)length);
    wait_forever();
}
