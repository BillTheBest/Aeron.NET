﻿using System;
using System.Runtime.CompilerServices;
using Adaptive.Aeron.Protocol;
using Adaptive.Agrona;
using Adaptive.Agrona.Concurrent;

namespace Adaptive.Aeron.LogBuffer
{
    /// <summary>
    /// Term buffer appender which supports many producers concurrently writing an append-only log.
    /// 
    /// <b>Note:</b> This class is threadsafe.
    /// 
    /// Messages are appended to a term using a framing protocol as described in <seealso cref="FrameDescriptor"/>.
    /// 
    /// A default message header is applied to each message with the fields filled in for fragment flags, type, term number,
    /// as appropriate.
    /// 
    /// A message of type <seealso cref="FrameDescriptor.PADDING_FRAME_TYPE"/> is appended at the end of the buffer if claimed
    /// space is not sufficiently large to accommodate the message about to be written.
    /// </summary>
    public class TermAppender
    {
        /// <summary>
        /// The append operation tripped the end of the buffer and needs to rotate.
        /// </summary>
        public const int TRIPPED = -1;

        /// <summary>
        /// The append operation went past the end of the buffer and failed.
        /// </summary>
        public const int FAILED = -2;

        private readonly long tailAddressOffset;
        private readonly UnsafeBuffer _termBuffer;
        private readonly UnsafeBuffer _metaDataBuffer;

        /// <summary>
        /// Construct a view over a term buffer and state buffer for appending frames.
        /// </summary>
        /// <param name="termBuffer">     for where messages are stored. </param>
        /// <param name="metaDataBuffer"> for where the state of writers is stored manage concurrency. </param>
        /// <param name="partitionIndex"> for this will be the active appender.</param>
        public TermAppender(UnsafeBuffer termBuffer, UnsafeBuffer metaDataBuffer, int partitionIndex)
        {
            var tailCounterOffset = LogBufferDescriptor.TERM_TAIL_COUNTERS_OFFSET + partitionIndex * BitUtil.SIZE_OF_LONG;
            metaDataBuffer.BoundsCheck(tailCounterOffset, BitUtil.SIZE_OF_LONG);
            _termBuffer = termBuffer;
            _metaDataBuffer = metaDataBuffer;
            tailAddressOffset = tailCounterOffset; // TODO divergence
        }

        /// <summary>
        /// Get the raw value current tail value in a volatile memory ordering fashion.
        /// </summary>
        /// <returns> the current tail value. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long RawTailVolatile()
        {
            return _metaDataBuffer.GetLongVolatile((int)tailAddressOffset);
        }

        /// <summary>
        /// Set the value for the tail counter.
        /// </summary>
        /// <param name="termId"> for the tail counter </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TailTermId(int termId)
        {
            _metaDataBuffer.PutLong((int)tailAddressOffset, ((long) termId) << 32);
        }
        
        /// <summary>
        /// Claim length of a the term buffer for writing in the message with zero copy semantics.
        /// </summary>
        /// <param name="header">      for writing the default header. </param>
        /// <param name="length">      of the message to be written. </param>
        /// <param name="bufferClaim"> to be updated with the claimed region. </param>
        /// <returns> the resulting offset of the term after the append on success otherwise <seealso cref="#TRIPPED"/> or <seealso cref="#FAILED"/>
        /// packed with the termId if a padding record was inserted at the end. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Claim(HeaderWriter header, int length, BufferClaim bufferClaim)
        {
            int frameLength = length + DataHeaderFlyweight.HEADER_LENGTH;
            int alignedLength = BitUtil.Align(frameLength, FrameDescriptor.FRAME_ALIGNMENT);
            long rawTail = GetAndAddRawTail(alignedLength);
            long termOffset = rawTail & 0xFFFFFFFFL;

            UnsafeBuffer termBuffer = _termBuffer;
            int termLength = termBuffer.Capacity;

            long resultingOffset = termOffset + alignedLength;
            if (resultingOffset > termLength)
            {
                resultingOffset = HandleEndOfLogCondition(termBuffer, termOffset, header, termLength, TermId(rawTail));
            }
            else
            {
                int offset = (int) termOffset;
                header.Write(termBuffer, offset, frameLength, TermId(rawTail));
                bufferClaim.Wrap(termBuffer, offset, frameLength);
            }

            return resultingOffset;
        }

