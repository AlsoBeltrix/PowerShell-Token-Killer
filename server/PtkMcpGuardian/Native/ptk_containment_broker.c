#if defined(__APPLE__)
#define _DARWIN_C_SOURCE
#endif
#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <libproc.h>
#endif

#define PTK_MAGIC_0 UINT8_C(0x50)
#define PTK_MAGIC_1 UINT8_C(0x54)
#define PTK_MAGIC_2 UINT8_C(0x4b)
#define PTK_MAGIC_3 UINT8_C(0x42)
#define PTK_PROTOCOL_VERSION UINT8_C(2)
#define PTK_HEADER_BYTES 8U
#define PTK_MAXIMUM_PAYLOAD_BYTES 64U
#define PTK_TERM_TO_KILL_MILLISECONDS UINT64_C(2000)
#define PTK_CONTAINMENT_DEADLINE_MILLISECONDS UINT64_C(10000)
#define PTK_POLL_MILLISECONDS 25

#define PTK_LIVENESS_READ 3
#define PTK_CONTROL_READ 4
#define PTK_EVENT_WRITE 5
#define PTK_WORKER_REQUEST_READ 6
#define PTK_WORKER_EVENT_WRITE 7
#define PTK_WORKER_STDOUT_WRITE 8
#define PTK_WORKER_STDERR_WRITE 9

enum command_kind {
    COMMAND_START = 1,
    COMMAND_ARM_GROUP = 2,
    COMMAND_RELEASE = 3,
    COMMAND_SHUTDOWN = 4
};

enum event_kind {
    EVENT_HELLO = 1,
    EVENT_CHILD_GATED = 2,
    EVENT_ARMED = 3,
    EVENT_RELEASED = 4,
    EVENT_START_FAILED = 5
};

enum start_failure_stage {
    STAGE_FORK = 1,
    STAGE_CHILD_SETUP = 2,
    STAGE_IDENTITY_CAPTURE = 3,
    STAGE_GROUP_ARM = 4,
    STAGE_GROUP_VALIDATE = 5,
    STAGE_GATE_RELEASE = 6,
    STAGE_EXEC = 7
};

enum receive_result {
    RECEIVE_LIVENESS_LOST = -2,
    RECEIVE_INVALID = -1,
    RECEIVE_EOF = 0,
    RECEIVE_OK = 1
};

enum signal_result {
    SIGNAL_ERROR = -1,
    SIGNAL_DEFERRED = 0,
    SIGNAL_DELIVERED = 1
};

struct process_identity {
    uint64_t high;
    uint64_t low;
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

static void encode_u16(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)(value >> 8);
    destination[1] = (uint8_t)value;
}

static uint16_t decode_u16(const uint8_t *source)
{
    return (uint16_t)(((uint16_t)source[0] << 8) | (uint16_t)source[1]);
}

static void encode_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)(value >> 24);
    destination[1] = (uint8_t)(value >> 16);
    destination[2] = (uint8_t)(value >> 8);
    destination[3] = (uint8_t)value;
}

static void encode_u64(uint8_t *destination, uint64_t value)
{
    for (size_t index = 0U; index < 8U; ++index) {
        destination[index] =
            (uint8_t)(value >> (56U - (unsigned int)(index * 8U)));
    }
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
    value.tv_nsec =
        (long)((milliseconds % UINT64_C(1000)) * UINT64_C(1000000));
    while (nanosleep(&value, &value) != 0 && errno == EINTR) {
    }
}

static bool descriptor_is_open(int descriptor)
{
    errno = 0;
    return fcntl(descriptor, F_GETFD) >= 0 || errno != EBADF;
}

static bool set_close_on_exec(int descriptor)
{
    int flags = fcntl(descriptor, F_GETFD);
    return flags >= 0 && fcntl(descriptor, F_SETFD, flags | FD_CLOEXEC) == 0;
}

