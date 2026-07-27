#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <inttypes.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/types.h>
#include <unistd.h>

static void fail_now(const char *operation)
{
    int saved_error = errno;
    (void)dprintf(
        STDERR_FILENO,
        "worker fixture failure: %s errno=%d\n",
        operation,
        saved_error);
    _exit(70);
}

static void close_quietly(int descriptor)
{
    if (descriptor >= 0) {
        (void)close(descriptor);
    }
}

static void ignore_term(void)
{
    if (signal(SIGTERM, SIG_IGN) == SIG_ERR) {
        fail_now("signal");
    }
}

static void write_all(int descriptor, const void *buffer, size_t length)
{
    const unsigned char *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0U) {
        ssize_t written = write(descriptor, cursor, remaining);
        if (written < 0 && errno == EINTR) {
            continue;
        }
        if (written <= 0) {
            fail_now("write");
        }
        cursor += (size_t)written;
        remaining -= (size_t)written;
    }
}

static void read_all(int descriptor, void *buffer, size_t length)
{
    unsigned char *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0U) {
        ssize_t received = read(descriptor, cursor, remaining);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received <= 0) {
            fail_now("read");
        }
        cursor += (size_t)received;
        remaining -= (size_t)received;
    }
}

static void wait_forever(void)
{
    for (;;) {
        (void)pause();
    }
}

int main(void)
{
    pid_t worker_pid = getpid();
    pid_t worker_group = getpgrp();
    if (worker_pid <= 0 || worker_group != worker_pid) {
        errno = EPROTO;
        fail_now("worker process group");
    }

    (void)unsetenv("PTK_WORKER_REQUEST_HANDLE");
    (void)unsetenv("PTK_WORKER_EVENT_HANDLE");
    close_quietly(3);
    close_quietly(4);
    ignore_term();

    int report_descriptors[2];
    if (pipe(report_descriptors) != 0) {
        fail_now("pipe");
    }

    pid_t descendant_pid = fork();
    if (descendant_pid < 0) {
        fail_now("fork");
    }
    if (descendant_pid == 0) {
        close_quietly(report_descriptors[0]);
        ignore_term();
        pid_t grandchild_pid = fork();
        if (grandchild_pid < 0) {
            fail_now("fork grandchild");
        }
        if (grandchild_pid == 0) {
            close_quietly(report_descriptors[1]);
            ignore_term();
            wait_forever();
        }
        write_all(
            report_descriptors[1],
            &grandchild_pid,
            sizeof(grandchild_pid));
        close_quietly(report_descriptors[1]);
        wait_forever();
    }

    close_quietly(report_descriptors[1]);
    pid_t grandchild_pid = 0;
    read_all(
        report_descriptors[0],
        &grandchild_pid,
        sizeof(grandchild_pid));
    close_quietly(report_descriptors[0]);

    pid_t descendant_group = getpgid(descendant_pid);
    if (descendant_group != worker_group) {
        errno = EPROTO;
        fail_now("descendant process group");
    }
    pid_t grandchild_group = getpgid(grandchild_pid);
    if (grandchild_pid <= 0 || grandchild_group != worker_group) {
        errno = EPROTO;
        fail_now("grandchild process group");
    }

    if (dprintf(
            STDOUT_FILENO,
            "{\"workerPid\":%jd,\"workerPgid\":%jd,"
            "\"descendantPid\":%jd,\"descendantPgid\":%jd,"
            "\"grandchildPid\":%jd,\"grandchildPgid\":%jd}\n",
            (intmax_t)worker_pid,
            (intmax_t)worker_group,
            (intmax_t)descendant_pid,
            (intmax_t)descendant_group,
            (intmax_t)grandchild_pid,
            (intmax_t)grandchild_group) < 0) {
        fail_now("write readiness");
    }

    wait_forever();
    return 0;
}
