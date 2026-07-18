// P/Invoke resolution the ILC emits for marshalled calls. Bare-metal builds bind every P/Invoke
// directly (DirectPInvoke), so the lazy-resolution slow path is never reached and fails fast.

using System;
using System.Runtime.InteropServices;

namespace Internal.Runtime.CompilerHelpers {
    internal static unsafe class InteropHelpers {
        private static IntPtr ResolvePInvoke(MethodFixupCell* pCell)
            => ((pCell->Target != default) ? pCell->Target : ResolvePInvokeSlow(pCell: pCell));
        private static IntPtr ResolvePInvokeSlow(MethodFixupCell* pCell) {
#if WINDOWS
            ModuleFixupCell* pModuleCell = pCell->Module;
            if (pModuleCell->Handle == default) {
                pModuleCell->Handle = LoadLibraryA(pModuleCell->ModuleName);
                if (pModuleCell->Handle == default)
                    Environment.FailFast(null);

                [DllImport("kernel32"), SuppressGCTransition]
                static extern IntPtr LoadLibraryA(IntPtr name);
            }

            pCell->Target = GetProcAddress(pModuleCell->Handle, pCell->MethodName);
            if (pCell->Target == default)
                Environment.FailFast(null);
            return pCell->Target;

            [DllImport("kernel32"), SuppressGCTransition]
            static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr name);
#else
            // No dynamic loader on bare metal; all P/Invokes are direct-bound at link time.
            Environment.FailFast(message: null);
            return default;
#endif
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ModuleFixupCell {
            public IntPtr Handle;
            public IntPtr ModuleName;
            public IntPtr CallingAssemblyType;
            public uint DllImportSearchPathAndCookie;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MethodFixupCell {
            public IntPtr Target;
            public IntPtr MethodName;
            public ModuleFixupCell* Module;

            private int Flags;
        }
    }
}