static void close_descriptors_above(int maximum_preserved)
{
    struct rlimit limit;
    rlim_t maximum = UINT64_C(65536);
    if (getrlimit(RLIMIT_NOFILE, &limit) == 0 &&
        limit.rlim_cur != RLIM_INFINITY) {
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

static bool send_event(
    enum event_kind kind,
    const uint8_t *payload,
    uint16_t payload_length)
{
    if (payload_length > PTK_MAXIMUM_PAYLOAD_BYTES) {
        return false;
    }
    uint8_t frame[PTK_HEADER_BYTES + PTK_MAXIMUM_PAYLOAD_BYTES] = {0};
    frame[0] = PTK_MAGIC_0;
    frame[1] = PTK_MAGIC_1;
    frame[2] = PTK_MAGIC_2;
    frame[3] = PTK_MAGIC_3;
    frame[4] = PTK_PROTOCOL_VERSION;
    frame[5] = (uint8_t)kind;
    encode_u16(frame + 6U, payload_length);
    if (payload_length > 0U) {
        (void)memcpy(frame + PTK_HEADER_BYTES, payload, payload_length);
    }
    return write_full(
        PTK_EVENT_WRITE,
        frame,
        PTK_HEADER_BYTES + (size_t)payload_length);
}

static enum receive_result read_control_bytes(
    void *buffer,
    size_t length)
{
    uint8_t *cursor = (uint8_t *)buffer;
    while (length > 0U) {
        struct pollfd descriptors[2];
        descriptors[0].fd = PTK_CONTROL_READ;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = PTK_LIVENESS_READ;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int result;
        do {
            result = poll(descriptors, 2U, -1);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return RECEIVE_INVALID;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t ignored = 0U;
            ssize_t received;
            do {
                received = read(PTK_LIVENESS_READ, &ignored, sizeof(ignored));
            } while (received < 0 && errno == EINTR);
            return RECEIVE_LIVENESS_LOST;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) == 0) {
            continue;
        }
        ssize_t received = read(PTK_CONTROL_READ, cursor, length);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received < 0) {
            return RECEIVE_INVALID;
        }
        if (received == 0) {
            return RECEIVE_EOF;
        }
        cursor += (size_t)received;
        length -= (size_t)received;
    }
    return RECEIVE_OK;
}

static enum receive_result receive_command(enum command_kind *kind)
{
    uint8_t header[PTK_HEADER_BYTES];
    enum receive_result result = read_control_bytes(header, sizeof(header));
    if (result != RECEIVE_OK) {
        return result;
    }
    if (header[0] != PTK_MAGIC_0 || header[1] != PTK_MAGIC_1 ||
        header[2] != PTK_MAGIC_2 || header[3] != PTK_MAGIC_3 ||
        header[4] != PTK_PROTOCOL_VERSION ||
        decode_u16(header + 6U) != 0U) {
        return RECEIVE_INVALID;
    }
    uint8_t value = header[5];
    if (value < (uint8_t)COMMAND_START ||
        value > (uint8_t)COMMAND_SHUTDOWN) {
        return RECEIVE_INVALID;
    }
    *kind = (enum command_kind)value;
    return RECEIVE_OK;
}

#if defined(__linux__)
static bool read_process_identity(
    pid_t process_id,
    struct process_identity *identity)
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
    for (int field = 3; field <= 22; ++field) {
        char *end = cursor;
        while (*end != '\0' && *end != ' ') {
            ++end;
        }
        if (field == 22) {
            char saved = *end;
            *end = '\0';
            errno = 0;
            char *parsed_end = NULL;
            unsigned long long parsed = strtoull(cursor, &parsed_end, 10);
            *end = saved;
            if (errno != 0 || parsed_end == cursor || *parsed_end != '\0') {
                return false;
            }
            identity->high = UINT64_C(0);
            identity->low = (uint64_t)parsed;
            return identity->low != UINT64_C(0);
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
    struct process_identity *identity)
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
    return identity->high != UINT64_C(0) || identity->low != UINT64_C(0);
}
#else
#error "PtkContainmentBroker supports only Linux and macOS."
#endif

static bool identities_equal(
    struct process_identity left,
    struct process_identity right)
{
    return left.high == right.high && left.low == right.low;
}

