using System;
using System.Runtime.InteropServices;
using System.Security;
using static Reloaded.Memory.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Internal.Utilities
{
    /// <summary/>
    internal static class VirtualAllocUtility
    {
        private const int ERROR_NOT_ENOUGH_MEMORY = 8;    // Out of memory.
        private const int ERROR_OUTOFMEMORY       = 14;   // Not enough storage.
        private const int ERROR_COMMIT_LIMIT      = 1455; // Paging file too small / commit limit reached.

        public static UIntPtr VirtualAllocLocal(nuint address, ulong size)
        {
            var result = VirtualAlloc
            (
                address,
                (UIntPtr) size,
                (uint) (MEM_ALLOCATION_TYPE.MEM_RESERVE | MEM_ALLOCATION_TYPE.MEM_COMMIT),
                (uint) MEM_PROTECTION.PAGE_EXECUTE_READWRITE
            );

            if (result == UIntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_NOT_ENOUGH_MEMORY || error == ERROR_OUTOFMEMORY || error == ERROR_COMMIT_LIMIT)
                    throw new OutOfMemoryException($"VirtualAlloc failed because the system is out of memory (Win32 error {error})");
            }

            return result;
        }
        
        // Duplicated here because we need SetLastError = true
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualAlloc(nuint lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);
    }
}
