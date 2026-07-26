using System;
using System.Runtime.InteropServices;
using System.Security;
using static Reloaded.Memory.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Internal.Utilities
{
    internal static class VirtualAllocUtility
    {
        private const int ERROR_NOT_ENOUGH_MEMORY = 8; // "Not enough memory resources are available to process this command."
        private const int ERROR_OUTOFMEMORY = 14; // "Not enough storage is available to complete this operation."
        private const int ERROR_COMMITMENT_LIMIT = 1455; // "The paging file is too small for this operation to complete."

        internal const nuint AllocationGranularity = 65536;

        private const uint MEM_RESERVE_COMMIT = (uint)(MEM_ALLOCATION_TYPE.MEM_RESERVE | MEM_ALLOCATION_TYPE.MEM_COMMIT);
        private const uint PAGE_EXECUTE_READWRITE  = (uint)MEM_PROTECTION.PAGE_EXECUTE_READWRITE;

        // Not readonly so that tests can force the legacy path
        private static bool CanUseVirtualAlloc2 = ProbeVirtualAlloc2();
        
        internal static bool IsVirtualAlloc2Available => CanUseVirtualAlloc2;
        
        public static UIntPtr VirtualAllocLocal(nuint address, ulong size)
        {
            var result = VirtualAlloc
            (
                address,
                (UIntPtr) size,
                MEM_RESERVE_COMMIT,
                PAGE_EXECUTE_READWRITE
            );

            if (result == UIntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_NOT_ENOUGH_MEMORY || error == ERROR_OUTOFMEMORY || error == ERROR_COMMITMENT_LIMIT)
                    throw new OutOfMemoryException($"VirtualAlloc failed because the system is out of memory (Win32 error {error})");
            }

            return result;
        }
        
        public static unsafe UIntPtr VirtualAlloc2Local(nuint minimumAddress, nuint maximumAddress, ulong size)
        {
            if (!CanUseVirtualAlloc2)
                throw new InvalidOperationException("VirtualAlloc2 is not available");

            nuint pageMask = (nuint)Environment.SystemPageSize - 1;
            nuint granMask = AllocationGranularity - 1;
            
            nuint low = (minimumAddress + granMask) & ~granMask;
            if (low < minimumAddress)
                return UIntPtr.Zero;

            if (low < 0x10000)
                low = 0x10000;
            
            if (maximumAddress <= (nuint)Environment.SystemPageSize)
                return UIntPtr.Zero;

            nuint high = (maximumAddress & ~pageMask) - 1;
            if (high <= low)
                return UIntPtr.Zero;

            nuint allocSize = ((nuint)size + pageMask) & ~pageMask;
            if (allocSize < (nuint)size)
                return UIntPtr.Zero;
            
            // Not going to fit
            if (high - low < allocSize - 1) 
                return UIntPtr.Zero;

            var requirements = new MEM_ADDRESS_REQUIREMENTS
            {
                LowestStartingAddress = low,
                HighestEndingAddress  = high,
                Alignment             = 0
            };

            var parameters = new[]
            {
                new MEM_EXTENDED_PARAMETER
                {
                    TypeHeader = MEM_EXTENDED_PARAMETER_TYPE.MemExtendedParameterAddressRequirements,
                    Pointer    = (nint)(&requirements)
                }
            };

            var result = VirtualAlloc2
            (
                -1,
                0, // use requirements parameter
                allocSize,
                MEM_RESERVE_COMMIT,
                PAGE_EXECUTE_READWRITE,
                parameters,
                (uint)parameters.Length
            );

            if (result == UIntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_COMMITMENT_LIMIT)
                    throw new OutOfMemoryException($"VirtualAlloc2 failed because the system is out of memory (Win32 error {error})");
            }

            return result;
        }
        
        private static bool ProbeVirtualAlloc2()
        {
            try
            {
                return NativeLibrary.TryLoad("kernelbase.dll", out var handle) &&
                       NativeLibrary.TryGetExport(handle, nameof(VirtualAlloc2), out _);
            }
            catch
            {
                return false;
            }
        }

        // Both duplicated here because we need SetLastError = true
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualAlloc(nuint lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernelbase.dll", SetLastError = true)]
        private static extern UIntPtr VirtualAlloc2(nint process, nint baseAddress, nuint size, uint allocationType,
            uint pageProtection, [In, Out] MEM_EXTENDED_PARAMETER[] extendedParameters, uint parameterCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEM_ADDRESS_REQUIREMENTS
        {
            public nuint LowestStartingAddress;
            public nuint HighestEndingAddress;
            public nint  Alignment;
        }

        private enum MEM_EXTENDED_PARAMETER_TYPE : ulong
        {
            MemExtendedParameterInvalidType = 0,
            MemExtendedParameterAddressRequirements = 1,
            MemExtendedParameterNumaNode = 2,
            MemExtendedParameterPartitionHandle = 3,
            MemExtendedParameterUserPhysicalHandle = 4,
            MemExtendedParameterAttributeFlags = 5,
            MemExtendedParameterImageMachine = 6,
            MemExtendedParameterMax
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct MEM_EXTENDED_PARAMETER
        {
            [FieldOffset(0)] public MEM_EXTENDED_PARAMETER_TYPE TypeHeader;
            [FieldOffset(8)] public nint Pointer;
        }
    }
}
