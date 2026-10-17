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

        // How far either side of the preferred address the first VirtualAlloc2 search window reaches.
        private const nuint InitialWindowHalfWidth = 0x100000;

        /// <summary> Implementation of the Searcher that scans and finds existing <see cref="MemoryBuffer"/>s within the current process. </summary>
        private static readonly MemoryBufferSearcher BufferSearcher = new();

        /// <summary> The process on which the MemoryBuffer acts upon. </summary>
        public Process Process { get; private set; }

        /// <summary>
        /// Creates a new <see cref="MemoryBufferHelper"/> for the specified process.
        /// </summary>
        /// <param name="process">The process.</param>
        public MemoryBufferHelper(Process process)
        {
            Process = process;

            if (process.Id != Environment.ProcessId)
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

            ValidateRequest(size, minimumAddress, maximumAddress);

            int bufferSize = GetBufferSize(size);
            var candidates = FindCandidateLocations(bufferSize, minimumAddress, maximumAddress);

            if (candidates.Count > 0)
                return new BufferAllocationProperties(candidates[0], bufferSize);

            // Scan coming up empty means we're done
            var attempts = new List<AllocationAttempt>
            {
                new(AllocationMethod.VirtualAlloc, AllocationOutcome.NoFreeRegionFound, minimumAddress, maximumAddress)
            };

            throw new BufferAllocationException("MemoryBuffer", size, bufferSize, minimumAddress, maximumAddress,
                VirtualAllocUtility.IsVirtualAlloc2Available, attempts);
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

            ValidateRequest(size, minimumAddress, maximumAddress);
            ValidateRetries(retryCount);

            // Serialized so that the scan in the fallback path can't go stale through another of our own threads
            // allocating the candidate first, and so that registering the buffer stays consistent with allocating it.
            // Threads outside our control can still take a candidate, that's what the fallthrough and retries are for.
            bool lockTaken = false;
            try
            {
                MemoryBuffer.EnterGlobalLock(ref lockTaken, nameof(CreateMemoryBuffer));
                return Run(retryCount, () =>
                {
                    int bufferSize = GetBufferSize(size);
                    var attempts   = new List<AllocationAttempt>();
                    var address    = AllocateInRange(bufferSize, minimumAddress, maximumAddress, attempts);

                    if (address == 0)
                        throw new BufferAllocationException("MemoryBuffer", size, bufferSize, minimumAddress, maximumAddress,
                            VirtualAllocUtility.IsVirtualAlloc2Available, attempts);

                    // Memory is already reserved and committed by AllocateInRange
                    var buffer = MemoryBufferFactory.CreateBuffer(address, bufferSize, allocateMemory: false);
                    BufferSearcher.AddBuffer(buffer);

                    return buffer;
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
            var buffers = BufferSearcher.GetBuffers(size);

            // Get all MemoryBuffers where the next item fits into the given minimum and maximum address.
            // The rest of the buffer can lie outside. Alignment padding is included by the caller.
            AddressRange allowedRange = new AddressRange(minimumAddress, maximumAddress);
            var memoryBuffers         = new List<MemoryBuffer>(buffers.Length);

            foreach (var buffer in buffers)
            {
                var bufferHeader = buffer.Properties;
                nuint itemEnd = size > 0 ? bufferHeader.WritePointer + (nuint)size - 1 : bufferHeader.WritePointer;
                var itemRange = new AddressRange(bufferHeader.WritePointer, itemEnd);
                if (allowedRange.Contains(ref itemRange))
                {
                    memoryBuffers.Add(buffer);
                }
            }

            return memoryBuffers.ToArray();
        }

        /// <summary>
        /// Executes a given function while holding the lock shared by every <see cref="MemoryBuffer"/>.
        /// </summary>
        /// <param name="func">The function to execute while holding the lock.</param>
        public T ExecuteWithLock<T>(Func<T> func)
        {
            // See CreateMemoryBuffer for why the lock is taken this way.
            bool lockTaken = false;
            try
            {
                MemoryBuffer.EnterGlobalLock(ref lockTaken, nameof(ExecuteWithLock));
                return func();
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(MemoryBuffer.GlobalLock);
                }
            }
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
        ///     VirtualAlloc yourself, except that it's safe to call from multiple threads. Where VirtualAlloc2 is
        ///     available the kernel picks the address, so there is no window in which the address can be taken from
        ///     under us; where it isn't, losing a candidate address to something outside our control (or a wine bug
        ///     where allocation can fail on the first free pages repeatedly) is absorbed by falling through to the next
        ///     candidate address and retrying.
        ///     The memory is allocated with the PAGE_EXECUTE_READWRITE permissions.
        /// </remarks>
        public BufferAllocationProperties Allocate(int size, nuint minimumAddress = 0x10000, nuint maximumAddress = 0x7FFFFFFF, int retryCount = 3)
        {
            if (minimumAddress <= 0)
                throw new ArgumentException("Please do not set the minimum address to 0 or negative. It collides with the return values of Windows API functions" +
                                            "where e.g. 0 is returned on failure but you can also allocate successfully on 0.");

            ValidateRequest(size, minimumAddress, maximumAddress);
            ValidateRetries(retryCount);

            // See CreateMemoryBuffer for why allocation is serialized, and why the lock is taken this way.
            bool lockTaken = false;
            try
            {
                MemoryBuffer.EnterGlobalLock(ref lockTaken, nameof(Allocate));
                return Run(retryCount, () =>
                {
                    int bufferSize = GetBufferSize(size);
                    var attempts   = new List<AllocationAttempt>();
                    var address    = AllocateInRange(bufferSize, minimumAddress, maximumAddress, attempts);

                    if (address != 0)
                        return new BufferAllocationProperties(address, bufferSize);

                    throw new BufferAllocationException("allocation", size, bufferSize, minimumAddress, maximumAddress,
                        VirtualAllocUtility.IsVirtualAlloc2Available, attempts);
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
        /// Rejects requests that don't make sense/would wrap around.
        /// </summary>
        private static void ValidateRequest(int size, nuint minimumAddress, nuint maximumAddress)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "Size must be positive.");
            }

            if (minimumAddress > maximumAddress)
            {
                throw new ArgumentException($"Minimum address 0x{minimumAddress:X} is above maximum address 0x{maximumAddress:X}.", nameof(minimumAddress));
            }
        }

        /// <summary>
        /// Reject invalid retry counts.
        /// </summary>
        private static void ValidateRetries(int retryCount)
        {
            if (retryCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retryCount), retryCount, "Retry count must be positive.");
            }
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
        /// Get an "optimal" address to find our buffer around.
        /// </summary>
        /// <param name="minimumAddress">The minimum absolute address the allocation may occupy.</param>
        /// <param name="maximumAddress">The maximum absolute address the allocation may occupy.</param>
        /// <returns></returns>
        private nuint GetPreferredAddress(nuint minimumAddress, nuint maximumAddress)
        {
            // Callers derive the range as "a target address, plus or minus the reach of a relative jump", so the
            // middle of the range is the address the caller actually wants to be near. Placing buffers there rather
            // than at the edges of the range keeps them within range of later requests made around neighboring
            // targets, letting us reuse them instead of wasting one more granule every time.
            return minimumAddress + ((maximumAddress - minimumAddress) / 2);
        }

        /// <summary>
        /// Reserves and commits <paramref name="bufferSize"/> bytes inside the given address range, as close to the
        /// middle of that range as we can manage.
        /// </summary>
        /// <param name="bufferSize">The size to allocate, as returned by <see cref="GetBufferSize"/>.</param>
        /// <param name="minimumAddress">The minimum absolute address the allocation may occupy.</param>
        /// <param name="maximumAddress">The maximum absolute address the allocation may occupy.</param>
        /// <param name="attempts">Collects every failed attempt.</param>
        /// <returns>The base address of the allocation, or zero if the range could not be satisfied.</returns>
        private nuint AllocateInRange(int bufferSize, nuint minimumAddress, nuint maximumAddress, List<AllocationAttempt> attempts)
        {
            nuint preferred = GetPreferredAddress(minimumAddress, maximumAddress);

            if (VirtualAllocUtility.IsVirtualAlloc2Available)
            {
                // MEM_ADDRESS_REQUIREMENTS has no notion of a preferred address, only of a permitted range.
                nuint size = (nuint)bufferSize;

                // Below may end up to a buffer past preferred
                nuint belowHigh = maximumAddress - preferred > size - 1 ? preferred + (size - 1) : maximumAddress;

                for (nuint halfWidth = InitialWindowHalfWidth; ; halfWidth *= 2)
                {
                    // preferred is always inside [minimumAddress, maximumAddress]
                    nuint low  = preferred - minimumAddress > halfWidth ? preferred - halfWidth : minimumAddress;
                    nuint high = maximumAddress - preferred > halfWidth ? preferred + halfWidth : maximumAddress;

                    var below = TryAllocate2(bufferSize, low, belowHigh, AllocationPlacement.TopDown, attempts);
                    if (below != 0)
                    {
                        return below;
                    }

                    var above = TryAllocate2(bufferSize, preferred, high, AllocationPlacement.BottomUp, attempts);
                    if (above != 0)
                    {
                        return above;
                    }

                    if (low == minimumAddress && high == maximumAddress)
                    {
                        break;
                    }
                }

                // We used to return here, but let's try with legacy VirtualAlloc
            }

            // No VirtualAlloc2 or it failed, do the old allocation logic
            var candidates = FindCandidateLocations(bufferSize, minimumAddress, maximumAddress);
            if (candidates.Count == 0)
                attempts.Add(new AllocationAttempt(AllocationMethod.VirtualAlloc, AllocationOutcome.NoFreeRegionFound, minimumAddress, maximumAddress));

            foreach (var candidate in candidates)
            {
                if (VirtualAllocUtility.VirtualAllocLocal(candidate, (ulong)bufferSize, out var attempt) != UIntPtr.Zero)
                    return candidate;

                attempts.Add(attempt);
            }

            return 0;
        }

        /// <summary>
        /// Make one VirtualAlloc2 attempt inside the given range.
        /// </summary>
        /// <returns>The base address of the allocation, or zero on failure.</returns>
        private static nuint TryAllocate2(int bufferSize, nuint low, nuint high, AllocationPlacement placement, List<AllocationAttempt> attempts)
        {
            var result = VirtualAllocUtility.VirtualAlloc2Local(low, high, (ulong)bufferSize, placement, out var attempt);
            if (result == UIntPtr.Zero)
            {
                attempts.Add(attempt);
            }

            return result;
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
            nuint preferred = GetPreferredAddress(minimumAddress, maximumAddress);

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
