/* The Vulkan bring-up guest: links the RADV ICD directly (musl static can't dlopen, and we link
 * dynamically anyway -- see Program.cs Route A) and drives the driver as far as a GPU-less host
 * allows: instance -> enumerate -> logical device -> buffer-object alloc/map -> a real compute
 * pipeline (ACO compiles RDNA2 ISA). Everything short of actual command EXECUTION runs bare-metal;
 * the synthetic Van Gogh node answers the amdgpu ioctls. Each stage prints so the furthest point
 * reached is visible on the serial log.
 *
 * Built on Alpine-musl against the lean RADV build (see radv/build-vktest-musl.sh):
 *   gcc -O2 vktest.c -o vktest /root/mesa-VER/build-radv/src/amd/vulkan/libvulkan_radeon.so
 * Part of Puck.BareMetal. */
#include <vulkan/vulkan.h>
#include <stdio.h>
#include <string.h>

/* The RADV ICD entry point; we call it directly instead of going through the Khronos loader. */
extern PFN_vkVoidFunction vk_icdGetInstanceProcAddr(VkInstance instance, const char *pName);

#define GIPA(inst, name) vk_icdGetInstanceProcAddr((inst), (name))

/* A trivial GLSL compute shader compiled to SPIR-V (no I/O -- enough to drive ACO end to end):
 *   #version 450
 *   layout(local_size_x = 64) in;
 *   void main() {}
 * glslangValidator -V --target-env vulkan1.1. Embedded so the guest needs no filesystem shader. */
static const uint32_t k_comp_spv[] = {
    0x07230203, 0x00010300, 0x0008000a, 0x0000000d, 0x00000000, 0x00020011, 0x00000001, 0x0006000b,
    0x00000001, 0x4c534c47, 0x6474732e, 0x3035342e, 0x00000000, 0x0003000e, 0x00000000, 0x00000001,
    0x0006000f, 0x00000005, 0x00000004, 0x6e69616d, 0x00000000, 0x00000000, 0x00060010, 0x00000004,
    0x00000011, 0x00000040, 0x00000001, 0x00000001, 0x00030003, 0x00000002, 0x000001c2, 0x00040005,
    0x00000004, 0x6e69616d, 0x00000000, 0x00020013, 0x00000002, 0x00030021, 0x00000003, 0x00000002,
    0x00050036, 0x00000002, 0x00000004, 0x00000000, 0x00000003, 0x000200f8, 0x00000005, 0x000100fd,
    0x00010038,
};

