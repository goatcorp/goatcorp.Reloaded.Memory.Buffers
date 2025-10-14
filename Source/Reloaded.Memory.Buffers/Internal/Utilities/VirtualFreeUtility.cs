using System;
using static Reloaded.Memory.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Internal.Utilities
{
    /// <summary/>
    internal static unsafe class VirtualFreeUtility
    {
        public static void VirtualFreeLocal(nuint address)
        {
            VirtualFree
            (
                address,
                (UIntPtr) 0,
                MEM_ALLOCATION_TYPE.MEM_RELEASE
            );
        }
    }
}
