using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// An instance's record of its devices' GPU virtual address space, kept so a lost device's fault addresses
/// (<see cref="VulkanDeviceFault"/>) name the objects they fall in, as Direct3D 12's removal record names its
/// allocations. The driver reports every range it binds to or unbinds from an object
/// (<c>VK_EXT_device_address_binding_report</c>) through a debug-utils messenger of the record's own; the record keeps
/// the ranges bound now, a bounded history of the ranges unbound and each object's name
/// (<see cref="VulkanGpuObjectNaming"/>). Reading or writing it takes a lock; the driver reports from whichever thread
/// binds or frees.
/// </summary>
public sealed unsafe class VulkanDeviceAddressBindings : IDisposable {
    /// <summary>The optional device extension.</summary>
    public const string ExtensionName = "VK_EXT_device_address_binding_report";
    /// <summary>The <c>VkPhysicalDeviceAddressBindingReportFeaturesEXT</c> structure type, whose first flag
    /// (<c>reportAddressBinding</c>) turns the reports on.</summary>
    public const uint FeatureStructureType = 1000354000;
    private const uint CallbackStructureType = 1000354001;
    private const uint DebugUtilsMessengerCreateInfoStructureType = 1000128004;
    // VK_DEBUG_UTILS_MESSAGE_TYPE_DEVICE_ADDRESS_BINDING_BIT_EXT, at every severity.
    private const uint MessageType = 0x8u;
    private const uint EverySeverity = 0x1111u;
    // The unbound ranges kept, the latest first displacing the oldest.
    private const int HistoryCapacity = 16384;
    // The bound ranges a report names on either side of an address no range holds.
    private const int Neighbours = 2;

    private readonly Lock m_lock = new();
    private readonly Dictionary<(ulong Handle, ulong Base), Binding> m_bound = [];
    private readonly Binding[] m_unbound = new Binding[HistoryCapacity];
    private readonly Dictionary<ulong, string> m_names = [];
    private readonly VulkanInstanceCommands m_instance;
    private GCHandle m_self;
    private nint m_messenger;
    private int m_unboundNext;
    private int m_unboundCount;
    private long m_sequence;

    private readonly record struct Binding(ulong Base, ulong Size, uint ObjectType, ulong Handle, bool Internal, long Sequence, string? Name);

    private VulkanDeviceAddressBindings(VulkanInstanceCommands instance) {
        m_instance = instance;
    }

    /// <summary>Creates an instance's record and the messenger that feeds it, when the instance can report through a
    /// debug-utils messenger.</summary>
    /// <param name="instance">The command table of the instance whose devices report to the record; it must outlive
    /// the record.</param>
    /// <returns>The record, which the instance owns and disposes before itself, or <see langword="null"/> when the
    /// instance has no <c>VK_EXT_debug_utils</c> or the messenger could not be created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public static VulkanDeviceAddressBindings? Create(VulkanInstanceCommands instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var create = instance.CreateDebugUtilsMessengerExt;

        if (create is null) {
            return null;
        }

        var bindings = new VulkanDeviceAddressBindings(instance: instance);

        bindings.m_self = GCHandle.Alloc(value: bindings);

        var info = new VkDebugUtilsMessengerCreateInfoExt {
            MessageSeverity = EverySeverity,
            MessageType = MessageType,
            StructureType = DebugUtilsMessengerCreateInfoStructureType,
            UserCallback = ((nint)((delegate* unmanaged[Cdecl]<uint, uint, VkDebugUtilsMessengerCallbackDataExt*, void*, uint>)(&OnMessage))),
            UserData = GCHandle.ToIntPtr(value: bindings.m_self),
        };

