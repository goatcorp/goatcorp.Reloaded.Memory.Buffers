using System;
using System.Runtime.InteropServices;
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

        [StructLayout(LayoutKind.Sequential)]
        struct MEM_ADDRESS_REQUIREMENTS
        {
            public nuint LowestStartingAddress;
            public nuint HighestEndingAddress;
            public nint Alignment;
        }

        enum MEM_EXTENDED_PARAMETER_TYPE : ulong
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
        struct MEM_EXTENDED_PARAMETER
        {
            [FieldOffset(0)] public MEM_EXTENDED_PARAMETER_TYPE TypeHeader;
            [FieldOffset(8)] public nint Pointer;
            [FieldOffset(8)] public nint Size;
            [FieldOffset(8)] public nint Handle;
            [FieldOffset(8)] public uint ULong;
        }

        [DllImport("kernelbase.dll", SetLastError = true)]
        static extern nuint VirtualAlloc2(
            nint Process,
            nint BaseAddress,
            nuint Size,
            MEM_ALLOCATION_TYPE AllocationType,
            MEM_PROTECTION PageProtection,
            [In, Out] MEM_EXTENDED_PARAMETER[] ExtendedParameters,
            uint ParameterCount
        );

        public static UIntPtr VirtualAlloc2Local(nuint minimumAddress, nuint maximumAddress, ulong size)
        {
            var pageMask = (nuint)Environment.SystemPageSize - 1;

            var requirements = new MEM_ADDRESS_REQUIREMENTS
            {
                LowestStartingAddress = (minimumAddress + pageMask) & ~pageMask,
                HighestEndingAddress = ((maximumAddress + (nuint)size) & ~pageMask) + pageMask,
                Alignment = 0 // system default
            };

            requirements.LowestStartingAddress = (nuint)Math.Max(requirements.LowestStartingAddress, 0x10000);

            unsafe
            {
                var param = new MEM_EXTENDED_PARAMETER
                {
                    TypeHeader = MEM_EXTENDED_PARAMETER_TYPE.MemExtendedParameterAddressRequirements,
                    Pointer = (nint)(&requirements)
                };

                MEM_EXTENDED_PARAMETER[] paramsArray = [param];

                return VirtualAlloc2
                (
                    -1, // current process
                    0,  // let system decide address based on MEM_ADDRESS_REQUIREMENTS
                    ((nuint)size + pageMask) & ~pageMask,
                    MEM_ALLOCATION_TYPE.MEM_RESERVE | MEM_ALLOCATION_TYPE.MEM_COMMIT,
                    MEM_PROTECTION.PAGE_EXECUTE_READWRITE,
                    paramsArray,
                    (uint)paramsArray.Length
                );
            }
        }
    }
}
