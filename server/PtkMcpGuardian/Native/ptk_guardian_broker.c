#if defined(__APPLE__)
#define _DARWIN_C_SOURCE 1
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
#include <sys/proc.h>
#endif

#define PTK_TERM_TO_KILL_MILLISECONDS 2000
#define PTK_CONTAINMENT_DEADLINE_MILLISECONDS 10000
#define PTK_IDENTITY_POLL_MILLISECONDS 25
#define PTK_EVENT_MAGIC UINT32_C(0x50544b42)
#define PTK_PROTOCOL_VERSION UINT32_C(2)
#define PTK_EVENT_BYTES 48U
#define PTK_COMMAND_BYTES 80U
#define PTK_MAXIMUM_WORKER_GROUPS 128

enum event_kind {
    EVENT_READY = 1,
    EVENT_HOST_EXITED = 2,
    EVENT_CONTAINMENT_CONFIRMED = 3,
    EVENT_CONTAINMENT_FAILED = 4,
    EVENT_REGISTRY_PENDING = 5,
    EVENT_REGISTRY_ARMED = 6,
    EVENT_REGISTRY_REMOVED = 7,
    EVENT_REGISTRY_REJECTED = 8
};

enum command_kind {
    COMMAND_START = 1,
    COMMAND_STOP = 2,
    COMMAND_REGISTER_PENDING = 3,
    COMMAND_REGISTER_ARMED = 4,
    COMMAND_REMOVE = 5
};

enum receive_result {
    RECEIVE_LIVENESS_LOST = -2,
    RECEIVE_INVALID = -1,
    RECEIVE_EOF = 0,
    RECEIVE_OK = 1
};

enum registry_state {
    REGISTRY_EMPTY = 0,
    REGISTRY_PENDING = 1,
    REGISTRY_ARMED = 2
};

struct process_identity {
    uint64_t high;
    uint64_t low;
};

struct broker_command {
    enum command_kind kind;
    uint64_t request_id;
    pid_t worker_broker_pid;
    pid_t worker_pid;
    pid_t process_group;
    struct process_identity worker_broker_identity;
    struct process_identity worker_identity;
};