        if ((create(instance.Handle, in info, 0, out var messenger) != VkResult.Success) || (messenger == 0)) {
            bindings.m_self.Free();
            return null;
        }
        bindings.m_messenger = messenger;
        return bindings;
    }

    /// <summary>Records the name an object was given, which a report prints beside its ranges.</summary>
    /// <param name="handle">The object's native handle.</param>
    /// <param name="name">The name.</param>
    public void Name(ulong handle, string name) {
        lock (m_lock) {
            m_names[handle] = name;
        }
    }

    /// <summary>Returns the lines naming what a faulting address falls in: every range bound now or unbound within the
    /// kept history that meets the reported range, latest first, and, where no bound range meets it, the bound ranges
    /// nearest below and above it.</summary>
    /// <param name="address">The fault's reported address.</param>
    /// <param name="precision">The fault's address precision in bytes, a power of two (zero or one for an exact
    /// address).</param>
    /// <returns>The lines, each starting with two spaces of indent; one line saying so when nothing is known.</returns>
    public string Describe(ulong address, ulong precision) {
        var width = Math.Max(val1: precision, val2: 1UL);
        var low = (address & ~(width - 1UL));
        var high = (low + width);
        var report = new StringBuilder();

        lock (m_lock) {
            var bound = m_bound.Values.Where(predicate: binding => Meets(binding: binding, high: high, low: low)).OrderByDescending(keySelector: static binding => binding.Sequence).ToList();
            var unbound = new List<Binding>();

            for (var index = 0; (index < m_unboundCount); index++) {
                var binding = m_unbound[(((m_unboundNext - 1 - index) + HistoryCapacity) % HistoryCapacity)];

                if (Meets(binding: binding, high: high, low: low)) {
                    unbound.Add(item: binding);
                }
            }

            foreach (var binding in bound) {
                report.Append(handler: $"\n    bound {Describe(binding: binding, names: m_names)}");
            }
            foreach (var binding in unbound) {
                report.Append(handler: $"\n    unbound {(m_sequence - binding.Sequence)} reports ago: {Describe(binding: binding, names: m_names)}");
            }
            if (bound.Count == 0) {
                foreach (var binding in m_bound.Values.Where(predicate: binding => ((binding.Base + binding.Size) <= low)).OrderByDescending(keySelector: static binding => (binding.Base + binding.Size)).Take(count: Neighbours)) {
                    report.Append(handler: $"\n    nearest below, ending 0x{(low - (binding.Base + binding.Size)):x} bytes before: {Describe(binding: binding, names: m_names)}");
                }
                foreach (var binding in m_bound.Values.Where(predicate: binding => (binding.Base >= high)).OrderBy(keySelector: static binding => binding.Base).Take(count: Neighbours)) {
                    report.Append(handler: $"\n    nearest above, starting 0x{(binding.Base - high):x} bytes after: {Describe(binding: binding, names: m_names)}");
                }
            }
            if (report.Length == 0) {
                report.Append(handler: $"\n    no range reported ({m_bound.Count} bound, {m_unboundCount} unbound kept, {m_sequence} reports)");
            }
        }

        return report.ToString();
    }

    /// <summary>Destroys the messenger, after which the record keeps what it holds and receives nothing more.</summary>
    public void Dispose() {
        if (m_messenger == 0) {
            return;
        }

        if (m_instance.DestroyDebugUtilsMessengerExt is not null) {
            m_instance.DestroyDebugUtilsMessengerExt(m_instance.Handle, m_messenger, 0);
        }
        m_messenger = 0;
        m_self.Free();
    }

    private static bool Meets(in Binding binding, ulong low, ulong high) =>
        ((binding.Base < high) && (low < (binding.Base + binding.Size)));
    private static string Describe(in Binding binding, Dictionary<ulong, string> names) {
        var name = (binding.Name ?? names.GetValueOrDefault(key: binding.Handle) ?? "(unnamed)");

        return $"{TypeName(objectType: binding.ObjectType)} 0x{binding.Handle:x} '{name}'{(binding.Internal ? " (driver-internal)" : string.Empty)} [0x{binding.Base:x16}, 0x{(binding.Base + binding.Size):x16}) {binding.Size} bytes, report {binding.Sequence}";
    }
    private static string TypeName(uint objectType) =>
        objectType switch {
            8u => "memory",
            9u => "buffer",
            10u => "image",
            14u => "image view",
            _ => $"object type {objectType}",
        };

    private void Record(in VkDeviceAddressBindingCallbackDataExt binding, uint objectType, ulong handle) {
        lock (m_lock) {
            var sequence = ++m_sequence;
            var key = (handle, binding.BaseAddress);

            if (sequence == 1L) {
                Console.Error.WriteLine(value: $"[device-fault] {ExtensionName} reporting: first range [0x{binding.BaseAddress:x16}, +{binding.Size}) of {TypeName(objectType: objectType)} 0x{handle:x}");
            }

            if (binding.BindingType == 0u) {
                m_bound[key] = new Binding(Base: binding.BaseAddress, Size: binding.Size, ObjectType: objectType, Handle: handle, Internal: ((binding.Flags & 1u) != 0u), Sequence: sequence, Name: null);
                return;
            }

            _ = m_bound.Remove(key: key);
            m_unbound[m_unboundNext] = new Binding(Base: binding.BaseAddress, Size: binding.Size, ObjectType: objectType, Handle: handle, Internal: ((binding.Flags & 1u) != 0u), Sequence: sequence, Name: m_names.GetValueOrDefault(key: handle));
            m_unboundNext = ((m_unboundNext + 1) % HistoryCapacity);
            m_unboundCount = Math.Min(val1: (m_unboundCount + 1), val2: HistoryCapacity);
        }
    }

    // The messenger's callback: finds the binding the message's chain carries and records it against the message's first
    // object. It returns VK_FALSE so the reporting call proceeds, and never throws across the native boundary.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnMessage(uint messageSeverity, uint messageTypes, VkDebugUtilsMessengerCallbackDataExt* callbackData, void* userData) {
        try {
            if ((callbackData is null) || (userData is null) || ((messageTypes & MessageType) == 0u)) {
                return 0;
            }

            var next = callbackData->Next;

            while (next != 0) {
                var header = (VkDeviceAddressBindingCallbackDataExt*)next;

                if (header->StructureType == CallbackStructureType) {
                    var hasObject = ((callbackData->ObjectCount > 0) && (callbackData->Objects is not null));

                    if (GCHandle.FromIntPtr(value: ((nint)userData)).Target is VulkanDeviceAddressBindings bindings) {
                        bindings.Record(binding: in *header, handle: (hasObject ? callbackData->Objects[0].ObjectHandle : 0UL), objectType: (hasObject ? callbackData->Objects[0].ObjectType : 0u));
                    }
                    break;
                }
                next = header->Next;
            }
        } catch {
            // A diagnostic never fails the reporting call.
        }

        return 0;
    }
}
