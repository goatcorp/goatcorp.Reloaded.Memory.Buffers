using System;
using static Reloaded.Memory.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Internal.Utilities
{
    /// <summary/>
    internal static class VirtualAllocUtility
    {
        public static UIntPtr VirtualAllocLocal(nuint address, ulong size)
        {
            return VirtualAlloc
            (
                address,
                (UIntPtr) size,
                MEM_ALLOCATION_TYPE.MEM_RESERVE | MEM_ALLOCATION_TYPE.MEM_COMMIT,
                MEM_PROTECTION.PAGE_EXECUTE_READWRITE
            );
        }
    }
}
