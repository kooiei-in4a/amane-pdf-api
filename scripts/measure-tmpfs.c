// Validation-only sampler, mounted into the container; never shipped in the image.
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stdio.h>
#include <sys/statvfs.h>
#include <time.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc != 3) return 2;
    uint64_t peak = 0, count = 0;
    const struct timespec interval = {0, 1000000};
    while (access(argv[1], F_OK) != 0) {
        struct statvfs stat;
        if (statvfs("/tmp", &stat) != 0) return 3;
        uint64_t used = (uint64_t)(stat.f_blocks - stat.f_bfree) * stat.f_frsize;
        if (used > peak) peak = used;
        count++;
        nanosleep(&interval, NULL);
    }
    FILE *result = fopen(argv[2], "wx");
    if (!result) return 4;
    fprintf(result, "%llu %llu\n", (unsigned long long)peak, (unsigned long long)count);
    return fclose(result) == 0 ? 0 : 5;
}