struct registry_entry {
    enum registry_state state;
    pid_t worker_broker_pid;
    pid_t worker_pid;
    pid_t process_group;
    struct process_identity worker_broker_identity;
    struct process_identity worker_identity;
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

static void consume_liveness_event(int descriptor)
{
    uint8_t unexpected = 0U;
    ssize_t received;
    do {
        received = read(descriptor, &unexpected, sizeof(unexpected));
    } while (received < 0 && errno == EINTR);
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

static void encode_u64(uint8_t *destination, uint64_t value)
{
    for (size_t index = 0U; index < 8U; ++index) {
        destination[index] =
            (uint8_t)(value >> (56U - (unsigned int)(index * 8U)));
    }
}

static uint64_t decode_u64(const uint8_t *source)
{
    uint64_t value = UINT64_C(0);
    for (size_t index = 0U; index < 8U; ++index) {
        value = (value << 8U) | (uint64_t)source[index];
    }
    return value;
}

static bool send_event(
    int descriptor,
    enum event_kind kind,
    pid_t host_pid,
    pid_t broker_pid,
    uint32_t value,
    uint64_t request_id)
{
    uint8_t frame[PTK_EVENT_BYTES] = {0};
    encode_u32(frame, PTK_EVENT_MAGIC);
    encode_u32(frame + 4U, PTK_PROTOCOL_VERSION);
    encode_u32(frame + 8U, (uint32_t)kind);
    encode_u32(frame + 16U, (uint32_t)host_pid);
    encode_u32(frame + 20U, (uint32_t)broker_pid);
    encode_u32(frame + 24U, value);
    encode_u64(frame + 32U, request_id);
    return write_full(descriptor, frame, sizeof(frame));
}

static bool identity_is_valid(struct process_identity identity)
{
    return identity.high != UINT64_C(0) || identity.low != UINT64_C(0);
}

static bool command_has_empty_registry(const struct broker_command *command)
{
    return command->request_id == UINT64_C(0) &&
        command->worker_broker_pid == 0 &&
        command->worker_pid == 0 &&
        command->process_group == 0 &&
        !identity_is_valid(command->worker_broker_identity) &&
        !identity_is_valid(command->worker_identity);
}

static enum receive_result read_command_bytes(
    int command_read,
    int liveness_read,
    uint8_t *buffer,
    size_t length)
{
    size_t offset = 0U;
    while (offset < length) {
        struct pollfd descriptors[2];
        descriptors[0].fd = command_read;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = liveness_read;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int result;
        do {
            result = poll(descriptors, 2U, -1);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return RECEIVE_INVALID;
        }
        if ((descriptors[1].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            consume_liveness_event(liveness_read);
            return RECEIVE_LIVENESS_LOST;
        }
        if ((descriptors[0].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) == 0) {
            continue;
        }
        ssize_t received = read(
            command_read,
            buffer + offset,
            length - offset);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received < 0) {
            return RECEIVE_INVALID;
        }
        if (received == 0) {
            return offset == 0U ? RECEIVE_EOF : RECEIVE_INVALID;
        }
        offset += (size_t)received;
    }
    return RECEIVE_OK;
}

static enum receive_result receive_command(
    int command_read,
    int liveness_read,
    struct broker_command *command)
{
    uint8_t frame[PTK_COMMAND_BYTES];
    enum receive_result result = read_command_bytes(
        command_read,
        liveness_read,
        frame,
        sizeof(frame));
    if (result != RECEIVE_OK) {
        return result;
    }
    if (decode_u32(frame) != PTK_EVENT_MAGIC ||
        decode_u32(frame + 4U) != PTK_PROTOCOL_VERSION ||
        decode_u32(frame + 12U) != 0U ||
        decode_u32(frame + 36U) != 0U ||
        decode_u64(frame + 72U) != UINT64_C(0)) {
        return RECEIVE_INVALID;
    }

    uint32_t raw_kind = decode_u32(frame + 8U);
    if (raw_kind < (uint32_t)COMMAND_START ||
        raw_kind > (uint32_t)COMMAND_REMOVE) {
        return RECEIVE_INVALID;
    }
    uint32_t raw_broker_pid = decode_u32(frame + 24U);
    uint32_t raw_worker_pid = decode_u32(frame + 28U);
    uint32_t raw_group = decode_u32(frame + 32U);
    if (raw_broker_pid > (uint32_t)INT32_MAX ||
        raw_worker_pid > (uint32_t)INT32_MAX ||
        raw_group > (uint32_t)INT32_MAX) {
        return RECEIVE_INVALID;
    }

    command->kind = (enum command_kind)raw_kind;
    command->request_id = decode_u64(frame + 16U);
    command->worker_broker_pid = (pid_t)raw_broker_pid;
    command->worker_pid = (pid_t)raw_worker_pid;
    command->process_group = (pid_t)raw_group;
    command->worker_broker_identity.high = decode_u64(frame + 40U);
    command->worker_broker_identity.low = decode_u64(frame + 48U);
    command->worker_identity.high = decode_u64(frame + 56U);
    command->worker_identity.low = decode_u64(frame + 64U);

    if (command->kind == COMMAND_START || command->kind == COMMAND_STOP) {
        return command_has_empty_registry(command)
            ? RECEIVE_OK
            : RECEIVE_INVALID;
    }
    if (command->request_id == UINT64_C(0) ||
        command->worker_broker_pid <= 0 ||
        command->worker_pid <= 0 ||
        command->worker_broker_pid == command->worker_pid ||
        command->process_group != command->worker_pid ||
        !identity_is_valid(command->worker_broker_identity) ||
        !identity_is_valid(command->worker_identity)) {
        return RECEIVE_INVALID;
    }
    return RECEIVE_OK;
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

static int parse_descriptor(const char *value)
{
    char *end = NULL;
    errno = 0;
    long parsed = strtol(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' ||
        parsed < 3 || parsed > INT32_MAX) {
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

static bool identities_equal(
    struct process_identity left,
    struct process_identity right)
{
    return left.high == right.high && left.low == right.low;
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
#error "PtkGuardianBroker supports only Linux and macOS."
#endif

static bool identity_is_live(
    pid_t process_id,
    struct process_identity expected,
    bool *is_zombie)
{
    struct process_identity actual;
    bool zombie = false;
    if (process_id <= 0 ||
        !read_process_identity(process_id, &actual, &zombie) ||
        !identities_equal(actual, expected)) {
        *is_zombie = false;
        return false;
    }
    *is_zombie = zombie;
    return true;
}

static bool signal_host_group(pid_t host_pid, int signal_number)
{
    if (host_pid <= 0) {
        return false;
    }
    return kill(-host_pid, signal_number) == 0 || errno == ESRCH;
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
    if (signal(SIGTERM, SIG_DFL) == SIG_ERR || setpgid(0, 0) != 0) {
        _exit(71);
    }
    const uint8_t gated = UINT8_C(1);
    if (!write_full(ready_write, &gated, sizeof(gated))) {
        _exit(71);
    }
    close_quietly(ready_write);

    uint8_t release = 0U;
    ssize_t received;
    do {
        received = read(release_read, &release, sizeof(release));
    } while (received < 0 && errno == EINTR);
    close_quietly(release_read);
    if (received != (ssize_t)sizeof(release) || release != UINT8_C(1)) {
        _exit(72);
    }

    int request_copy = fcntl(request_read, F_DUPFD, 5);
    int event_copy = fcntl(event_write, F_DUPFD, 5);
    if (request_copy < 0 || event_copy < 0 ||
        dup2(request_copy, 3) < 0 ||
        dup2(event_copy, 4) < 0) {
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
    descriptors[0].revents = 0;
    descriptors[1].fd = liveness_read;
    descriptors[1].events = POLLIN | POLLHUP;
    descriptors[1].revents = 0;
    for (;;) {
        int result;
        do {
            result = poll(descriptors, 2U, -1);
        } while (result < 0 && errno == EINTR);
        if (result < 0) {
            return false;
        }
        if ((descriptors[1].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            consume_liveness_event(liveness_read);
            return false;
        }
        if ((descriptors[0].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            uint8_t gated = 0U;
            ssize_t received;
            do {
                received = read(ready_read, &gated, sizeof(gated));
            } while (received < 0 && errno == EINTR);
            return received == (ssize_t)sizeof(gated) &&
                gated == UINT8_C(1);
        }
    }
}

static struct registry_entry *find_registry_entry(
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    const struct broker_command *command)
{
    for (size_t index = 0U; index < PTK_MAXIMUM_WORKER_GROUPS; ++index) {
        struct registry_entry *entry = &entries[index];
        if (entry->state != REGISTRY_EMPTY &&
            entry->worker_broker_pid == command->worker_broker_pid &&
            entry->worker_pid == command->worker_pid &&
            entry->process_group == command->process_group &&
            identities_equal(
                entry->worker_broker_identity,
                command->worker_broker_identity) &&
            identities_equal(
                entry->worker_identity,
                command->worker_identity)) {
            return entry;
        }
    }
    return NULL;
}

static bool registry_identity_conflicts(
    const struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    const struct broker_command *command)
{
    for (size_t index = 0U; index < PTK_MAXIMUM_WORKER_GROUPS; ++index) {
        const struct registry_entry *entry = &entries[index];
        if (entry->state != REGISTRY_EMPTY &&
            (entry->worker_broker_pid == command->worker_broker_pid ||
             entry->worker_pid == command->worker_pid ||
             entry->process_group == command->process_group)) {
            return true;
        }
    }
    return false;
}

static bool register_pending(
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    pid_t host_pid,
    const struct broker_command *command)
{
    if (registry_identity_conflicts(entries, command) ||
        group_exists(command->process_group)) {
        return false;
    }
    bool broker_zombie = false;
    bool worker_zombie = false;
    if (!identity_is_live(
            command->worker_broker_pid,
            command->worker_broker_identity,
            &broker_zombie) ||
        broker_zombie ||
        !identity_is_live(
            command->worker_pid,
            command->worker_identity,
            &worker_zombie) ||
        worker_zombie ||
        getpgid(command->worker_broker_pid) != host_pid ||
        getpgid(command->worker_pid) != host_pid) {
        return false;
    }
    for (size_t index = 0U; index < PTK_MAXIMUM_WORKER_GROUPS; ++index) {
        if (entries[index].state == REGISTRY_EMPTY) {
            entries[index].state = REGISTRY_PENDING;
            entries[index].worker_broker_pid =
                command->worker_broker_pid;
            entries[index].worker_pid = command->worker_pid;
            entries[index].process_group = command->process_group;
            entries[index].worker_broker_identity =
                command->worker_broker_identity;
            entries[index].worker_identity = command->worker_identity;
            return true;
        }
    }
    return false;
}

static bool register_armed(
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    pid_t host_pid,
    const struct broker_command *command)
{
    struct registry_entry *entry = find_registry_entry(entries, command);
    bool broker_zombie = false;
    bool worker_zombie = false;
    if (entry == NULL || entry->state != REGISTRY_PENDING ||
        !identity_is_live(
            entry->worker_broker_pid,
            entry->worker_broker_identity,
            &broker_zombie) ||
        broker_zombie ||
        !identity_is_live(
            entry->worker_pid,
            entry->worker_identity,
            &worker_zombie) ||
        worker_zombie ||
        getpgid(entry->worker_broker_pid) != host_pid ||
        getpgid(entry->worker_pid) != entry->process_group) {
        return false;
    }
    entry->state = REGISTRY_ARMED;
    return true;
}

static bool remove_registry_entry(
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    const struct broker_command *command)
{
    struct registry_entry *entry = find_registry_entry(entries, command);
    bool zombie = false;
    if (entry == NULL ||
        identity_is_live(
            entry->worker_broker_pid,
            entry->worker_broker_identity,
            &zombie) ||
        identity_is_live(
            entry->worker_pid,
            entry->worker_identity,
            &zombie) ||
        group_exists(entry->process_group)) {
        return false;
    }
    (void)memset(entry, 0, sizeof(*entry));
    return true;
}

static bool apply_registry_command(
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    pid_t host_pid,
    const struct broker_command *command,
    enum event_kind *acknowledgement)
{
    switch (command->kind) {
        case COMMAND_REGISTER_PENDING:
            *acknowledgement = EVENT_REGISTRY_PENDING;
            return register_pending(entries, host_pid, command);
        case COMMAND_REGISTER_ARMED:
            *acknowledgement = EVENT_REGISTRY_ARMED;
            return register_armed(entries, host_pid, command);
        case COMMAND_REMOVE:
            *acknowledgement = EVENT_REGISTRY_REMOVED;
            return remove_registry_entry(entries, command);
        default:
            return false;
    }
}

static void signal_registered_groups(
    const struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS],
    int signal_number)
{
    for (size_t index = 0U; index < PTK_MAXIMUM_WORKER_GROUPS; ++index) {
        const struct registry_entry *entry = &entries[index];
        if (entry->state == REGISTRY_EMPTY ||
            !group_exists(entry->process_group)) {
            continue;
        }
        bool broker_zombie = false;
        if (!identity_is_live(
                entry->worker_broker_pid,
                entry->worker_broker_identity,
                &broker_zombie) ||
            broker_zombie) {
            continue;
        }
        if (kill(-entry->process_group, signal_number) != 0 &&
            errno != ESRCH) {
            continue;
        }
    }
}

static bool registry_is_gone(
    const struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS])
{
    for (size_t index = 0U; index < PTK_MAXIMUM_WORKER_GROUPS; ++index) {
        const struct registry_entry *entry = &entries[index];
        if (entry->state == REGISTRY_EMPTY) {
            continue;
        }
        bool zombie = false;
        if (identity_is_live(
                entry->worker_broker_pid,
                entry->worker_broker_identity,
                &zombie) ||
            identity_is_live(
                entry->worker_pid,
                entry->worker_identity,
                &zombie) ||
            group_exists(entry->process_group)) {
            return false;
        }
    }
    return true;
}

static int contain_host(
    pid_t host_pid,
    int broker_event_write,
    bool host_reaped,
    bool host_exit_reported,
    const struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS])
{
    uint64_t started = monotonic_milliseconds();
    signal_registered_groups(entries, SIGTERM);
    (void)signal_host_group(host_pid, SIGTERM);
    while (monotonic_milliseconds() - started <
           PTK_TERM_TO_KILL_MILLISECONDS) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            break;
        }
        if (host_reaped && !host_exit_reported) {
            (void)send_event(
                broker_event_write,
                EVENT_HOST_EXITED,
                host_pid,
                getpid(),
                0U,
                UINT64_C(0));
            host_exit_reported = true;
        }
        sleep_milliseconds(PTK_IDENTITY_POLL_MILLISECONDS);
    }

    signal_registered_groups(entries, SIGKILL);
    (void)signal_host_group(host_pid, SIGKILL);
    while (monotonic_milliseconds() - started <=
           PTK_CONTAINMENT_DEADLINE_MILLISECONDS) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            break;
        }
        if (host_reaped && !host_exit_reported) {
            (void)send_event(
                broker_event_write,
                EVENT_HOST_EXITED,
                host_pid,
                getpid(),
                0U,
                UINT64_C(0));
            host_exit_reported = true;
        }
        if (host_reaped &&
            !group_exists(host_pid) &&
            registry_is_gone(entries)) {
            (void)send_event(
                broker_event_write,
                EVENT_CONTAINMENT_CONFIRMED,
                host_pid,
                getpid(),
                0U,
                UINT64_C(0));
            return 0;
        }
        sleep_milliseconds(PTK_IDENTITY_POLL_MILLISECONDS);
    }

    (void)send_event(
        broker_event_write,
        EVENT_CONTAINMENT_FAILED,
        host_pid,
        getpid(),
        0U,
        UINT64_C(0));
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
        signal(SIGTERM, SIG_IGN) == SIG_ERR ||
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
    struct registry_entry entries[PTK_MAXIMUM_WORKER_GROUPS];
    (void)memset(entries, 0, sizeof(entries));

    if ((setpgid(host_pid, host_pid) != 0 && errno != EACCES) ||
        !wait_for_child_gate(child_ready[0], liveness_read)) {
        close_quietly(child_ready[0]);
        close_quietly(child_release[1]);
        return contain_host(
            host_pid,
            broker_event_write,
            host_reaped,
            host_exit_reported,
            entries);
    }
    close_quietly(child_ready[0]);
    if (getpgid(host_pid) != host_pid || getpgrp() == host_pid) {
        close_quietly(child_release[1]);
        return contain_host(
            host_pid,
            broker_event_write,
            host_reaped,
            host_exit_reported,
            entries);
    }

    if (!send_event(
            broker_event_write,
            EVENT_READY,
            host_pid,
            getpid(),
            (uint32_t)host_pid,
            UINT64_C(0))) {
        close_quietly(child_release[1]);
        return contain_host(
            host_pid,
            broker_event_write,
            host_reaped,
            host_exit_reported,
            entries);
    }
    struct broker_command command;
    enum receive_result received = receive_command(
        command_read,
        liveness_read,
        &command);
    if (received != RECEIVE_OK || command.kind != COMMAND_START) {
        close_quietly(child_release[1]);
        return contain_host(
            host_pid,
            broker_event_write,
            host_reaped,
            host_exit_reported,
            entries);
    }

    const uint8_t release = UINT8_C(1);
    if (!write_full(child_release[1], &release, sizeof(release))) {
        close_quietly(child_release[1]);
        return contain_host(
            host_pid,
            broker_event_write,
            host_reaped,
            host_exit_reported,
            entries);
    }
    close_quietly(child_release[1]);

    for (;;) {
        if (!reap_direct_host(host_pid, &host_reaped)) {
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }
        if (host_reaped) {
            if (!host_exit_reported) {
                (void)send_event(
                    broker_event_write,
                    EVENT_HOST_EXITED,
                    host_pid,
                    getpid(),
                    0U,
                    UINT64_C(0));
                host_exit_reported = true;
            }
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }

        struct pollfd descriptors[2];
        descriptors[0].fd = liveness_read;
        descriptors[0].events = POLLIN | POLLHUP;
        descriptors[0].revents = 0;
        descriptors[1].fd = command_read;
        descriptors[1].events = POLLIN | POLLHUP;
        descriptors[1].revents = 0;
        int poll_result;
        do {
            poll_result = poll(
                descriptors,
                2U,
                (int)PTK_IDENTITY_POLL_MILLISECONDS);
        } while (poll_result < 0 && errno == EINTR);
        if (poll_result < 0 ||
            (descriptors[0].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            if (poll_result >= 0) {
                consume_liveness_event(liveness_read);
            }
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }
        if ((descriptors[1].revents &
                (POLLIN | POLLHUP | POLLERR | POLLNVAL)) == 0) {
            continue;
        }

        received = receive_command(command_read, liveness_read, &command);
        if (received != RECEIVE_OK || command.kind == COMMAND_STOP) {
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }
        enum event_kind acknowledgement = EVENT_REGISTRY_REJECTED;
        bool accepted = apply_registry_command(
            entries,
            host_pid,
            &command,
            &acknowledgement);
        if (!accepted) {
            (void)send_event(
                broker_event_write,
                EVENT_REGISTRY_REJECTED,
                host_pid,
                getpid(),
                (uint32_t)command.kind,
                command.request_id);
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }
        if (!send_event(
                broker_event_write,
                acknowledgement,
                host_pid,
                getpid(),
                0U,
                command.request_id)) {
            return contain_host(
                host_pid,
                broker_event_write,
                host_reaped,
                host_exit_reported,
                entries);
        }
    }
}

int main(int argc, char **argv)
{
    if (argc != 9 || strcmp(argv[1], "--broker-v2") != 0) {
        return 64;
    }
    int liveness_read = parse_descriptor(argv[2]);
    int command_read = parse_descriptor(argv[3]);
    int broker_event_write = parse_descriptor(argv[4]);
    int request_read = parse_descriptor(argv[5]);
    int event_write = parse_descriptor(argv[6]);
    if (liveness_read < 0 || command_read < 0 ||
        broker_event_write < 0 || request_read < 0 || event_write < 0 ||
        liveness_read == command_read ||
        liveness_read == broker_event_write ||
        liveness_read == request_read ||
        liveness_read == event_write ||
        command_read == broker_event_write ||
        command_read == request_read ||
        command_read == event_write ||
        broker_event_write == request_read ||
        broker_event_write == event_write ||
        request_read == event_write ||
        !descriptor_is_open(liveness_read) ||
        !descriptor_is_open(command_read) ||
        !descriptor_is_open(broker_event_write) ||
        !descriptor_is_open(request_read) ||
        !descriptor_is_open(event_write) ||
        argv[7][0] != '/' ||
        argv[8][0] != '/') {
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