static bool identity_is_live(
    pid_t process_id,
    struct process_identity expected)
{
    struct process_identity actual;
    return process_id > 0 &&
        read_process_identity(process_id, &actual) &&
        identities_equal(actual, expected);
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

static bool reap_worker(pid_t worker_pid, bool *reaped)
{
    if (*reaped) {
        return true;
    }
    int status = 0;
    pid_t result;
    do {
        result = waitpid(worker_pid, &status, WNOHANG);
    } while (result < 0 && errno == EINTR);
    if (result == worker_pid || (result < 0 && errno == ECHILD)) {
        *reaped = true;
        return true;
    }
    return result == 0;
}

static enum signal_result signal_worker(
    pid_t worker_pid,
    struct process_identity identity,
    bool armed,
    bool worker_reaped,
    int signal_number)
{
    if (worker_reaped) {
        if (!armed || !group_exists(worker_pid)) {
            return SIGNAL_DELIVERED;
        }
        return kill(-worker_pid, signal_number) == 0 || errno == ESRCH
            ? SIGNAL_DELIVERED
            : SIGNAL_ERROR;
    }
    bool identity_unknown =
        identity.high == UINT64_C(0) && identity.low == UINT64_C(0);
    if ((!identity_unknown || armed) &&
        !identity_is_live(worker_pid, identity)) {
        return SIGNAL_DEFERRED;
    }
    pid_t target = armed ? -worker_pid : worker_pid;
    return kill(target, signal_number) == 0 || errno == ESRCH
        ? SIGNAL_DELIVERED
        : SIGNAL_ERROR;
}

static bool worker_contained(
    pid_t worker_pid,
    bool armed,
    bool worker_reaped)
{
    return worker_reaped && (!armed || !group_exists(worker_pid));
}

static bool contain_worker(
    pid_t worker_pid,
    struct process_identity identity,
    bool armed)
{
    bool reaped = false;
    uint64_t started = monotonic_milliseconds();
    bool term_delivered = false;
    while (monotonic_milliseconds() - started <
           PTK_TERM_TO_KILL_MILLISECONDS) {
        if (!reap_worker(worker_pid, &reaped)) {
            return false;
        }
        if (worker_contained(worker_pid, armed, reaped)) {
            return true;
        }
        if (!term_delivered) {
            enum signal_result result = signal_worker(
                worker_pid,
                identity,
                armed,
                reaped,
                SIGTERM);
            if (result == SIGNAL_ERROR) {
                return false;
            }
            term_delivered = result == SIGNAL_DELIVERED;
        }
        sleep_milliseconds(PTK_POLL_MILLISECONDS);
    }
    bool kill_delivered = false;
    while (monotonic_milliseconds() - started <=
           PTK_CONTAINMENT_DEADLINE_MILLISECONDS) {
        if (!reap_worker(worker_pid, &reaped)) {
            return false;
        }
        if (worker_contained(worker_pid, armed, reaped)) {
            return true;
        }
        if (!kill_delivered) {
            enum signal_result result = signal_worker(
                worker_pid,
                identity,
                armed,
                reaped,
                SIGKILL);
            if (result == SIGNAL_ERROR) {
                return false;
            }
            kill_delivered = result == SIGNAL_DELIVERED;
        }
        sleep_milliseconds(PTK_POLL_MILLISECONDS);
    }
    return false;
}

static void encode_identity(
    uint8_t *destination,
    pid_t process_id,
    struct process_identity identity)
{
    encode_u32(destination, (uint32_t)process_id);
    encode_u64(destination + 4U, identity.high);
    encode_u64(destination + 12U, identity.low);
}

static bool send_hello(
    pid_t broker_pid,
    struct process_identity broker_identity)
{
    uint8_t payload[20];
    encode_identity(payload, broker_pid, broker_identity);
    return send_event(EVENT_HELLO, payload, (uint16_t)sizeof(payload));
}

static bool send_child_event(
    enum event_kind kind,
    pid_t broker_pid,
    struct process_identity broker_identity,
    pid_t worker_pid,
    struct process_identity worker_identity,
    bool include_process_group)
{
    uint8_t payload[44];
    encode_identity(payload, broker_pid, broker_identity);
    encode_identity(payload + 20U, worker_pid, worker_identity);
    uint16_t length = 40U;
    if (include_process_group) {
        encode_u32(payload + 40U, (uint32_t)worker_pid);
        length = 44U;
    }
    return send_event(kind, payload, length);
}

static bool send_start_failed(
    enum start_failure_stage stage,
    int error_number)
{
    uint8_t payload[5];
    payload[0] = (uint8_t)stage;
    encode_u32(payload + 1U, (uint32_t)error_number);
    return send_event(EVENT_START_FAILED, payload, (uint16_t)sizeof(payload));
}

static void redirect_worker_handles(int exec_error_write)
{
    int null_input = open("/dev/null", O_RDONLY);
    if (null_input < 0 ||
        dup2(exec_error_write, 5) < 0 ||
        !set_close_on_exec(5) ||
        dup2(null_input, STDIN_FILENO) < 0 ||
        dup2(PTK_WORKER_STDOUT_WRITE, STDOUT_FILENO) < 0 ||
        dup2(PTK_WORKER_STDERR_WRITE, STDERR_FILENO) < 0) {
        int failure = errno;
        if (null_input >= 0) {
            close_quietly(null_input);
        }
        errno = failure;
        return;
    }
    if (null_input > STDERR_FILENO) {
        close_quietly(null_input);
    }
    if (dup2(PTK_WORKER_REQUEST_READ, 3) < 0 ||
        dup2(PTK_WORKER_EVENT_WRITE, 4) < 0) {
        return;
    }
    close_descriptors_above(5);
    errno = 0;
}

static void gated_worker_main(
    int gated_write,
    int release_read,
    int exec_error_write,
    const char *working_directory,
    char *const worker_arguments[])
{
    close_quietly(PTK_LIVENESS_READ);
    close_quietly(PTK_CONTROL_READ);
    close_quietly(PTK_EVENT_WRITE);
    if (signal(SIGTERM, SIG_DFL) == SIG_ERR) {
        _exit(71);
    }
    const uint8_t gated = UINT8_C(1);
    if (!write_full(gated_write, &gated, sizeof(gated))) {
        _exit(71);
    }
    close_quietly(gated_write);

    uint8_t release = 0U;
    ssize_t received;
    do {
        received = read(release_read, &release, sizeof(release));
    } while (received < 0 && errno == EINTR);
    close_quietly(release_read);
    if (received != (ssize_t)sizeof(release) || release != UINT8_C(1)) {
        _exit(71);
    }
    if (chdir(working_directory) != 0) {
        int failure = errno;
        (void)write_full(exec_error_write, &failure, sizeof(failure));
        _exit(72);
    }
    redirect_worker_handles(exec_error_write);
    if (errno != 0) {
        int failure = errno;
        (void)write_full(5, &failure, sizeof(failure));
        _exit(72);
    }

    execv(worker_arguments[0], worker_arguments);
    int failure = errno;
    (void)write_full(5, &failure, sizeof(failure));
    _exit(72);
}

static enum receive_result wait_for_gated_child(int descriptor)
{
    for (;;) {
        struct pollfd descriptors[2];
        descriptors[0].fd = descriptor;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = PTK_LIVENESS_READ;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int result;
        do {
            result = poll(descriptors, 2U, -1);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return RECEIVE_INVALID;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            return RECEIVE_LIVENESS_LOST;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            uint8_t value = 0U;
            ssize_t received;
            do {
                received = read(descriptor, &value, sizeof(value));
            } while (received < 0 && errno == EINTR);
            return received == (ssize_t)sizeof(value) && value == UINT8_C(1)
                ? RECEIVE_OK
                : RECEIVE_INVALID;
        }
    }
}

static enum receive_result wait_for_exec_result(
    int descriptor,
    int *exec_error)
{
    size_t length = 0U;
    uint8_t *destination = (uint8_t *)exec_error;
    for (;;) {
        struct pollfd descriptors[2];
        descriptors[0].fd = descriptor;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = PTK_LIVENESS_READ;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int result;
        do {
            result = poll(descriptors, 2U, -1);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return RECEIVE_INVALID;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            return RECEIVE_LIVENESS_LOST;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) == 0) {
            continue;
        }
        ssize_t received = read(
            descriptor,
            destination + length,
            sizeof(*exec_error) - length);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received < 0) {
            return RECEIVE_INVALID;
        }
        if (received == 0) {
            return length == 0U ? RECEIVE_EOF : RECEIVE_INVALID;
        }
        length += (size_t)received;
        if (length == sizeof(*exec_error)) {
            return RECEIVE_OK;
        }
    }
}

