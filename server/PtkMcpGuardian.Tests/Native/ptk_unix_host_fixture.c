#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <unistd.h>

static void write_full(int descriptor, const void *buffer, size_t length)
{
    const char *cursor = (const char *)buffer;
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

static void wait_forever(void)
{
    for (;;) {
        pause();
    }
}

int main(int argc, char **argv)
{
    if (argc != 2 || strcmp(argv[1], "--host") != 0) {
        return 64;
    }
    const char *marker = getenv("PTK_UNIX_HOST_FIXTURE_MARKER");
    if (marker == NULL || marker[0] != '/') {
        return 64;
    }
    if (signal(SIGTERM, SIG_IGN) == SIG_ERR) {
        return 70;
    }

    int identities[2];
    if (pipe(identities) != 0) {
        return 70;
    }
    pid_t child = fork();
    if (child < 0) {
        return 70;
    }
    if (child == 0) {
        close(identities[0]);
        pid_t grandchild = fork();
        if (grandchild < 0) {
            _exit(70);
        }
        if (grandchild == 0) {
            close(identities[1]);
            wait_forever();
        }
        pid_t values[2] = {getpid(), grandchild};
        write_full(identities[1], values, sizeof(values));
        close(identities[1]);
        wait_forever();
    }

    close(identities[1]);
    pid_t values[2];
    size_t received = 0U;
    while (received < sizeof(values)) {
        ssize_t count = read(
            identities[0],
            ((char *)values) + received,
            sizeof(values) - received);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            return 70;
        }
        received += (size_t)count;
    }
    close(identities[0]);

    int output = open(marker, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (output < 0) {
        return 70;
    }
    char line[128];
    int length = snprintf(
        line,
        sizeof(line),
        "%ld %ld %ld\n",
        (long)getpid(),
        (long)values[0],
        (long)values[1]);
    if (length <= 0 || (size_t)length >= sizeof(line)) {
        return 70;
    }
    write_full(output, line, (size_t)length);
    close(output);
    wait_forever();
}
