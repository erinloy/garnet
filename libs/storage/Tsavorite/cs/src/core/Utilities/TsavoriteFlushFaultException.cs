// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    /// <summary>
    /// A log page flush the device keeps refusing. Raised to every waiter on the flushed-until address (a checkpoint's
    /// WAIT_FLUSH) once <see cref="LogSettings.FlushRetryLimit"/> consecutive writes of one range have failed, so a checkpoint
    /// FAILS with the range and the device's error code instead of waiting forever on a FlushedUntilAddress that cannot move.
    /// The page keeps being retried in the background; a later successful write clears the fault.
    /// </summary>
    public sealed class TsavoriteFlushFaultException : TsavoriteException
    {
        /// <summary>The first logical address of the range that could not be flushed.</summary>
        public long FromAddress { get; }

        /// <summary>The logical address one past the end of the range that could not be flushed.</summary>
        public long UntilAddress { get; }

        /// <summary>The device's error code on the most recent failed write.</summary>
        public uint ErrorCode { get; }

        /// <summary>How many consecutive writes of the range had failed when the fault was raised.</summary>
        public int Attempts { get; }

        /// <summary>Create the fault for a range.</summary>
        public TsavoriteFlushFaultException(long fromAddress, long untilAddress, uint errorCode, int attempts)
            : base($"Log flush of [{LogAddress.AddressString(fromAddress)}, {LogAddress.AddressString(untilAddress)}) failed {attempts} consecutive time(s) " +
                   $"(device error code {errorCode}). FlushedUntilAddress cannot advance past it, so no checkpoint can complete until a retry " +
                   "succeeds; the page is still being retried.")
        {
            FromAddress = fromAddress;
            UntilAddress = untilAddress;
            ErrorCode = errorCode;
            Attempts = attempts;
        }
    }
}
