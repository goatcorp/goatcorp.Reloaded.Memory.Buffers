using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Reloaded.Memory.Sources;

namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// Provides a buffer for permanent (until the process dies) general small size memory storage.
    /// </summary>
    public unsafe class MemoryBuffer : IDisposable
    {
        /// <summary>
        /// Lock shared by every buffer in the process, to serialize the scanning and allocation paths.
        /// This is deliberately not per-buffer. Callers (in Reloaded.Hooks) append to a second buffer while
        /// holding the first one's lock, e.g. reserving an absolute jump's pointer cell while assembling a
        /// stub. Which buffer serves such a nested request depends on address range and remaining capacity,
        /// so with per-buffer locks two threads can acquire the same pair of buffers in opposite orders and
        /// deadlock. This becomes more likely as allocation pressure grows. A single lock can't form a cycle and
        /// hook creation does not happen frequently so I think this is fine.
        /// </summary>
        internal static readonly object GlobalLock = new();
        
        private const int LockTimeoutMs = 60_000;

        /// <summary>
        /// Acquires <see cref="GlobalLock"/> and throws if we can't acquire it in time.
        /// </summary>
        /// <param name="lockTaken">Set to true if the lock was acquired. The caller ALWAYS has to release the lock.</param>
        /// <param name="context">What we are trying to do that requires a lock.</param>
        /// <exception cref="TimeoutException">The lock could not be acquired within <see cref="LockTimeoutMs"/>.</exception>
        internal static void EnterGlobalLock(ref bool lockTaken, string context)
        {
            Monitor.TryEnter(GlobalLock, LockTimeoutMs, ref lockTaken);
            if (!lockTaken)
                throw new TimeoutException(
                    $"Could not acquire the shared MemoryBuffer lock within {LockTimeoutMs / 1000} seconds ({context}). " +
                    $"Another thread is likely deadlocked while holding it, e.g. by taking a lock inside an " +
                    $"{nameof(ExecuteWithLock)} callback that is owned by a thread which is itself appending to a buffer.");
        }

        /// <summary> Defines where Memory will be read in or written to. </summary>
        public IMemory MemorySource   { get; private set; }

        /// <summary> Gets/Sets the header/properties of the buffer stored in unmanaged memory. </summary>
        public MemoryBufferProperties Properties { get; set; }

        /// <summary> Stores the location of the <see cref="MemoryBufferProperties"/> structure. </summary>
        private readonly nuint _address;

        /*
            --------------
            Constructor(s)
            --------------
        */

        internal MemoryBuffer(IMemory memorySource, nuint address)
        {
            _address = address;
            MemorySource   = memorySource;
        }

        internal MemoryBuffer(IMemory memorySource, nuint address, MemoryBufferProperties memoryBufferProperties) : this(memorySource, address)
        {
            Properties = memoryBufferProperties;
        }

        /*
            ----------
            Destructor
            ----------
        */

        /// <summary/>
        ~MemoryBuffer()
        {
            Dispose();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // TODO: This probably doesn't work as intended since MemoryBufferSearcher caches buffers?
            //MemorySource.Free(AllocationAddress);
            GC.SuppressFinalize(this);
        }

        /*
            --------------
            Core Functions
            --------------
        */

        /// <summary>
        /// Locks all buffers from use by other threads and executes a given function.
        /// The lock is shared between all buffers and is reentrant. It's safe to append to this or any other buffer
        /// from inside <paramref name="func"/>.
        /// </summary>
        /// <param name="func">The function to execute while preventing others' access to buffers.</param>
        public T ExecuteWithLock<T>(Func<T> func)
        {
            bool lockTaken = false;
            try
            {
                EnterGlobalLock(ref lockTaken, $"buffer at 0x{(ulong)_address:X}");
                return func();
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(GlobalLock);
            }
        }

        /// <summary>
        /// Sets a new alignment (in bytes) for the buffer and auto-aligns the buffer.
        /// Note that setting the alignment will move the buffer offset to the next multiple of "alignment",
        /// unless it is already aligned.
        /// </summary>
        public void SetAlignment(int alignment)
        {
            ExecuteWithLock(() =>
            {
                var bufferProperties = Properties;
                bufferProperties.SetAlignment(alignment);
                Properties = bufferProperties;

                return true;
            });
        }

        /// <summary>
        /// Allocates a fixed amount of memory on the buffer for your own data to be stored.
        /// </summary>
        /// <param name="numBytes">Number of bytes to write.</param>
        /// <param name="alignment">The memory alignment of the item to be added to the buffer.</param>
        /// <returns>Pointer to the passed in bytes written to memory. Null pointer, if it cannot fit into the buffer.</returns>
        public nuint Add(int numBytes, int alignment = 4)
        {
            return ExecuteWithLock(() =>
            {
                var bufferProperties = Properties;

                // Re-align the buffer before write operation.
                bufferProperties.SetAlignment(alignment);

                // Check if item can fit in buffer and buffer address is valid.
                // Has to be checked against the realigned copy, the write below happens at its WritePointer,
                // so checking the unaligned Properties lets a write overrun the buffer by up to alignment-1 bytes
                if (bufferProperties.Remaining < numBytes)
                    return (nuint)0;

                // Append the item to the buffer.
                nuint appendAddress = bufferProperties.WritePointer;
                bufferProperties.Offset += numBytes;
                Properties = bufferProperties;

                return appendAddress;
            });
        }

        /// <summary>
        /// Writes your own memory bytes into process' memory and gives you the address
        /// for the memory location of the written bytes.
        /// </summary>
        /// <param name="bytesToWrite">Individual bytes to be written onto the buffer.</param>
        /// <param name="alignment">The memory alignment of the item to be added to the buffer.</param>
        /// <returns>Pointer to the passed in bytes written to memory. Null pointer, if it cannot fit into the buffer.</returns>
        public nuint Add(byte[] bytesToWrite, int alignment = 4)
        {
            return ExecuteWithLock(() =>
            {
                var bufferProperties = Properties;

                // Re-align the buffer before write operation.
                bufferProperties.SetAlignment(alignment);

                // Check if item can fit in buffer and buffer address is valid.
                // Checked against the re-aligned copy, see the Add(int, int) overload.
                if (bufferProperties.Remaining < bytesToWrite.Length)
                    return (nuint)0;

                // Append the item to the buffer.
                nuint appendAddress = bufferProperties.WritePointer;
                MemorySource.WriteRaw(appendAddress, bytesToWrite);
                bufferProperties.Offset += bytesToWrite.Length;
                Properties = bufferProperties;

                return appendAddress;
            });
        }

        /// <summary>
        /// Writes your own structure address into process' memory and gives you the address 
        /// to which the structure has been directly written to.
        /// </summary>
        /// <param name="bytesToWrite">A structure to be converted into individual bytes to be written onto the buffer.</param>
        /// <param name="marshalElement">Set this to true to marshal the given parameter before writing it to the buffer, else false.</param>
        /// <param name="alignment">The memory alignment of the item to be added to the buffer.</param>
        /// <returns>Pointer to the newly written structure in memory. Null pointer, if it cannot fit into the buffer.</returns>
        public nuint Add<TStructure>(ref TStructure bytesToWrite, bool marshalElement = false, int alignment = 4)
        {
            var bytesToWriteByVal = bytesToWrite;
            return ExecuteWithLock(() =>
            {
                var bufferProperties = Properties;

                int structLength = Struct.GetSize<TStructure>(marshalElement);

                // Re-align the buffer before write operation.
                bufferProperties.SetAlignment(alignment);

                // Check if item can fit in buffer and buffer address is valid.
                // Checked against the re-aligned copy, see the Add(int, int) overload.
                if (bufferProperties.Remaining < structLength)
                    return (nuint)0;

                // Append the item to the buffer.
                nuint appendAddress = bufferProperties.WritePointer;
                MemorySource.Write(appendAddress, ref bytesToWriteByVal, marshalElement);
                bufferProperties.Offset += structLength;
                Properties = bufferProperties;

                return appendAddress;
            });
        }

        /// <summary>
        /// Returns true if the object can fit into the buffer, else false.
        /// </summary>
        /// <param name="objectSize">The size of the object to be appended to the buffer.</param>
        /// <returns>Returns true if the object can fit into the buffer, else false.</returns>
        public bool CanItemFit(int objectSize)
        {
            // Check if base buffer uninitialized or if object size too big.
            return Properties.Remaining >= objectSize;
        }

        /// <summary>
        /// Returns true if the object can fit into the buffer, else false.
        /// </summary>
        /// <param name="item">The item to check if it can fit into the buffer.</param>
        /// <param name="marshalElement">True if the item is to be marshalled, else false.</param>
        public bool CanItemFit<TGeneric>(ref TGeneric item, bool marshalElement = false)
        {
            return CanItemFit(Struct.GetSize<TGeneric>(marshalElement));
        }

        /*
            --------------
            Misc Functions
            --------------
        */

        /// <summary>
        /// [Testing use only]
        /// The address where the individual buffer has been allocated.
        /// </summary>
        internal nuint AllocationAddress => _address;

        /// <summary/>
        public override bool Equals(object obj)
        {
            // The two <see cref="MemoryBuffer"/>s are equal if their base address is the same.
            var buffer = obj as MemoryBuffer;
            return buffer != null && _address == buffer._address;
        }

        /// <summary/>
        [ExcludeFromCodeCoverage]
        public override int GetHashCode()
        {
            return (int)_address;
        }
    }
}
