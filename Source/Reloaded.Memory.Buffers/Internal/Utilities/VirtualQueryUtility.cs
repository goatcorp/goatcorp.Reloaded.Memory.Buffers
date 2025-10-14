using System;
using System.Runtime.InteropServices;
using System.Security;
using static Reloaded.Memory.Buffers.Internal.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Internal.Utilities
{
    /// <summary/>
    internal static unsafe class VirtualQueryUtility
    {
        /* Custom Kernel32 DLLImport statements for performance uptick. */
        
        [DllImport("kernel32.dll", SetLastError = true), SuppressUnmanagedCodeSecurity]
        private static extern UIntPtr VirtualQuery(nuint lpAddress, ref MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        public static nuint VirtualQueryLocal(nuint address, ref MEMORY_BASIC_INFORMATION memoryInformation)
        {
            return VirtualQuery(address, ref memoryInformation, (UIntPtr) sizeof(MEMORY_BASIC_INFORMATION));
        }
    }
}
