#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define PTK_TERM_TO_KILL_MILLISECONDS 2000
#define PTK_CONTAINMENT_DEADLINE_MILLISECONDS 10000
#define PTK_IDENTITY_POLL_MILLISECONDS 25
#define PTK_EVENT_MAGIC UINT32_C(0x50544b42)
#define PTK_PROTOCOL_VERSION UINT32_C(1)
#define PTK_EVENT_BYTES 32U
#define PTK_COMMAND_BYTES 16U

enum event_kind {
    EVENT_READY = 1,
    EVENT_HOST_EXITED = 2,
    EVENT_CONTAINMENT_CONFIRMED = 3,
    EVENT_CONTAINMENT_FAILED = 4
};

enum command_kind {
    COMMAND_START = 1,
    COMMAND_STOP = 2
};

static void close_quietly(int descriptor)
{
    if (descriptor >= 0) {
        (void)close(descriptor);
    }
}

static bool write_full(int descriptor, const void *buffer, size_t length)
{
    const uint8_t *cursor = (const uint8_t *)buffer;
    while (length > 0U) {
        ssize_t written = write(descriptor, cursor, length);
        if (written < 0 && errno == EINTR) {
            continue;
        }
        if (written <= 0) {
            return false;
        }
        cursor += (size_t)written;
        length -= (size_t)written;
    }
    return true;
}

static bool read_full(int descriptor, void *buffer, size_t length)
{
    uint8_t *cursor = (uint8_t *)buffer;
    while (length > 0U) {
        ssize_t received = read(descriptor, cursor, length);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received <= 0) {
            return false;
        }
        cursor += (size_t)received;
        length -= (size_t)received;
    }
    return true;
}

static void encode_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)(value >> 24);
    destination[1] = (uint8_t)(value >> 16);
    destination[2] = (uint8_t)(value >> 8);
    destination[3] = (uint8_t)value;
}

static uint32_t decode_u32(const uint8_t *source)
{
    return ((uint32_t)source[0] << 24) |
        ((uint32_t)source[1] << 16) |
        ((uint32_t)source[2] << 8) |
        (uint32_t)source[3];
}

static bool send_event(
    int descriptor,
    enum event_kind kind,
    pid_t host_pid,
    pid_t broker_pid,
    uint32_t value)
{
    uint8_t frame[PTK_EVENT_BYTES] = {0};
    encode_u32(frame, PTK_EVENT_MAGIC);
    encode_u32(frame + 4U, PTK_PROTOCOL_VERSION);
    encode_u32(frame + 8U, (uint32_t)kind);
    encode_u32(frame + 16U, (uint32_t)host_pid);
    encode_u32(frame + 20U, (uint32_t)broker_pid);
    encode_u32(frame + 24U, value);
    return write_full(descriptor, frame, sizeof(frame));
}

static bool receive_command(int descriptor, enum command_kind *kind)
{
    uint8_t frame[PTK_COMMAND_BYTES];
    if (!read_full(descriptor, frame, sizeof(frame))) {
        return false;
    }
    if (decode_u32(frame) != PTK_EVENT_MAGIC ||
        decode_u32(frame + 4U) != PTK_PROTOCOL_VERSION ||
        decode_u32(frame + 12U) != 0U) {
        return false;
    }
    uint32_t decoded = decode_u32(frame + 8U);
    if (decoded != (uint32_t)COMMAND_START && decoded != (uint32_t)COMMAND_STOP) {
        return false;
    }
    *kind = (enum command_kind)decoded;
    return true;
}

static uint64_t monotonic_milliseconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) {
        _exit(70);
    }
    return ((uint64_t)value.tv_sec * UINT64_C(1000)) +
        ((uint64_t)value.tv_nsec / UINT64_C(1000000));
}