int main(void)
{
    PFN_vkCreateInstance create = (PFN_vkCreateInstance)GIPA(NULL, "vkCreateInstance");
    if (!create) { printf("vktest: no vkCreateInstance\n"); return 1; }

    VkApplicationInfo app = { 0 };
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo ci = { 0 };
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo = &app;

    VkInstance inst;
    VkResult r = create(&ci, NULL, &inst);
    printf("vktest: vkCreateInstance -> %d\n", (int)r);
    if (r != VK_SUCCESS) return 1;

    PFN_vkEnumeratePhysicalDevices en = (PFN_vkEnumeratePhysicalDevices)GIPA(inst, "vkEnumeratePhysicalDevices");
    PFN_vkGetPhysicalDeviceProperties props = (PFN_vkGetPhysicalDeviceProperties)GIPA(inst, "vkGetPhysicalDeviceProperties");
    uint32_t n = 0;
    en(inst, &n, NULL);
    printf("vktest: physical devices = %u\n", n);
    if (n == 0) return 1;
    VkPhysicalDevice devs[4];
    if (n > 4) n = 4;
    en(inst, &n, devs);
    VkPhysicalDevice phys = devs[0];
    VkPhysicalDeviceProperties p;
    props(phys, &p);
    printf("vktest:   device 0: %s (vendor 0x%x device 0x%x)\n", p.deviceName, p.vendorID, p.deviceID);
    printf("vktest: VULKAN DRIVER IS RUNNING\n");

    /* --- the rest of the owl: a logical device --- */
    PFN_vkGetPhysicalDeviceQueueFamilyProperties qfp =
        (PFN_vkGetPhysicalDeviceQueueFamilyProperties)GIPA(inst, "vkGetPhysicalDeviceQueueFamilyProperties");
    uint32_t qn = 0;
    qfp(phys, &qn, NULL);
    printf("vktest: queue families = %u\n", qn);
    if (qn == 0) return 1;
    VkQueueFamilyProperties qf[8];
    if (qn > 8) qn = 8;
    qfp(phys, &qn, qf);
    uint32_t qfi = 0;
    for (uint32_t i = 0; i < qn; i++)
        if (qf[i].queueFlags & VK_QUEUE_COMPUTE_BIT) { qfi = i; break; }

    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci = { 0 };
    qci.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qci.queueFamilyIndex = qfi;
    qci.queueCount = 1;
    qci.pQueuePriorities = &prio;
    VkDeviceCreateInfo dci = { 0 };
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;

    PFN_vkCreateDevice createDev = (PFN_vkCreateDevice)GIPA(inst, "vkCreateDevice");
    VkDevice dev;
    r = createDev(phys, &dci, NULL, &dev);
    printf("vktest: vkCreateDevice -> %d\n", (int)r);
    if (r != VK_SUCCESS) return 1;
    printf("vktest: LOGICAL DEVICE CREATED\n");

    PFN_vkGetDeviceProcAddr gdpa = (PFN_vkGetDeviceProcAddr)GIPA(inst, "vkGetDeviceProcAddr");
#define GDPA(name) gdpa(dev, name)

    /* --- a buffer-object: create, query, allocate device memory, bind, map, write --- */
    PFN_vkCreateBuffer createBuf = (PFN_vkCreateBuffer)GDPA("vkCreateBuffer");
    PFN_vkGetBufferMemoryRequirements getReq = (PFN_vkGetBufferMemoryRequirements)GDPA("vkGetBufferMemoryRequirements");
    PFN_vkAllocateMemory allocMem = (PFN_vkAllocateMemory)GDPA("vkAllocateMemory");
    PFN_vkBindBufferMemory bindBuf = (PFN_vkBindBufferMemory)GDPA("vkBindBufferMemory");
    PFN_vkMapMemory mapMem = (PFN_vkMapMemory)GDPA("vkMapMemory");
    PFN_vkGetPhysicalDeviceMemoryProperties memProps =
        (PFN_vkGetPhysicalDeviceMemoryProperties)GIPA(inst, "vkGetPhysicalDeviceMemoryProperties");

    VkBufferCreateInfo bci = { 0 };
    bci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bci.size = 4096;
    bci.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
    bci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    VkBuffer buf;
    r = createBuf(dev, &bci, NULL, &buf);
    printf("vktest: vkCreateBuffer -> %d\n", (int)r);
    if (r == VK_SUCCESS) {
        VkMemoryRequirements req;
        getReq(dev, buf, &req);
        VkPhysicalDeviceMemoryProperties mp;
        memProps(phys, &mp);
        uint32_t mt = 0;
        for (uint32_t i = 0; i < mp.memoryTypeCount; i++)
            if ((req.memoryTypeBits & (1u << i)) &&
                (mp.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)) { mt = i; break; }
        VkMemoryAllocateInfo mai = { 0 };
        mai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        mai.allocationSize = req.size;
        mai.memoryTypeIndex = mt;
        VkDeviceMemory mem;
        r = allocMem(dev, &mai, NULL, &mem);
        printf("vktest: vkAllocateMemory -> %d\n", (int)r);
        if (r == VK_SUCCESS) {
            r = bindBuf(dev, buf, mem, 0);
            void *ptr = NULL;
            if (r == VK_SUCCESS) r = mapMem(dev, mem, 0, req.size, 0, &ptr);
            if (r == VK_SUCCESS && ptr) { memset(ptr, 0xA5, 256); printf("vktest: BUFFER OBJECT MAPPED + WRITTEN\n"); }
        }
    }

    /* --- the crown jewel: a real compute pipeline (ACO compiles RDNA2 ISA bare-metal) --- */
    PFN_vkCreateShaderModule createSM = (PFN_vkCreateShaderModule)GDPA("vkCreateShaderModule");
    PFN_vkCreatePipelineLayout createPL = (PFN_vkCreatePipelineLayout)GDPA("vkCreatePipelineLayout");
    PFN_vkCreateComputePipelines createCP = (PFN_vkCreateComputePipelines)GDPA("vkCreateComputePipelines");

    VkShaderModuleCreateInfo smci = { 0 };
    smci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    smci.codeSize = sizeof(k_comp_spv);
    smci.pCode = k_comp_spv;
    VkShaderModule sm;
    r = createSM(dev, &smci, NULL, &sm);
    printf("vktest: vkCreateShaderModule -> %d\n", (int)r);
    if (r == VK_SUCCESS) {
        VkPipelineLayoutCreateInfo plci = { 0 };
        plci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        VkPipelineLayout pl;
        r = createPL(dev, &plci, NULL, &pl);
        if (r == VK_SUCCESS) {
            VkComputePipelineCreateInfo cpci = { 0 };
            cpci.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
            cpci.stage.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
            cpci.stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
            cpci.stage.module = sm;
            cpci.stage.pName = "main";
            cpci.layout = pl;
            VkPipeline pipe;
            r = createCP(dev, VK_NULL_HANDLE, 1, &cpci, NULL, &pipe);
            printf("vktest: vkCreateComputePipelines -> %d\n", (int)r);
            if (r == VK_SUCCESS) printf("vktest: COMPUTE PIPELINE COMPILED (ACO -> RDNA2 ISA)\n");
        }
    }

    printf("vktest: done.\n");
    return 0;
}
