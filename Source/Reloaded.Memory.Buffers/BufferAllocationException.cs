using System;
using System.Collections.Generic;
using System.Text;

namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// Thrown when an allocation couldn't be made inside the requested address range, for debugging on the app side.
    /// </summary>
    public class BufferAllocationException : Exception
    {
        private const int MaxAttemptsInMessage = 8;

        /// <summary>
        /// Gets the buffer size that was requested by the caller.
        /// </summary>
        public int RequestedSize { get; }

        /// <summary>
        /// Gets the size that was actually being allocated, including overhead.
        /// </summary>
        public int BufferSize { get; }

        /// <summary>
        /// Gets the lowest allowable address.
        /// </summary>
        public nuint MinimumAddress { get; }

        /// <summary>
        /// Gets the highest allowable address.
        /// </summary>
        public nuint MaximumAddress { get; }

        /// <summary>
        /// Gets a value indicating whether VirtualAlloc2 was available.
        /// </summary>
        public bool VirtualAlloc2Available { get; }

        /// <summary>
        /// Gets the amount of attemps we made to allocate.
        /// </summary>
        public IReadOnlyList<AllocationAttempt> Attempts { get; }

        internal BufferAllocationException(string what, int requestedSize, int bufferSize, nuint minimumAddress,
            nuint maximumAddress, bool virtualAlloc2Available, IReadOnlyList<AllocationAttempt> attempts)
            : base(BuildMessage(what, requestedSize, bufferSize, minimumAddress, maximumAddress, attempts))
        {
            RequestedSize          = requestedSize;
            BufferSize             = bufferSize;
            MinimumAddress         = minimumAddress;
            MaximumAddress         = maximumAddress;
            VirtualAlloc2Available = virtualAlloc2Available;
            Attempts               = attempts;
        }

        private static string BuildMessage(string what, int requestedSize, int bufferSize, nuint minimumAddress,
            nuint maximumAddress, IReadOnlyList<AllocationAttempt> attempts)
        {
            var message = new StringBuilder();
            message.Append($"Unable to find memory location to fit {what} of size {requestedSize} ({bufferSize}) " +
                           $"between 0x{minimumAddress:X} and 0x{maximumAddress:X}.");

            if (attempts.Count == 0)
            {
                message.Append(" No allocation was attempted.");
                return message.ToString();
            }

            message.Append($" {attempts.Count} attempt(s): ");

            var shown = Math.Min(attempts.Count, MaxAttemptsInMessage);
            for (var x = 0; x < shown; x++)
            {
                if (x > 0)
                    message.Append("; ");

                message.Append(attempts[x].ToString());
            }

            if (shown < attempts.Count)
                message.Append($"; (+{attempts.Count - shown} more)");

            return message.ToString();
        }
    }
}
