using System;
using System.Diagnostics;
using System.Threading;
using Reloaded.Memory.Buffers.Tests.Helpers;
using Reloaded.Memory.Sources;
using Xunit;
using static Reloaded.Memory.Buffers.Internal.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers.Tests
{
    public class MemoryBufferTests : IDisposable
    {
        private MemoryBufferHelper _bufferHelper;
        public MemoryBufferTests()
        {
            _bufferHelper = new MemoryBufferHelper(Process.GetCurrentProcess());
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// Tests if a <see cref="MemoryBuffer"/> can be successfully created.
        /// </summary>
        [Fact]
        public void CreateBufferInternal() => CreateBufferBase(_bufferHelper);

        /* Same as above, except without cache. */

        /// <summary>
        /// Tests the "Add" functionality of the <see cref="MemoryBuffer"/>; including
        /// the return of the correct pointer and CanItemFit.
        /// </summary>
        [Fact]
        private void MemoryBufferAddGenericInternal() => MemoryBufferAddGeneric(CreateMemoryBuffer(_bufferHelper), _bufferHelper.Process);

        /// <summary>
        /// Tests the "Add" functionality of the <see cref="MemoryBuffer"/>, with raw data;
        /// including the return of the correct pointer and CanItemFit.
        /// </summary>
        [Fact]
        private unsafe void MemoryBufferAddByteArrayInternal() => MemoryBufferAddByteArray(CreateMemoryBuffer(_bufferHelper), _bufferHelper.Process);


        /*
         * ----------
         * Core Tests
         * ----------
        */

        [Fact]
        private void AllocateFree()
        {
            for (int x = 0; x < 20; x++)
            {
                // Commit
                var buf = _bufferHelper.Allocate(4096);

                // Write something to start of buffers to test allocation.
                var bufMem = new Sources.Memory();

                bufMem.Write(buf.MemoryAddress, 5);

                // Release
                _bufferHelper.Free(buf.MemoryAddress);
            }
        }

        [Fact]
        private void AllocateConcurrent()
        {
            int numThreads = 100;
            var threads = new Thread[numThreads];

            for (int x = 0; x < numThreads; x++)
            {
                threads[x] = new Thread(AllocateFree); 
                threads[x].Start();
            }

            foreach (var thread in threads)
                thread.Join();
        }

        /// <summary>
        /// [Testing Purposes]
        /// Creates a buffer, then frees the memory belonging to the buffer.
        /// </summary>
        private void CreateBufferBase(MemoryBufferHelper bufferHelper)
        {
            var buffer = bufferHelper.CreateMemoryBuffer(4096);

            // Cleanup
            Internal.Testing.Buffers.FreeBuffer(buffer);
        }

        /// <summary>
        /// Tests the "Add" functionality of the <see cref="MemoryBuffer"/>; including
        /// the return of the correct pointer and CanItemFit.
        /// </summary>
        private unsafe void MemoryBufferAddGeneric(MemoryBuffer buffer, Process process)
        {
            // Setup test.
            ExternalMemory externalMemory = new ExternalMemory(process);

            // Disable item alignment.
            var bufferHeader = buffer.Properties;
            buffer.Properties = bufferHeader;

            // Get remaining space, items to place.
            int remainingBufferSpace    = bufferHeader.Remaining;
            int structSize              = Struct.GetSize<RandomIntStruct>();
            int itemsToFit              = remainingBufferSpace / structSize;

            // Generate array of random int structs.
            RandomIntStruct[] randomIntStructs = new RandomIntStruct[itemsToFit];

            for (int x = 0; x < itemsToFit; x++)
                randomIntStructs[x] = RandomIntStruct.BuildRandomStruct();

            // Fill the buffer and verify each item as it's added.
            for (int x = 0; x < itemsToFit; x++)
            {
                nuint writeAddress = buffer.Add(ref randomIntStructs[x], false, 1);

                // Read back and compare.
                externalMemory.Read(writeAddress, out RandomIntStruct actual);
                Assert.Equal(randomIntStructs[x], actual);
            }

            // Compare again, running the entire array this time.
            nuint bufferStartPtr = bufferHeader.DataPointer;
            for (int x = 0; x < itemsToFit; x++)
            {
                nuint readAddress = bufferStartPtr + (nuint)(x * structSize);

                // Read back and compare.
                externalMemory.Read(readAddress, out RandomIntStruct actual);
                Assert.Equal(randomIntStructs[x], actual);
            }

            // The array is full, calling CanItemFit should return false.
            Assert.False(buffer.CanItemFit(ref randomIntStructs[0]));

            // Likewise, calling Add should return IntPtr.Zero.
            var randIntStr = RandomIntStruct.BuildRandomStruct();
            Assert.Equal((nuint)0, buffer.Add(ref randIntStr, false, 1));
        }

        /// <summary>
        /// Tests the "Add" functionality of the <see cref="MemoryBuffer"/>, with raw data;
        /// including the return of the correct pointer and CanItemFit.
        /// </summary>
        private unsafe void MemoryBufferAddByteArray(MemoryBuffer buffer, Process process)
        {
            // Setup test.
            ExternalMemory externalMemory = new ExternalMemory(process);

            // Disable item alignment.
            var bufferHeader = buffer.Properties;
            buffer.Properties = bufferHeader;

            // Get remaining space, items to place.
            int remainingBufferSpace = bufferHeader.Remaining;
            var randomByteArray      = RandomByteArray.GenerateRandomByteArray(remainingBufferSpace);
            byte[] rawArray          = randomByteArray.Array;

            // Fill the buffer with the whole array.
            buffer.Add(rawArray, 1);

            // Compare against the array written.
            nuint bufferStartPtr = bufferHeader.DataPointer;
            for (int x = 0; x < remainingBufferSpace; x++)
            {
                nuint readAddress = bufferStartPtr + (nuint)x;

                // Read back and compare.
                externalMemory.Read(readAddress, out byte actual);
                Assert.Equal(rawArray[x], actual);
            }

            // The array is full, calling CanItemFit should return false.
            Assert.False(buffer.CanItemFit(sizeof(byte)));

            // Likewise, calling Add should return IntPtr.Zero.
            byte testByte = 55;
            Assert.Equal((nuint)0, buffer.Add(ref testByte, false, 1));
        }

        /*
         * ---------------
         * Utility Methods
         * ---------------
        */

        private MemoryBuffer CreateMemoryBuffer(MemoryBufferHelper helper)
        {
            return helper.CreateMemoryBuffer(4096);
        }

        /// <summary>
        /// Asserts whether the contents of a given <see cref="MemoryBuffer"/> lie in the <see cref="minAddress"/> to <see cref="maxAddress"/> address range.
        /// </summary>
        private unsafe void AssertBufferInRange(MemoryBuffer buffer, nuint minAddress, nuint maxAddress)
        {
            nuint bufferDataPtr = buffer.Properties.DataPointer;
            if ((void*)bufferDataPtr < (void*)minAddress ||
                (void*)bufferDataPtr > (void*)maxAddress)
            {
                Assert.True(false, $"The newly allocated MemoryBuffer should lie in the {minAddress.ToString("X")} to {maxAddress.ToString("X")} range.");
            }
        }

        /// <summary>
        /// Returns the max addressable address of the process sitting behind the <see cref="MemoryBufferHelper"/>.
        /// </summary>
        private UIntPtr GetMaxAddress(MemoryBufferHelper helper, bool largeAddressAware = false)
        {
            // Is this Windows on Windows 64? (x86 app running on x64 Windows)
            IsWow64Process(helper.Process.Handle, out bool isWow64);
            GetSystemInfo(out SYSTEM_INFO systemInfo);
            long maxAddress = 0x7FFFFFFF;

            // Check for large address aware
            if (largeAddressAware && IntPtr.Size == 4 && (uint)systemInfo.lpMaximumApplicationAddress > maxAddress)
                maxAddress = (uint)systemInfo.lpMaximumApplicationAddress;

            // Check if 64bit.
            if (systemInfo.wProcessorArchitecture == ProcessorArchitecture.PROCESSOR_ARCHITECTURE_AMD64 && !isWow64)
                maxAddress = (long)systemInfo.lpMaximumApplicationAddress;

            return (UIntPtr)maxAddress;
        }
    }
}
