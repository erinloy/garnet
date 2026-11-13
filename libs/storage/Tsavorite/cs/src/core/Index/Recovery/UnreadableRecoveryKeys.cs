// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System.Collections.Generic;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>A recovery that replayed past records whose key it could not read: each skip left its key's index entry at an older
    /// version, so the recovered store would answer with that older value. Thrown after every page read and flush is done.</summary>
    public sealed class RecoveryRefusedException : TsavoriteException
    {
        /// <summary>The prefix every message carries, so a caller can tell this refusal from a torn checkpoint.</summary>
        public const string Prefix = "RecoveryRefused:";

        /// <summary>Records skipped by this recovery.</summary>
        public long Skipped { get; }

        /// <summary>The first skipped addresses, in replay order.</summary>
        public IReadOnlyList<long> First { get; }

        internal RecoveryRefusedException(long skipped, IReadOnlyList<long> first)
            : base($"{Prefix} replay skipped {skipped} record(s) whose key is not inline and cannot be read without an object map, so each one's key "
                + $"would read its prior version. First at: {string.Join(", ", System.Linq.Enumerable.Select(first, LogAddress.AddressString))}. "
                + "Nothing was quarantined; an offline rebuild opens with the refusal off and skips only a tail nothing reaches.")
        {
            Skipped = skipped;
            First = first;
        }
    }

    public partial class TsavoriteBase
    {
        /// <summary>The most skipped addresses one recovery names.</summary>
        public const int UnreadableRecoveryKeysNamed = 16;

        /// <summary>When set, a recovery that skips any unreadable key throws <see cref="RecoveryRefusedException"/> once its I/O is done,
        /// instead of opening a store whose skipped keys read their prior versions. Off for an offline rebuild's source, which must open to cure it.</summary>
        public bool RefuseUnreadableRecoveryKeys { get; set; }

        /// <summary>Records the last recovery skipped because their key could not be read.</summary>
        public long LastRecoverySkippedKeys => Interlocked.Read(ref lastRecoverySkippedKeys);

        /// <summary>The first <see cref="UnreadableRecoveryKeysNamed"/> addresses the last recovery skipped.</summary>
        public IReadOnlyList<long> LastRecoverySkippedAt
        {
            get { lock (lastRecoverySkippedAt) return [.. lastRecoverySkippedAt]; }
        }

        private long lastRecoverySkippedKeys;
        private readonly List<long> lastRecoverySkippedAt = [];

        private protected void ResetRecoverySkippedKeys()
        {
            Interlocked.Exchange(ref lastRecoverySkippedKeys, 0);
            lock (lastRecoverySkippedAt) lastRecoverySkippedAt.Clear();
        }

        private protected void NoteRecoverySkippedKey(long address)
        {
            Interlocked.Increment(ref RecoveryOverflowKeys.SkippedCount);
            if (Interlocked.Increment(ref lastRecoverySkippedKeys) <= UnreadableRecoveryKeysNamed)
                lock (lastRecoverySkippedAt) lastRecoverySkippedAt.Add(address);
        }

        private protected void RefuseIfRecoverySkippedKeys()
        {
            var skipped = LastRecoverySkippedKeys;
            if (skipped > 0 && RefuseUnreadableRecoveryKeys)
                throw new RecoveryRefusedException(skipped, LastRecoverySkippedAt);
        }
    }
}