        /// <summary>
        /// Append an unfragmented message to the the term buffer.
        /// </summary>
        /// <param name="header">    for writing the default header. </param>
        /// <param name="srcBuffer"> containing the message. </param>
        /// <param name="srcOffset"> at which the message begins. </param>
        /// <param name="length">    of the message in the source buffer. </param>
        /// <param name="reservedValueSupplier"><see cref="ReservedValueSupplier"/> for the frame</param>
        /// <returns> the resulting offset of the term after the append on success otherwise <seealso cref="TRIPPED"/> or <seealso cref="FAILED"/>
        /// packed with the termId if a padding record was inserted at the end. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if DEBUG
        public virtual long AppendUnfragmentedMessage(HeaderWriter header, UnsafeBuffer srcBuffer, int srcOffset, int length, ReservedValueSupplier reservedValueSupplier)
#else
        public long AppendUnfragmentedMessage(HeaderWriter header, UnsafeBuffer srcBuffer, int srcOffset, int length, ReservedValueSupplier reservedValueSupplier)
#endif
        {
            int frameLength = length + DataHeaderFlyweight.HEADER_LENGTH;
            int alignedLength = BitUtil.Align(frameLength, FrameDescriptor.FRAME_ALIGNMENT);
            long rawTail = GetAndAddRawTail(alignedLength);
            long termOffset = rawTail & 0xFFFFFFFFL;

            UnsafeBuffer termBuffer = _termBuffer;
            int termLength = termBuffer.Capacity;

            long resultingOffset = termOffset + alignedLength;
            if (resultingOffset > termLength)
            {
                resultingOffset = HandleEndOfLogCondition(termBuffer, termOffset, header, termLength, TermId(rawTail));
            }
            else
            {
                int offset = (int) termOffset;
                header.Write(termBuffer, offset, frameLength, TermId(rawTail));
                termBuffer.PutBytes(offset + DataHeaderFlyweight.HEADER_LENGTH, srcBuffer, srcOffset, length);

                if (null != reservedValueSupplier)
                {
                    long reservedValue = reservedValueSupplier(termBuffer, offset, frameLength);
                    termBuffer.PutLong(offset + DataHeaderFlyweight.RESERVED_VALUE_OFFSET, reservedValue);
                }

                FrameDescriptor.FrameLengthOrdered(termBuffer, offset, frameLength);
            }

            return resultingOffset;
        }


