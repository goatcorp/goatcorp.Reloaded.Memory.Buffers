namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// The API a given allocation attempt was made through.
    /// </summary>
    public enum AllocationMethod
    {
        /// <summary>
        /// The kernel tried to find memory for us.
        /// </summary>
        VirtualAlloc2,

        /// <summary>
        /// We did a scan ourselves.
        /// </summary>
        VirtualAlloc
    }

    /// <summary>
    /// How an individual allocation attempt ended.
    /// </summary>
    public enum AllocationOutcome
    {
        /// <summary>
        /// The allocation succeeded.
        /// </summary>
        Succeeded,

        /// <summary>
        /// The allocation call was made and the kernel return an error we didn't know.
        /// </summary>
        KernelRefused,

        /// <summary>
        /// No call was made because the requested address range could not be turned into one the API accepts.
        /// </summary>
        RangeUnusable,

        /// <summary>
        /// No call was made because the usable part of the requested address range is too small to fit the allocation.
        /// </summary>
        RangeTooSmall,

        /// <summary>
        /// No call was made because scanning the process memory map turned up no free region.
        /// </summary>
        NoFreeRegionFound
    }

    /// <summary>
    /// A record of one allocation attempt.
    /// </summary>
    public readonly struct AllocationAttempt
    {
        /// <summary>
        /// The API this attempt was made through.
        /// </summary>
        public AllocationMethod Method { get; }

        /// <summary>
        /// How the attempt ended.
        /// </summary>
        public AllocationOutcome Outcome { get; }

        /// <summary>
        /// The lowest address the allocation was permitted to occupy (post-alignment).
        /// </summary>
        public nuint LowestAddress { get; }

        /// <summary>
        /// The highest address the allocation was permitted to occupy (post-alignment).
        /// </summary>
        public nuint HighestAddress { get; }

        /// <summary>
        /// The Win32 error code if there was a failed call.
        /// </summary>
        public int Win32Error { get; }

        internal AllocationAttempt(AllocationMethod method, AllocationOutcome outcome, nuint lowestAddress, nuint highestAddress, int win32Error = 0)
        {
            Method         = method;
            Outcome        = outcome;
            LowestAddress  = lowestAddress;
            HighestAddress = highestAddress;
            Win32Error     = win32Error;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string detail = Outcome switch
            {
                AllocationOutcome.Succeeded => "succeeded",
                AllocationOutcome.KernelRefused => $"win32={Win32Error}",
                AllocationOutcome.RangeUnusable => "range unusable",
                AllocationOutcome.RangeTooSmall => "range too small",
                AllocationOutcome.NoFreeRegionFound => "no free region in range",
                _ => Outcome.ToString()
            };

            return $"{Method} [0x{LowestAddress:X}-0x{HighestAddress:X}] {detail}";
        }
    }
}
