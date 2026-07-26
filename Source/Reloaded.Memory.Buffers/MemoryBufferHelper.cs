using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Reloaded.Memory.Buffers.Internal;
using Reloaded.Memory.Buffers.Internal.Structs;
using Reloaded.Memory.Buffers.Internal.Utilities;
using static Reloaded.Memory.Buffers.Internal.Kernel32.Kernel32;
using static Reloaded.Memory.Kernel32.Kernel32;

namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// Provides a way to detect individual Reloaded buffers inside a process used for general small size memory storage,
    /// adding buffer information within certain proximity of an address as well as other various utilities partaining to
    /// buffers.
    /// </summary>
    public class MemoryBufferHelper
    {
        /// <summary> Contains the default size of memory pages to be allocated. </summary>
        internal const int DefaultPageSize = 0x1000;
        
        /// <summary> Implementation of the Searcher that scans and finds existing <see cref="MemoryBuffer"/>s within the current process. </summary>
        private readonly MemoryBufferSearcher _bufferSearcher = new();

        /// <summary> The process on which the MemoryBuffer acts upon. </summary>
        public Process Process { get; private set; }

        /// <summary>
        /// Creates a new <see cref="MemoryBufferHelper"/> for the specified process.
        /// </summary>
        /// <param name="process">The process.</param>
        public MemoryBufferHelper(Process process)
        {
            Process = process;
            if (process.Id != Process.GetCurrentProcess().Id)
                throw new ArgumentException("MemoryBufferHelper only supports the current process.");
        }

        /*
            -----------------------
            Memory Buffer Factories
            -----------------------
        */

        /// <summary>
        /// Finds an appropriate location where a <see cref="MemoryBuffer"/>;
        /// or other memory allocation could be performed.
        /// Note: Please see remarks for this function.
        /// </summary>
        /// <param name = "size" > The space in bytes that the specific <see cref="MemoryBuffer"/> would require to accomodate.</param>
        /// <param name="minimumAddress">The minimum absolute address to find a buffer in.</param>
        /// <param name="maximumAddress">The maximum absolute address to find a buffer in.</param>
        /// <remarks>
        /// WARNING:
        ///     Using this in a multithreaded environment can be dangerous, be careful.
        ///     It is possible to have a race condition on memory allocation.
        ///     If you want to just allocate memory, please use the provided <see cref="Allocate"/> function instead.
        /// </remarks>
        public BufferAllocationProperties FindBufferLocation(int size, nuint minimumAddress, nuint maximumAddress)
        {
            if (minimumAddress <= 0)
                throw new ArgumentException("Please do not set the minimum address to 0 or negative. It collides with the return values of Windows API functions" +
                                            "where e.g. 0 is returned on failure but you can also allocate successfully on 0.");

            int bufferSize = GetBufferSize(size);
            var candidates = FindCandidateLocations(bufferSize, minimumAddress, maximumAddress);

            if (candidates.Count > 0)
                return new BufferAllocationProperties(candidates[0], bufferSize);

            throw new Exception($"Unable to find memory location to fit MemoryBuffer of size {size} ({bufferSize}) between {minimumAddress} and {maximumAddress}.");
        }

        /// <summary>
        /// Creates a <see cref="MemoryBuffer"/> that satisfies a set size constraint
        /// and proximity to a set address.
        /// </summary>
        /// <param name="size">The minimum size the <see cref="MemoryBuffer"/> will have to accomodate.</param>
        /// <param name="minimumAddress">The minimum absolute address to create a buffer in.</param>
        /// <param name="maximumAddress">The maximum absolute address to create a buffer in.</param>
        /// <param name="retryCount">In the case the memory allocation fails; the amount of times memory allocation is to be retried.</param>
        /// <exception cref="System.Exception">Memory allocation failure due to possible race condition with other process/process itself/Windows scheduling.</exception>
        public MemoryBuffer CreateMemoryBuffer(int size, nuint minimumAddress = 0x10000, nuint maximumAddress = 0x7FFFFFFF, int retryCount = 3)
        {
            if (minimumAddress <= 0)
                throw new ArgumentException("Please do not set the minimum address to 0 or negative. It collides with the return values of Windows API functions" +
                                            "where e.g. 0 is returned on failure but you can also allocate successfully on 0.");

            // Serialized so that the scan can't go stale through another of our own threads allocating the
            // candidate first. Threads outside our control can still, that's what the candidate fallthrough and
            // retries below are for.
            bool lockTaken = false;
            try
            {
                MemoryBuffer.EnterGlobalLock(ref lockTaken, nameof(CreateMemoryBuffer));
                return Run(retryCount, () =>
                {
                    int bufferSize = GetBufferSize(size);

                    // Walk the candidates outwards from the middle of the window. A candidate can go stale between the
                    // scan and the allocation (by basically anything else), so fall through to the next one rather 
                    // than restarting the whole scan, which would be a waste.
                    foreach (var candidate in FindCandidateLocations(bufferSize, minimumAddress, maximumAddress))
                    {
                        try
                        {
                            var buffer = MemoryBufferFactory.CreateBuffer(candidate, bufferSize);
                            _bufferSearcher.AddBuffer(buffer);

                            return buffer;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // Buffer is probably taken now by something else
                            // Ignore OOM, not recoverable
                        }
                    }

                    throw new Exception($"Unable to find memory location to fit MemoryBuffer of size {size} ({bufferSize}) between {minimumAddress} and {maximumAddress}.");
                });
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(MemoryBuffer.GlobalLock);
            }
        }

        /*
            -------------------
            Core Helper Methods
            -------------------
        */
        /// <summary>
        /// Searches unmanaged memory for pre-existing <see cref="MemoryBuffer"/>s that satisfy
        /// the given size requirements and address range.
        /// </summary>
        /// <param name="size">The amount of bytes a buffer must have minimum.</param>
        /// <param name="minimumAddress">The maximum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="maximumAddress">The minimum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <returns></returns>
        public MemoryBuffer[] FindBuffers(int size, nuint minimumAddress, nuint maximumAddress)
        {
            // Get buffers already existing in process.
            var buffers = _bufferSearcher.GetBuffers(size);

            // Get all MemoryBuffers where their raw data range fits into the given minimum and maximum address.
            AddressRange allowedRange = new AddressRange(minimumAddress, maximumAddress);
            var memoryBuffers         = new List<MemoryBuffer>(buffers.Length);

            foreach (var buffer in buffers)
            {
                var bufferHeader = buffer.Properties;
                var bufferAddressRange = new AddressRange(bufferHeader.DataPointer, (bufferHeader.DataPointer + (nuint)bufferHeader.Size));
                if (allowedRange.Contains(ref bufferAddressRange))
                    memoryBuffers.Add(buffer);
            }

            return memoryBuffers.ToArray();
        }

        /// <summary>
        /// Allocates memory that satisfies a set size constraint and proximity to a set address.
        /// </summary>
        /// <param name="size">The minimum size of the memory to be allocated.</param>
        /// <param name="minimumAddress">The minimum absolute address to allocate in.</param>
        /// <param name="maximumAddress">The maximum absolute address to allocate in.</param>
        /// <param name="retryCount">In the case the memory allocation for a potential location fails; the amount of times memory allocation is to be retried.</param>
        /// <exception cref="System.Exception">Memory allocation failure due to possible race condition with other process/process itself/Windows scheduling.</exception>
        /// <remarks>
        ///     This function is virtually the same to running <see cref="FindBufferLocation"/> and then running Windows'
        ///     VirtualAlloc yourself, except that it's safe to call from multiple threads (allocations are serialized
        ///     within the process), and that losing a candidate address to something outside our control (or a wine bug
        ///     where allocation can fail on the first free pages repeatedly) is absorbed by falling through to the next
        ///     candidate address and retrying.
        ///     The memory is allocated with the PAGE_EXECUTE_READWRITE permissions.
        /// </remarks>
        public BufferAllocationProperties Allocate(int size, nuint minimumAddress = 0x10000, nuint maximumAddress = 0x7FFFFFFF, int retryCount = 3)
        {
            if (minimumAddress <= 0)
                throw new ArgumentException("Please do not set the minimum address to 0 or negative. It collides with the return values of Windows API functions" +
                                            "where e.g. 0 is returned on failure but you can also allocate successfully on 0.");

            // See CreateMemoryBuffer for why allocation is serialized, and why the lock is taken this way.
            bool lockTaken = false;
            try
            {
                MemoryBuffer.EnterGlobalLock(ref lockTaken, nameof(Allocate));
                return Run(retryCount, () =>
                {
                    int bufferSize = GetBufferSize(size);

                    // See CreateMemoryBuffer: try successively further candidates rather than restarting the scan.
                    foreach (var candidate in FindCandidateLocations(bufferSize, minimumAddress, maximumAddress))
                    {
                        if (VirtualAllocUtility.VirtualAllocLocal(candidate, (ulong)bufferSize) != UIntPtr.Zero)
                            return new BufferAllocationProperties(candidate, bufferSize);
                    }

                    throw new Exception($"Unable to find memory location to fit allocation of size {size} ({bufferSize}) between {minimumAddress} and {maximumAddress}.");
                });
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(MemoryBuffer.GlobalLock);
            }
        }

        /// <summary>
        /// Frees memory that has been allocated by <see cref="Allocate"/>.
        /// </summary>
        /// <param name="address">The address of the memory originally received from the call to <see cref="Allocate"/>.</param>
        public void Free(nuint address)
        {
            VirtualFreeUtility.VirtualFreeLocal(address);
        }


        /*
            -----------------------
            Internal Helper Methods
            -----------------------
        */

        /// <summary>
        /// Calculates the size of a <see cref="MemoryBuffer"/> to be created for a given requested size
        /// of raw data, taking into consideration buffer overhead.
        /// </summary>
        /// <param name="size">The size of the buffer to be allocated.</param>
        /// <returns>A calculated buffer size based off of the requested capacity in bytes.</returns>
        public int GetBufferSize(int size)
        {
            GetSystemInfo(out var systemInfo);

            // Round to the allocation granularity instead of page size. VirtualAlloc will only accept a base address
            // that is a multiple of dwAllocationGranularity, so a buffer rounded to the 4KB page size still exhausts
            // a full granule. The remaining 60KB reads as MEM_FREE but can never be the base of another allocation.
            // Committing the whole granule costs memory we had already given up the address space for, and raises
            // the number of items a single buffer can hold.
            int granularity = (int)systemInfo.dwAllocationGranularity;

            // We only care about x64 but let's make sure anyway
            int pageSize = DefaultPageSize;
            if (systemInfo.dwPageSize > pageSize || (pageSize % systemInfo.dwPageSize != 0))
                pageSize = (int)systemInfo.dwPageSize;

            if (granularity < pageSize)
                granularity = pageSize;

            return Mathematics.RoundUp(size, granularity);
        }


        /// <summary>
        /// Runs a given function with a specified number of retries if an exception is thrown.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="retries">The number of times to retry the function.</param>
        /// <param name="function">The function to run.</param>
        private T Run<T>(int retries, Func<T> function)
        {
            Exception caughtException = new Exception("This should not throw");
            for (int x = 0; x < retries; x++)
            {
                try  { return function();  }
                // Give up immediately if system is OOM
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex) { caughtException = ex; }
            }

            throw caughtException;
        }

        /// <summary>
        /// Finds every location at which a buffer of the given size could be placed inside the given address range,
        /// ordered by proximity to the middle of that range.
        /// </summary>
        /// <param name="bufferSize">The size a <see cref="MemoryBuffer"/> would occupy, as returned by <see cref="GetBufferSize"/>.</param>
        /// <param name="minimumAddress">The minimum absolute address a <see cref="MemoryBuffer"/> may occupy.</param>
        /// <param name="maximumAddress">The maximum absolute address a <see cref="MemoryBuffer"/> may occupy.</param>
        /// <returns>Candidate base addresses, nearest-first. Empty if the range cannot fit a buffer.</returns>
        private List<nuint> FindCandidateLocations(int bufferSize, nuint minimumAddress, nuint maximumAddress)
        {
            // Callers derive the range as "a target address, plus or minus the reach of a relative jump", so the
            // middle of the range is the address the caller actually wants to be near. Placing buffers there rather
            // than in the first free region above minimumAddress keeps them within range of later requests made
            // around neighboring targets, letting us reuse them instead of wasting one more granule every time.
            
            nuint preferred = minimumAddress + ((maximumAddress - minimumAddress) / 2);

            var memoryPages = MemoryPages.GetPages(Process);
            var candidates  = new List<(nuint Pointer, nuint Distance)>();

            for (int x = 0; x < memoryPages.Count; x++)
            {
                var pointer = GetBufferPointerNearest(memoryPages[x], bufferSize, minimumAddress, maximumAddress, preferred);
                if (pointer != 0)
                    candidates.Add((pointer, pointer > preferred ? pointer - preferred : preferred - pointer));
            }

            candidates.Sort((a, b) => a.Distance < b.Distance ? -1 : (a.Distance > b.Distance ? 1 : 0));

            var result = new List<nuint>(candidates.Count);
            foreach (var candidate in candidates)
                result.Add(candidate.Pointer);

            return result;
        }

        /// <summary>
        /// Returns the base address closest to <paramref name="preferred"/> at which a buffer of the given size fits
        /// entirely inside both the given free region and the given address range.
        /// </summary>
        /// <param name="pageInfo">Information about a single memory region.</param>
        /// <param name="bufferSize">The size that a <see cref="MemoryBuffer"/> would occupy.</param>
        /// <param name="minimumPtr">The minimum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="maximumPtr">The maximum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="preferred">The address the buffer would ideally sit closest to.</param>
        /// <returns>Zero if no such address exists; otherwise a positive value.</returns>
        private static nuint GetBufferPointerNearest(in MEMORY_BASIC_INFORMATION pageInfo, int bufferSize, nuint minimumPtr, nuint maximumPtr, nuint preferred)
        {
            // Fast return if page is not free.
            if (pageInfo.State != (uint)MEM_ALLOCATION_TYPE.MEM_FREE)
                return 0;

            // This is valid in both 32bit and 64bit Windows.
            // We can call GetSystemInfo to get this but that's a waste; these are constant for x86 and x64.
            nuint allocationGranularity = 65536;

            nuint pageStart = (nuint)pageInfo.BaseAddress;
            nuint pageEnd   = pageStart + (nuint)pageInfo.RegionSize;

            if (pageEnd < pageStart)
                return 0;

            // Intersect the free region with the caller's range.
            nuint lowest  = pageStart > minimumPtr ? pageStart : minimumPtr;
            nuint highest = pageEnd   < maximumPtr ? pageEnd   : maximumPtr;

            if (highest < lowest || highest - lowest < (nuint)bufferSize)
                return 0;

            // VirtualAlloc only accepts a base that is a multiple of the allocation granularity, so the usable
            // bases within the intersection are the granule boundaries from `first` to `last` inclusive.
            nuint first = Mathematics.RoundUp(lowest, allocationGranularity);
            if (first < lowest) // Rounding wrapped past the top of the address space.
                return 0;

            nuint last = Mathematics.RoundDown(highest - (nuint)bufferSize, allocationGranularity);
            if (last < first)
                return 0;

            // Clamp the preferred address into [first, last]
            if (preferred <= first)
                return first;

            if (preferred >= last)
                return last;

            // Snap to granule boundary
            nuint snapped = Mathematics.RoundDown(preferred, allocationGranularity);
            return snapped < first ? first : snapped;
        }
    }
}
