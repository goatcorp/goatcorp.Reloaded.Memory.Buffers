using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Memory.Buffers.Internal.Utilities;
using Reloaded.Memory.Sources;

namespace Reloaded.Memory.Buffers.Internal
{
    /// <summary>
    /// Creates <see cref="MemoryBuffer"/>s in unmanaged memory and returns a new instance to the user.
    /// </summary>
    internal static class MemoryBufferFactory
    {
        private static IMemory _memSrc = new Sources.Memory();

        /// <summary>
        /// Creates a new buffer in a specified location in memory with a specified size.
        /// </summary>
        /// <param name="process">The process inside which the <see cref="MemoryBuffer"/> will be allocated.</param>
        /// <param name="bufferAddress">Base address of the new buffer to be created. </param>
        /// <param name="allocationSize">The amount of bytes allocated at bufferAddress</param>
        /// <param name="allocateMemory">Set this to false if the allocationSize bytes have already been preallocated at bufferAddress.</param>
        /// <remarks>
        /// This constructor will override any existing buffer!
        /// </remarks>
        internal static MemoryBuffer CreateBuffer(Process process, nuint bufferAddress, int allocationSize, bool allocateMemory = true)
        {
            if (allocateMemory)
                AllocateBuffer(process, bufferAddress, allocationSize);

            // Setup buffer after Magic.
            var dataPtr = (UIntPtr)bufferAddress;
            var realBufSize = allocationSize;
            var memoryBufferProperties = new MemoryBufferProperties(dataPtr, realBufSize);

            var buffer = new MemoryBuffer(_memSrc, bufferAddress, memoryBufferProperties);
            return buffer;
        }

        /// <summary>
        /// Allocates memory to store a <see cref="MemoryBuffer"/>s inside the target process/
        /// </summary>
        internal static void AllocateBuffer(Process process, nuint bufferAddress, int bufferSize)
        {
            // Get the function, commit the pages and check.
            var virtualAllocFunction = VirtualAllocUtility.GetVirtualAllocFunction(process);
            var address = virtualAllocFunction(process.Handle, bufferAddress, (uint)bufferSize);

            if (address == UIntPtr.Zero)
                throw new Exception($"Failed to allocate MemoryBuffer of size {bufferSize} at address {bufferAddress}. Last Win32 Error: {Marshal.GetLastWin32Error()}");
        }
    }
}