static int failure_exit_code(enum start_failure_stage stage)
{
    if (stage == STAGE_FORK) {
        return 70;
    }
    return stage == STAGE_EXEC ? 72 : 71;
}

static int fail_started_worker(
    enum start_failure_stage stage,
    int error_number,
    pid_t worker_pid,
    struct process_identity worker_identity,
    bool armed)
{
    bool contained = contain_worker(
        worker_pid,
        worker_identity,
        armed);
    if (contained) {
        (void)send_start_failed(stage, error_number);
        return failure_exit_code(stage);
    }
    return 74;
}

static int monitor_worker(
    pid_t worker_pid,
    struct process_identity worker_identity)
{
    bool worker_reaped = false;
    for (;;) {
        if (!reap_worker(worker_pid, &worker_reaped)) {
            return contain_worker(worker_pid, worker_identity, true) ? 71 : 74;
        }
        struct pollfd descriptors[2];
        descriptors[0].fd = PTK_LIVENESS_READ;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = PTK_CONTROL_READ;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int result;
        do {
            result = poll(
                descriptors,
                2U,
                PTK_POLL_MILLISECONDS);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return contain_worker(worker_pid, worker_identity, true) ? 64 : 74;
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            return contain_worker(worker_pid, worker_identity, true) ? 73 : 74;
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
            enum command_kind command;
            enum receive_result received = receive_command(&command);
            if (received == RECEIVE_LIVENESS_LOST) {
                return contain_worker(worker_pid, worker_identity, true)
                    ? 73
                    : 74;
            }
            bool valid_shutdown =
                received == RECEIVE_OK && command == COMMAND_SHUTDOWN;
            bool contained = contain_worker(
                worker_pid,
                worker_identity,
                true);
            if (!contained) {
                return 74;
            }
            return valid_shutdown ? 0 : 64;
        }
    }
}

