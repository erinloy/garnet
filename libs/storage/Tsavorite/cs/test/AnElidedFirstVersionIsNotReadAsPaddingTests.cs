// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Tsavorite.test
{
    /// <summary>
    /// nexus 11-26: an RCU that elides a key's first version (prev below begin) SealAndInvalidates it, leaving Sealed as its only bit. A page read
    /// cleared Sealed for every record, so that word became 0 and recovery and scans took it for padding: they stepped 8 bytes into the record's
    /// data header and lost every record after it on the page (a reopened RG store: B+-tree pages missing, 49 of 50 notes absent).
    /// </summary>
    [TestFixture]
    internal class AnElidedFirstVersionIsNotReadAsPaddingTests : TestBase
    {
        [Test]
        public unsafe void A_sealed_invalid_record_keeps_a_non_null_word_through_a_page_read()
        {
            var elided = RecordInfo.InitialValid;
            elided.PreviousAddress = LogAddress.kInvalidAddress;   // a first version with no chain below it, as on the RG store's page
            elided.SealAndInvalidate();
            ClassicAssert.AreEqual(0x0010_0000_0000_0000L, *(long*)&elided, "CONTROL: the word an RG store held at its stop point, Sealed alone");
            elided.ClearBitsForDiskImages();
            ClassicAssert.IsFalse(elided.IsNull, "MUTANT (Sealed cleared on an invalid record): an elided first version reads as padding");
            ClassicAssert.IsTrue(elided.Invalid, "it stays closed");

            var superseded = RecordInfo.InitialValid;
            superseded.Seal();
            superseded.ClearBitsForDiskImages();
            ClassicAssert.IsFalse(superseded.IsSealed, "CONTROL: a valid sealed record may become current again, so its Sealed bit clears");
        }
    }
}
