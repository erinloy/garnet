// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Tsavorite.test
{
    /// <summary>
    /// Hygiene from the read-cache eviction-spin investigation (11-01). A record whose filler overflows splits the excess off into a new
    /// invalid record; its RecordInfo was InitialValid with Valid cleared, a zero word, which reads as IsNull: the read-cache eviction walk
    /// takes that for a page's unused tail. The split-off record now carries kTempInvalidAddress, as an abandoned read-cache record does.
    /// Read-cache copies cannot reach the split (their filler is 0-7 bytes), so this is not the cause of the 10-30 lost links, which is
    /// still unknown.
    /// </summary>
    [TestFixture]
    internal unsafe class ASplitOffFillerRecordIsNeverAZeroWordTests
    {
        [Test]
        [Category("TsavoriteKV")]
        public void A_split_off_record_is_invalid_names_no_chain_and_is_not_null()
        {
            const int bufferSize = 1 << 14, explicitFiller = 4096;
            var buffer = (byte*)NativeMemory.AlignedAlloc(bufferSize, 64);
            try
            {
                NativeMemory.Clear(buffer, bufferSize);
                var recordBase = (long)buffer;
                var rdh = (RecordDataHeader*)(recordBase + RecordInfo.Size);
                var unaligned = rdh->GetUnalignedComponentSum();
                var aligned = rdh->GetAlignedComponentSum();
                ClassicAssert.Greater(explicitFiller >> 3, RecordDataHeader.MaxFillerWords, "CONTROL: the filler overflows and splits");

                rdh->SetFiller(recordBase, aligned - unaligned + explicitFiller);

                var splitOff = (RecordInfo*)(recordBase + aligned + (RecordDataHeader.RecordSplitRetainFillerWords << 3));
                ClassicAssert.AreEqual(RecordDataHeader.RecordSplitRetainFillerWords, rdh->FillerWords, "CONTROL: the record keeps its retained filler");
                ClassicAssert.IsTrue(splitOff->Invalid, "the split-off record is invalid");
                ClassicAssert.IsFalse(splitOff->IsNull, "its word is not zero, so no walk takes it for a page's unused tail");
                ClassicAssert.AreEqual(LogAddress.kTempInvalidAddress, splitOff->PreviousAddress, "it names no chain, as an abandoned read-cache record");
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }
    }
}