static int broker_main(
    const char *working_directory,
    char *const worker_arguments[])
{
    if (signal(SIGPIPE, SIG_IGN) == SIG_ERR ||
        signal(SIGTERM, SIG_IGN) == SIG_ERR) {
        return 70;
    }
    for (int descriptor = PTK_LIVENESS_READ;
         descriptor <= PTK_WORKER_STDERR_WRITE;
         ++descriptor) {
        if (!descriptor_is_open(descriptor) ||
            !set_close_on_exec(descriptor)) {
            return 70;
        }
    }
    close_descriptors_above(PTK_WORKER_STDERR_WRITE);

    pid_t broker_pid = getpid();
    struct process_identity broker_identity;
    if (!read_process_identity(broker_pid, &broker_identity) ||
        !send_hello(broker_pid, broker_identity)) {
        return 70;
    }
    enum command_kind command;
    enum receive_result received = receive_command(&command);
    if (received == RECEIVE_LIVENESS_LOST) {
        return 73;
    }
    if (received != RECEIVE_OK || command != COMMAND_START) {
        return 64;
    }

    int gated[2] = {-1, -1};
    int release[2] = {-1, -1};
    int exec_error[2] = {-1, -1};
    if (pipe(gated) != 0 || pipe(release) != 0 || pipe(exec_error) != 0) {
        int failure = errno;
        close_quietly(gated[0]);
        close_quietly(gated[1]);
        close_quietly(release[0]);
        close_quietly(release[1]);
        close_quietly(exec_error[0]);
        close_quietly(exec_error[1]);
        (void)send_start_failed(STAGE_FORK, failure);
        return 70;
    }
    if (!set_close_on_exec(exec_error[1])) {
        int failure = errno;
        close_quietly(gated[0]);
        close_quietly(gated[1]);
        close_quietly(release[0]);
        close_quietly(release[1]);
        close_quietly(exec_error[0]);
        close_quietly(exec_error[1]);
        (void)send_start_failed(STAGE_FORK, failure);
        return 70;
    }

    pid_t worker_pid = fork();
    if (worker_pid < 0) {
        int failure = errno;
        close_quietly(gated[0]);
        close_quietly(gated[1]);
        close_quietly(release[0]);
        close_quietly(release[1]);
        close_quietly(exec_error[0]);
        close_quietly(exec_error[1]);
        (void)send_start_failed(STAGE_FORK, failure);
        return 70;
    }
    if (worker_pid == 0) {
        close_quietly(gated[0]);
        close_quietly(release[1]);
        close_quietly(exec_error[0]);
        gated_worker_main(
            gated[1],
            release[0],
            exec_error[1],
            working_directory,
            worker_arguments);
    }

    close_quietly(gated[1]);
    close_quietly(release[0]);
    close_quietly(exec_error[1]);
    close_quietly(PTK_WORKER_REQUEST_READ);
    close_quietly(PTK_WORKER_EVENT_WRITE);
    close_quietly(PTK_WORKER_STDOUT_WRITE);
    close_quietly(PTK_WORKER_STDERR_WRITE);

    struct process_identity worker_identity = {0};
    received = wait_for_gated_child(gated[0]);
    close_quietly(gated[0]);
    if (received != RECEIVE_OK) {
        return fail_started_worker(
            STAGE_CHILD_SETUP,
            received == RECEIVE_LIVENESS_LOST ? ECANCELED : EPROTO,
            worker_pid,
            worker_identity,
            false);
    }
    if (!read_process_identity(worker_pid, &worker_identity)) {
        return fail_started_worker(
            STAGE_IDENTITY_CAPTURE,
            errno == 0 ? ESRCH : errno,
            worker_pid,
            worker_identity,
            false);
    }
    pid_t inherited_group = getpgrp();
    if (getpgid(worker_pid) != inherited_group ||
        !send_child_event(
            EVENT_CHILD_GATED,
            broker_pid,
            broker_identity,
            worker_pid,
            worker_identity,
            false)) {
        return fail_started_worker(
            STAGE_IDENTITY_CAPTURE,
            errno == 0 ? EPROTO : errno,
            worker_pid,
            worker_identity,
            false);
    }

    received = receive_command(&command);
    if (received != RECEIVE_OK || command != COMMAND_ARM_GROUP) {
        return fail_started_worker(
            STAGE_GROUP_ARM,
            received == RECEIVE_LIVENESS_LOST ? ECANCELED : EPROTO,
            worker_pid,
            worker_identity,
            false);
    }
    if (!identity_is_live(worker_pid, worker_identity) ||
        (setpgid(worker_pid, worker_pid) != 0 && errno != EACCES)) {
        return fail_started_worker(
            STAGE_GROUP_ARM,
            errno == 0 ? ESRCH : errno,
            worker_pid,
            worker_identity,
            false);
    }
    if (getpgid(worker_pid) != worker_pid ||
        !identity_is_live(worker_pid, worker_identity) ||
        !send_child_event(
            EVENT_ARMED,
            broker_pid,
            broker_identity,
            worker_pid,
            worker_identity,
            true)) {
        return fail_started_worker(
            STAGE_GROUP_VALIDATE,
            errno == 0 ? EPROTO : errno,
            worker_pid,
            worker_identity,
            true);
    }

    received = receive_command(&command);
    if (received != RECEIVE_OK || command != COMMAND_RELEASE) {
        return fail_started_worker(
            STAGE_GATE_RELEASE,
            received == RECEIVE_LIVENESS_LOST ? ECANCELED : EPROTO,
            worker_pid,
            worker_identity,
            true);
    }
    const uint8_t release_value = UINT8_C(1);
    if (!write_full(release[1], &release_value, sizeof(release_value))) {
        int failure = errno;
        close_quietly(release[1]);
        return fail_started_worker(
            STAGE_GATE_RELEASE,
            failure,
            worker_pid,
            worker_identity,
            true);
    }
    close_quietly(release[1]);

    int child_exec_error = 0;
    received = wait_for_exec_result(exec_error[0], &child_exec_error);
    close_quietly(exec_error[0]);
    if (received != RECEIVE_EOF) {
        return fail_started_worker(
            STAGE_EXEC,
            received == RECEIVE_OK
                ? child_exec_error
                : (received == RECEIVE_LIVENESS_LOST ? ECANCELED : EPROTO),
            worker_pid,
            worker_identity,
            true);
    }
    if (!send_event(EVENT_RELEASED, NULL, 0U)) {
        return contain_worker(worker_pid, worker_identity, true) ? 64 : 74;
    }
    return monitor_worker(worker_pid, worker_identity);
}

int main(int argc, char **argv)
{
    if (argc < 4 ||
        strcmp(argv[1], "--broker-v2") != 0 ||
        argv[2][0] != '/' ||
        argv[3][0] != '/') {
        return 64;
    }
    return broker_main(argv[2], &argv[3]);
}