        /// <summary>
        /// Append a fragmented message to the the term buffer.
        /// The message will be split up into fragments of MTU length minus header.
        /// </summary>
        /// <param name="header">           for writing the default header. </param>
        /// <param name="srcBuffer">        containing the message. </param>
        /// <param name="srcOffset">        at which the message begins. </param>
        /// <param name="length">           of the message in the source buffer. </param>
        /// <param name="maxPayloadLength"> that the message will be fragmented into. </param>
        /// /// <param name="reservedValueSupplier"><see cref="ReservedValueSupplier"/> for the frame</param>
        /// <returns> the resulting offset of the term after the append on success otherwise <seealso cref="#TRIPPED"/> or <seealso cref="#FAILED"/>
        /// packed with the termId if a padding record was inserted at the end. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long AppendFragmentedMessage(HeaderWriter header, UnsafeBuffer srcBuffer, int srcOffset, int length,
            int maxPayloadLength, ReservedValueSupplier reservedValueSupplier)
        {
            int numMaxPayloads = length/maxPayloadLength;
            int remainingPayload = length%maxPayloadLength;
            int lastFrameLength = remainingPayload > 0
                ? BitUtil.Align(remainingPayload + DataHeaderFlyweight.HEADER_LENGTH, FrameDescriptor.FRAME_ALIGNMENT)
                : 0;
            int requiredLength = (numMaxPayloads*(maxPayloadLength + DataHeaderFlyweight.HEADER_LENGTH)) +
                                 lastFrameLength;
            long rawTail = GetAndAddRawTail(requiredLength);
            int termId = TermId(rawTail);
            long termOffset = rawTail & 0xFFFFFFFFL;

            UnsafeBuffer termBuffer = _termBuffer;
            int termLength = termBuffer.Capacity;

            long resultingOffset = termOffset + requiredLength;
            if (resultingOffset > termLength)
            {
                resultingOffset = HandleEndOfLogCondition(termBuffer, termOffset, header, termLength, termId);
            }
            else
            {
                int offset = (int) termOffset;
                byte flags = FrameDescriptor.BEGIN_FRAG_FLAG;
                int remaining = length;
                do
                {
                    int bytesToWrite = Math.Min(remaining, maxPayloadLength);
                    int frameLength = bytesToWrite + DataHeaderFlyweight.HEADER_LENGTH;
                    int alignedLength = BitUtil.Align(frameLength, FrameDescriptor.FRAME_ALIGNMENT);

                    header.Write(termBuffer, offset, frameLength, termId);
                    termBuffer.PutBytes(offset + DataHeaderFlyweight.HEADER_LENGTH, srcBuffer,
                        srcOffset + (length - remaining), bytesToWrite);

                    if (remaining <= maxPayloadLength)
                    {
                        flags |= FrameDescriptor.END_FRAG_FLAG;
                    }

                    FrameDescriptor.FrameFlags(termBuffer, offset, flags);

                    if (null != reservedValueSupplier)
                    {
                        long reservedValue = reservedValueSupplier(termBuffer, offset, frameLength);
                        termBuffer.PutLong(offset + DataHeaderFlyweight.RESERVED_VALUE_OFFSET, reservedValue);
                    }

                    FrameDescriptor.FrameLengthOrdered(termBuffer, offset, frameLength);

                    flags = 0;
                    offset += alignedLength;
                    remaining -= bytesToWrite;
                } while (remaining > 0);
            }

            return resultingOffset;
        }


        /// <summary>
        /// Pack the values for termOffset and termId into a long for returning on the stack.
        /// </summary>
        /// <param name="termId">     value to be packed. </param>
        /// <param name="termOffset"> value to be packed. </param>
        /// <returns> a long with both ints packed into it. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Pack(int termId, int termOffset)
        {
            return ((long) termId << 32) | (termOffset & 0xFFFFFFFFL);
        }

        /// <summary>
        /// The termOffset as a result of the append
        /// </summary>
        /// <param name="result"> into which the termOffset value has been packed. </param>
        /// <returns> the termOffset after the append </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TermOffset(long result)
        {
            return (int) result;
        }

        /// <summary>
        /// The termId in which the append operation took place.
        /// </summary>
        /// <param name="result"> into which the termId value has been packed. </param>
        /// <returns> the termId in which the append operation took place. </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TermId(long result)
        {
            return (int)((long)((ulong)result >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long HandleEndOfLogCondition(UnsafeBuffer termBuffer, long termOffset, HeaderWriter header,
            int termLength, int termId)
        {
            int resultingOffset = FAILED;

            if (termOffset <= termLength)
            {
                resultingOffset = TRIPPED;

                if (termOffset < termLength)
                {
                    int offset = (int) termOffset;
                    int paddingLength = termLength - offset;
                    header.Write(termBuffer, offset, paddingLength, termId);
                    FrameDescriptor.FrameType(termBuffer, offset, FrameDescriptor.PADDING_FRAME_TYPE);
                    FrameDescriptor.FrameLengthOrdered(termBuffer, offset, paddingLength);
                }
            }

            return Pack(termId, resultingOffset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long GetAndAddRawTail(int alignedLength)
        {
            return _metaDataBuffer.GetAndAddLong((int)tailAddressOffset, alignedLength);
        }
    }
}