static void sleep_milliseconds(uint64_t milliseconds)
{
    struct timespec value;
    value.tv_sec = (time_t)(milliseconds / UINT64_C(1000));
    value.tv_nsec = (long)((milliseconds % UINT64_C(1000)) * UINT64_C(1000000));
    while (nanosleep(&value, &value) != 0 && errno == EINTR) {
    }
}

static int parse_descriptor(const char *value)
{
    char *end = NULL;
    errno = 0;
    long parsed = strtol(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' || parsed < 3 || parsed > INT32_MAX) {
        return -1;
    }
    return (int)parsed;
}

static bool descriptor_is_open(int descriptor)
{
    errno = 0;
    return fcntl(descriptor, F_GETFD) >= 0 || errno != EBADF;
}

static bool group_exists(pid_t process_group)
{
    if (process_group <= 0) {
        return false;
    }
    if (kill(-process_group, 0) == 0) {
        return true;
    }
    return errno == EPERM;
}

static bool signal_host_group(pid_t host_pid, int signal_number)
{
    if (host_pid <= 0) {
        return false;
    }
    if (kill(-host_pid, signal_number) == 0 || errno == ESRCH) {
        return true;
    }
    return false;
}

static bool reap_direct_host(pid_t host_pid, bool *reaped)
{
    if (*reaped) {
        return true;
    }
    int status = 0;
    pid_t result;
    do {
        result = waitpid(host_pid, &status, WNOHANG);
    } while (result < 0 && errno == EINTR);
    if (result == host_pid || (result < 0 && errno == ECHILD)) {
        *reaped = true;
        return true;
    }
    return result == 0;
}

static void redirect_standard_handles(void)
{
    int null_input = open("/dev/null", O_RDONLY);
    int null_output = open("/dev/null", O_WRONLY);
    if (null_input < 0 || null_output < 0 ||
        dup2(null_input, STDIN_FILENO) < 0 ||
        dup2(null_output, STDOUT_FILENO) < 0 ||
        dup2(null_output, STDERR_FILENO) < 0) {
        _exit(70);
    }
    if (null_input > STDERR_FILENO) {
        close_quietly(null_input);
    }
    if (null_output > STDERR_FILENO && null_output != null_input) {
        close_quietly(null_output);
    }
}

static void close_descriptors_above(int maximum_preserved)
{
    struct rlimit limit;
    rlim_t maximum = UINT64_C(65536);
    if (getrlimit(RLIMIT_NOFILE, &limit) == 0 && limit.rlim_cur != RLIM_INFINITY) {
        maximum = limit.rlim_cur;
    }
    if (maximum > (rlim_t)INT32_MAX) {
        maximum = (rlim_t)INT32_MAX;
    }
    for (int descriptor = maximum_preserved + 1;
         (rlim_t)descriptor < maximum;
         ++descriptor) {
        close_quietly(descriptor);
    }
}

static void exec_gated_host(
    int ready_write,
    int release_read,
    int request_read,
    int event_write,
    int liveness_read,
    int command_read,
    int broker_event_write,
    const char *host_path)
{
    close_quietly(liveness_read);
    close_quietly(command_read);
    close_quietly(broker_event_write);
    if (setpgid(0, 0) != 0) {
        _exit(71);
    }
    const uint8_t gated = UINT8_C(1);
    if (!write_full(ready_write, &gated, sizeof(gated))) {
        _exit(71);
    }
    close_quietly(ready_write);

    uint8_t release = 0;
    if (!read_full(release_read, &release, sizeof(release)) || release != UINT8_C(1)) {
        _exit(72);
    }
    close_quietly(release_read);

    int request_copy = fcntl(request_read, F_DUPFD, 5);
    int event_copy = fcntl(event_write, F_DUPFD, 5);
    if (request_copy < 0 || event_copy < 0 ||
        dup2(request_copy, 3) < 0 || dup2(event_copy, 4) < 0) {
        _exit(72);
    }
    close_quietly(request_copy);
    close_quietly(event_copy);
    if (setenv("PTK_HOST_REQUEST_READ_HANDLE", "3", 1) != 0 ||
        setenv("PTK_HOST_EVENT_WRITE_HANDLE", "4", 1) != 0) {
        _exit(72);
    }
    close_descriptors_above(4);
    redirect_standard_handles();

    char *const arguments[] = {(char *)host_path, (char *)"--host", NULL};
    execv(host_path, arguments);
    _exit(127);
}

