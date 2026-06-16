/* Exercises the /dev/dri/renderD128 DRM seam: open the render node, run the DRM_IOCTL_VERSION
 * two-call protocol (query lengths, then fetch the driver name), and mmap a fake BO. The kernel
 * answers "amdgpu" so a real RADV would accept the node. Proves the GPU userspace<->kernel seam
 * end-to-end before RADV exists. Build: musl-gcc -static-pie -Os -s. */
#include <stdio.h>
#include <string.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <dirent.h>

struct drm_version
{
    int version_major, version_minor, version_patchlevel;
    unsigned long name_len; char *name;
    unsigned long date_len; char *date;
    unsigned long desc_len; char *desc;
};
#define DRM_IOCTL_VERSION 0xC0406400UL /* _IOWR('d', 0x00, struct drm_version) */

struct drm_amdgpu_info
{
    unsigned long long return_pointer;
    unsigned int return_size, query, u[4];
};
#define DRM_IOCTL_AMDGPU_INFO 0xC0206445UL /* _IOWR('d', 0x45, struct drm_amdgpu_info) */
#define AMDGPU_INFO_DEV_INFO  0x16

int main(void)
{
    int fd;
    struct drm_version v;
    char namebuf[64];
    void *bo;

    fd = open("/dev/dri/renderD128", O_RDWR);
    if (fd < 0) { printf("open /dev/dri/renderD128 FAILED\n"); return 1; }
    printf("opened /dev/dri/renderD128 (fd=%d)\n", fd);

    memset(&v, 0, sizeof v);
    if (ioctl(fd, DRM_IOCTL_VERSION, &v) != 0) { printf("DRM_VERSION (lengths) FAILED\n"); return 1; }
    printf("DRM version %d.%d.%d, name_len=%lu\n",
           v.version_major, v.version_minor, v.version_patchlevel, v.name_len);

    memset(namebuf, 0, sizeof namebuf);
    if (v.name_len > 63) v.name_len = 63;
    v.name = namebuf;
    if (ioctl(fd, DRM_IOCTL_VERSION, &v) != 0) { printf("DRM_VERSION (strings) FAILED\n"); return 1; }
    namebuf[v.name_len] = 0;
    printf("DRM driver name = \"%s\"\n", namebuf);
    if (strcmp(namebuf, "amdgpu") == 0) printf("  -> a real RADV would accept this render node\n");

    {
        unsigned char devinfo[320];
        struct drm_amdgpu_info req;
        memset(devinfo, 0, sizeof devinfo);
        memset(&req, 0, sizeof req);
        req.return_pointer = (unsigned long long)(unsigned long)devinfo;
        req.return_size = sizeof devinfo;
        req.query = AMDGPU_INFO_DEV_INFO;
        if (ioctl(fd, DRM_IOCTL_AMDGPU_INFO, &req) != 0) { printf("AMDGPU_INFO DEV_INFO FAILED\n"); return 1; }
        unsigned int device_id = *(unsigned int *)(devinfo + 0);
        unsigned int family = *(unsigned int *)(devinfo + 16);
        unsigned int cu = *(unsigned int *)(devinfo + 48);
        printf("AMDGPU_INFO DEV_INFO: device_id=0x%04x family=0x%02x cu_active=%u\n", device_id, family, cu);
        if (device_id == 0x163F && family == 0x8B)
            printf("  -> Van Gogh (Steam Deck) identified; RADV would map this to CHIP_VANGOGH\n");
        else
            printf("  -> WRONG device/family (RADV would reject)\n");
    }

    /* The drmGetDevices2 shape: fstat the node -> rdev, enumerate /dev/dri, resolve the /sys
     * char-dev symlink to the PCI device, and read its vendor/device ids. */
    {
        struct stat st;
        DIR *dir;
        struct dirent *de;
        char lnk[128];
        ssize_t ln;
        FILE *vf;
        char vb[16];

        if (fstat(fd, &st) == 0)
            printf("fstat render node: ischr=%d rdev=%u:%u\n",
                   S_ISCHR(st.st_mode), (unsigned)major(st.st_rdev), (unsigned)minor(st.st_rdev));
        else
            printf("fstat FAILED\n");

        dir = opendir("/dev/dri");
        if (dir)
        {
            printf("/dev/dri:");
            while ((de = readdir(dir)) != NULL) printf(" %s", de->d_name);
            printf("\n");
            closedir(dir);
        }
        else printf("opendir /dev/dri FAILED\n");

        ln = readlink("/sys/dev/char/226:128", lnk, sizeof(lnk) - 1);
        if (ln > 0) { lnk[ln] = 0; printf("readlink /sys/dev/char/226:128 -> %s\n", lnk); }
        else printf("readlink FAILED\n");

        vf = fopen("/sys/devices/pci0000:00/0000:00:01.0/vendor", "r");
        if (vf) { memset(vb, 0, sizeof vb); fread(vb, 1, sizeof(vb) - 1, vf); fclose(vf);
                  printf("PCI vendor = %s", vb); }
        vf = fopen("/sys/devices/pci0000:00/0000:00:01.0/device", "r");
        if (vf) { memset(vb, 0, sizeof vb); fread(vb, 1, sizeof(vb) - 1, vf); fclose(vf);
                  printf("PCI device = %s", vb); }
    }

    bo = mmap(0, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (bo == MAP_FAILED) { printf("mmap BO FAILED\n"); return 1; }
    ((volatile unsigned char *)bo)[0] = 0xAB;
    printf("mmap'd a 4KiB BO at %p, wrote+read 0x%02x\n", bo, ((volatile unsigned char *)bo)[0]);

    printf("DRM seam OK\n");
    close(fd);
    return 0;
}
