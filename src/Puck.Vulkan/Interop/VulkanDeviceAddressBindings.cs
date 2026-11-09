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
/// the ranges bound now, a bounded history of the ranges unbound and the name of each object that holds a bound range
/// (<see cref="VulkanGpuObjectNaming"/>). An object is identified by its <c>VkObjectType</c> and handle together, since
/// Vulkan does not make non-dispatchable handle values unique across object types, and a name lives exactly as long as
/// the object holds a bound range: the unbind that releases the last one retires it, so a recycled handle never
/// inherits it and the record holds names for live objects only. Reading or writing it takes a lock; the driver
/// reports from whichever thread binds or frees.
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
    private readonly Dictionary<BindingKey, Binding> m_bound = [];
    private readonly Binding[] m_unbound = new Binding[HistoryCapacity];
    // One entry per object that holds at least one bound range, removed with its last one.
    private readonly Dictionary<ObjectKey, LiveObject> m_objects = [];
    // The object types reported so far; the first report of each is announced once on standard error.
    private readonly HashSet<uint> m_typesSeen = [];
    private readonly VulkanInstanceCommands m_instance;
    private GCHandle m_self;
    private nint m_messenger;
    private int m_unboundNext;
    private int m_unboundCount;
    private long m_sequence;

    private readonly record struct ObjectKey(uint ObjectType, ulong Handle);

    private readonly record struct BindingKey(uint ObjectType, ulong Handle, ulong Base);

    private struct LiveObject {
        public string? Name;
        public int Ranges;
    }

    // Name is the object's name at the moment its range was unbound, kept with the history entry; a bound range reads its
    // object's current name instead.
    private readonly record struct Binding(ulong Base, ulong Size, uint ObjectType, ulong Handle, bool Internal, long Sequence, string? Name) {
        // The range's inclusive last address, saturated at the top of the address space so no endpoint wraps; a
        // zero-size range holds no address and never meets anything.
        public ulong Last => ((Size == 0UL) ? Base : ((Size - 1UL) > (ulong.MaxValue - Base)) ? ulong.MaxValue : (Base + (Size - 1UL)));
        public bool IsEmpty => (Size == 0UL);
    }

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

    /// <summary>Records the name an object was given, which a report prints beside its ranges. The name lasts until the
    /// object's last bound range is unbound. An object holding no bound range has nothing a report could print it beside,
    /// so its name is dropped; objects are named after their creation has bound their memory.</summary>
    /// <param name="objectType">The object's <c>VkObjectType</c>.</param>
    /// <param name="handle">The object's native handle.</param>
    /// <param name="name">The name.</param>
    public void Name(uint objectType, ulong handle, string name) {
        lock (m_lock) {
            ref var live = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_objects, key: new ObjectKey(ObjectType: objectType, Handle: handle));

            if (!Unsafe.IsNullRef(ref live)) {
                live.Name = name;
            }
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
        // The fault's possible addresses, both ends inclusive (VkDeviceFaultAddressInfoKHR): the last never wraps, where
        // an exclusive end past the top of the address space would.
        var last = (address | (width - 1UL));
        var report = new StringBuilder();

        lock (m_lock) {
            var bound = m_bound.Values.Where(predicate: binding => Meets(binding: binding, last: last, low: low)).OrderByDescending(keySelector: static binding => binding.Sequence).ToList();
            var unbound = new List<Binding>();

            for (var index = 0; (index < m_unboundCount); index++) {
                var binding = m_unbound[(((m_unboundNext - 1 - index) + HistoryCapacity) % HistoryCapacity)];

                if (Meets(binding: binding, last: last, low: low)) {
                    unbound.Add(item: binding);
                }
            }

            foreach (var binding in bound) {
                report.Append(handler: $"\n    bound {Describe(binding: binding, name: m_objects.GetValueOrDefault(key: new ObjectKey(ObjectType: binding.ObjectType, Handle: binding.Handle)).Name)}");
            }
            foreach (var binding in unbound) {
                report.Append(handler: $"\n    unbound {(m_sequence - binding.Sequence)} reports ago: {Describe(binding: binding, name: binding.Name)}");
            }
            if (bound.Count == 0) {
                foreach (var binding in m_bound.Values.Where(predicate: binding => (!binding.IsEmpty && (binding.Last < low))).OrderByDescending(keySelector: static binding => binding.Last).Take(count: Neighbours)) {
                    report.Append(handler: $"\n    nearest below, ending 0x{(low - binding.Last - 1UL):x} bytes before: {Describe(binding: binding, name: m_objects.GetValueOrDefault(key: new ObjectKey(ObjectType: binding.ObjectType, Handle: binding.Handle)).Name)}");
                }
                foreach (var binding in m_bound.Values.Where(predicate: binding => (!binding.IsEmpty && (binding.Base > last))).OrderBy(keySelector: static binding => binding.Base).Take(count: Neighbours)) {
                    report.Append(handler: $"\n    nearest above, starting 0x{(binding.Base - last - 1UL):x} bytes after: {Describe(binding: binding, name: m_objects.GetValueOrDefault(key: new ObjectKey(ObjectType: binding.ObjectType, Handle: binding.Handle)).Name)}");
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

    // Whether the range holds an address in the inclusive span [low, last].
    private static bool Meets(in Binding binding, ulong low, ulong last) =>
        (!binding.IsEmpty && (binding.Base <= last) && (low <= binding.Last));
    // The range's end is shown exclusive, widened so the top of the address space does not wrap it to zero.
    private static string Describe(in Binding binding, string? name) =>
        $"{TypeName(objectType: binding.ObjectType)} 0x{binding.Handle:x} '{(name ?? "(unnamed)")}'{(binding.Internal ? " (driver-internal)" : string.Empty)} [0x{binding.Base:x16}, 0x{(((UInt128)binding.Base) + binding.Size):x16}) {binding.Size} bytes, report {binding.Sequence}";
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
            var objectKey = new ObjectKey(ObjectType: objectType, Handle: handle);
            var key = new BindingKey(ObjectType: objectType, Handle: handle, Base: binding.BaseAddress);

            if (m_typesSeen.Add(item: objectType)) {
                Console.Error.WriteLine(value: $"[device-fault] {ExtensionName} reporting: first range of a {TypeName(objectType: objectType)}, [0x{binding.BaseAddress:x16}, +{binding.Size}) of 0x{handle:x}");
            }

            if (binding.BindingType == 0u) {
                var bound = new Binding(Base: binding.BaseAddress, Size: binding.Size, ObjectType: objectType, Handle: handle, Internal: ((binding.Flags & 1u) != 0u), Sequence: sequence, Name: null);

                // A range bound again at the same base replaces its entry and holds no second claim on the object.
                if (m_bound.TryAdd(key: key, value: bound)) {
                    CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_objects, key: objectKey, exists: out _).Ranges++;
                } else {
                    m_bound[key] = bound;
                }
                return;
            }

            // The history entry keeps the name the object has now, or none: it never looks a name up again, so a later
            // object that reuses the handle cannot lend its own.
            string? name = null;
            ref var live = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_objects, key: objectKey);

            if (!Unsafe.IsNullRef(ref live)) {
                name = live.Name;
            }
            if (m_bound.Remove(key: key) && !Unsafe.IsNullRef(ref live) && (--live.Ranges <= 0)) {
                _ = m_objects.Remove(key: objectKey);
            }
            m_unbound[m_unboundNext] = new Binding(Base: binding.BaseAddress, Size: binding.Size, ObjectType: objectType, Handle: handle, Internal: ((binding.Flags & 1u) != 0u), Sequence: sequence, Name: name);
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