static bool wait_for_child_gate(int ready_read, int liveness_read)
{
    struct pollfd descriptors[2];
    descriptors[0].fd = ready_read;
    descriptors[0].events = POLLIN | POLLHUP;
    descriptors[1].fd = liveness_read;
    descriptors[1].events = POLLIN | POLLHUP;
    for (;;) {
        int result = poll(descriptors, 2U, -1);
        if (result < 0 && errno == EINTR) {
            continue;
        }
        if (result < 0) {
            return false;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t unexpected = 0;
            (void)read(liveness_read, &unexpected, sizeof(unexpected));
            return false;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t gated = 0;
            return read_full(ready_read, &gated, sizeof(gated)) && gated == UINT8_C(1);
        }
    }
}

static bool wait_for_start_command(int command_read, int liveness_read)
{
    struct pollfd descriptors[2];
    descriptors[0].fd = command_read;
    descriptors[0].events = POLLIN | POLLHUP;
    descriptors[1].fd = liveness_read;
    descriptors[1].events = POLLIN | POLLHUP;
    for (;;) {
        int result = poll(descriptors, 2U, -1);
        if (result < 0 && errno == EINTR) {
            continue;
        }
        if (result < 0) {
            return false;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t unexpected = 0;
            (void)read(liveness_read, &unexpected, sizeof(unexpected));
            return false;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            enum command_kind command;
            return receive_command(command_read, &command) && command == COMMAND_START;
        }
    }
}

static int contain_host(
    pid_t host_pid,
    int broker_event_write,
    bool host_reaped,
    bool host_exit_reported)
{
    uint64_t started = monotonic_milliseconds();
    (void)signal_host_group(host_pid, SIGTERM);
    while (monotonic_milliseconds() - started <
           (uint64_t)PTK_TERM_TO_KILL_MILLISECONDS) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            break;
        }
        if (host_reaped && !host_exit_reported) {
            (void)send_event(
                broker_event_write,
                EVENT_HOST_EXITED,
                host_pid,
                getpid(),
                0U);
            host_exit_reported = true;
        }
        sleep_milliseconds(PTK_IDENTITY_POLL_MILLISECONDS);
    }

    (void)signal_host_group(host_pid, SIGKILL);
    while (monotonic_milliseconds() - started <=
           (uint64_t)PTK_CONTAINMENT_DEADLINE_MILLISECONDS) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            break;
        }
        if (host_reaped && !host_exit_reported) {
            (void)send_event(
                broker_event_write,
                EVENT_HOST_EXITED,
                host_pid,
                getpid(),
                0U);
            host_exit_reported = true;
        }
        if (host_reaped && !group_exists(host_pid)) {
            (void)send_event(
                broker_event_write,
                EVENT_CONTAINMENT_CONFIRMED,
                host_pid,
                getpid(),
                0U);
            return 0;
        }
        sleep_milliseconds(PTK_IDENTITY_POLL_MILLISECONDS);
    }

    (void)send_event(
        broker_event_write,
        EVENT_CONTAINMENT_FAILED,
        host_pid,
        getpid(),
        0U);
    return 74;
}

