using System.Collections.Concurrent;
using System.Linq;

namespace Reloaded.Memory.Buffers.Internal
{
    /// <summary>
    /// Utility class which searches for existing <see cref="MemoryBuffer"/>s
    /// in a process with support for caching already found buffers.
    /// </summary>
    internal class MemoryBufferSearcher
    {
        /// <summary> Maintains address to buffer mappings. </summary>
        private readonly ConcurrentDictionary<nuint, MemoryBuffer> _bufferCache = new();
        
        /// <summary>
        /// Adds a new <see cref="MemoryBuffer"/> to the internal buffer cache.
        /// </summary>
        /// <param name="buffer">The buffer which to add to cache.</param>
        internal void AddBuffer(MemoryBuffer buffer)
        {
            _bufferCache[buffer.AllocationAddress] = buffer;
        }

        /// <summary>
        /// Returns a list of buffers that satisfy the passed in size requirements.
        /// </summary>
        /// <param name="size">The amount of bytes a buffer must have minimum.</param>
        /// <returns></returns>
        internal MemoryBuffer[] GetBuffers(int size)
        {
            var memoryBuffers = _bufferCache.Values.Where(x => x.CanItemFit(size)).ToArray();
            return memoryBuffers;
        }
    }
}