static int broker_main(
    int liveness_read,
    int command_read,
    int broker_event_write,
    int request_read,
    int event_write,
    const char *working_directory,
    const char *host_path)
{
    if (signal(SIGPIPE, SIG_IGN) == SIG_ERR ||
        chdir(working_directory) != 0) {
        return 70;
    }
    redirect_standard_handles();

    int child_ready[2] = {-1, -1};
    int child_release[2] = {-1, -1};
    if (pipe(child_ready) != 0 || pipe(child_release) != 0) {
        return 70;
    }

    pid_t host_pid = fork();
    if (host_pid < 0) {
        return 70;
    }
    if (host_pid == 0) {
        close_quietly(child_ready[0]);
        close_quietly(child_release[1]);
        exec_gated_host(
            child_ready[1],
            child_release[0],
            request_read,
            event_write,
            liveness_read,
            command_read,
            broker_event_write,
            host_path);
    }

    close_quietly(child_ready[1]);
    close_quietly(child_release[0]);
    close_quietly(request_read);
    close_quietly(event_write);
    bool host_reaped = false;
    bool host_exit_reported = false;

    if (setpgid(host_pid, host_pid) != 0 && errno != EACCES) {
        close_quietly(child_release[1]);
        return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
    }
    if (!wait_for_child_gate(child_ready[0], liveness_read)) {
        close_quietly(child_ready[0]);
        close_quietly(child_release[1]);
        return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
    }
    close_quietly(child_ready[0]);
    if (getpgid(host_pid) != host_pid || getpgrp() == host_pid) {
        close_quietly(child_release[1]);
        return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
    }

    if (!send_event(
            broker_event_write,
            EVENT_READY,
            host_pid,
            getpid(),
            (uint32_t)host_pid) ||
        !wait_for_start_command(command_read, liveness_read)) {
        close_quietly(child_release[1]);
        return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
    }

    const uint8_t release = UINT8_C(1);
    if (!write_full(child_release[1], &release, sizeof(release))) {
        close_quietly(child_release[1]);
        return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
    }
    close_quietly(child_release[1]);

    for (;;) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
        }
        if (host_reaped) {
            if (!host_exit_reported) {
                (void)send_event(
                    broker_event_write,
                    EVENT_HOST_EXITED,
                    host_pid,
                    getpid(),
                    0U);
                host_exit_reported = true;
            }
            return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
        }

        struct pollfd descriptors[2];
        descriptors[0].fd = liveness_read;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[1].fd = command_read;
        descriptors[1].events = POLLIN | POLLHUP;
        int result = poll(
            descriptors,
            2U,
            PTK_IDENTITY_POLL_MILLISECONDS);
        if (result < 0 && errno == EINTR) {
            continue;
        }
        if (result < 0) {
            return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t unexpected = 0;
            (void)read(liveness_read, &unexpected, sizeof(unexpected));
            return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            enum command_kind command;
            if (!receive_command(command_read, &command) || command != COMMAND_STOP) {
                return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
            }
            return contain_host(host_pid, broker_event_write, host_reaped, host_exit_reported);
        }
    }
}

int main(int argc, char **argv)
{
    if (argc != 9 || strcmp(argv[1], "--broker-v1") != 0) {
        return 64;
    }
    int liveness_read = parse_descriptor(argv[2]);
    int command_read = parse_descriptor(argv[3]);
    int broker_event_write = parse_descriptor(argv[4]);
    int request_read = parse_descriptor(argv[5]);
    int event_write = parse_descriptor(argv[6]);
    if (liveness_read < 0 || command_read < 0 || broker_event_write < 0 ||
        request_read < 0 || event_write < 0 ||
        liveness_read == command_read || liveness_read == broker_event_write ||
        liveness_read == request_read || liveness_read == event_write ||
        command_read == broker_event_write || command_read == request_read ||
        command_read == event_write || broker_event_write == request_read ||
        broker_event_write == event_write || request_read == event_write ||
        !descriptor_is_open(liveness_read) || !descriptor_is_open(command_read) ||
        !descriptor_is_open(broker_event_write) || !descriptor_is_open(request_read) ||
        !descriptor_is_open(event_write) || argv[7][0] != '/' || argv[8][0] != '/') {
        return 64;
    }
    return broker_main(
        liveness_read,
        command_read,
        broker_event_write,
        request_read,
        event_write,
        argv[7],
        argv[8]);
}
