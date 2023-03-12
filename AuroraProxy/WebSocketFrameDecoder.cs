using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace AuroraProxy
{
    public static class WebSocketLimits
    {
        public static readonly int MAX_LEN_125 = 125;
        public static readonly int MAX_LEN_126 = 65536;
        public static readonly int MAX_LEN_127 = 1073741824;
    }

    public class WebSocketFrameCoder
    {
        private readonly Random _random_ = new Random();
        private List<byte> _endOfPreviousBuffer = new();

        public WebSocketFrame CreatePingFrame()
        {
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = true;
            frame.Length = 0;
            frame.Mask = null;
            frame.OpCode = WebSocketFrameOpCode.PING;
            return frame;
        }

        public WebSocketFrame CreatePongFrame(WebSocketFrame pingFrame)
        {
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = true;
            frame.Length = pingFrame.Length;
            frame.Mask = createMask();
            frame.OpCode = WebSocketFrameOpCode.PONG;
            frame.OriginalBytes = pingFrame.OriginalBytes;
            return frame;
        }

        public WebSocketFrame CreateCloseFrame(bool isMasked)
        {
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = true;
            frame.Length = 0;
            frame.Mask = isMasked ? createMask() : null;
            frame.OpCode = WebSocketFrameOpCode.CLOSE;
            return frame;
        }

        public IEnumerable<WebSocketFrame> FromBytes(IEnumerable<byte> bytes)
        {
            List<WebSocketFrame> restoredFromBytes = new();
            List<byte> fullBytes = new();
            fullBytes.AddRange(_endOfPreviousBuffer);
            fullBytes.AddRange(bytes);
            int offset = 0;
            while(offset != -1)
            {
                WebSocketFrame singleFrame = singleFromBytes(fullBytes, ref offset);
                if (singleFrame is not null) restoredFromBytes.Add(singleFrame);
            }
            return restoredFromBytes.ToArray();
        }

        private WebSocketFrame singleFromBytes(IEnumerable<byte> bytes, ref int offset)
        {
            _endOfPreviousBuffer = new();
            List<byte> offsettedBytes = bytes.Skip(offset).ToList();
            int readIndex = 0;
            if (offsettedBytes.Count < 2)
            {
                _endOfPreviousBuffer = offsettedBytes;
                offset = -1;
                return null;
            }
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = (offsettedBytes[readIndex] & 0x80) != 0;
            frame.ReservedBit1 = (offsettedBytes[readIndex] & 0x40) != 0;
            frame.ReservedBit2 = (offsettedBytes[readIndex] & 0x20) != 0;
            frame.ReservedBit3 = (offsettedBytes[readIndex] & 0x10) != 0;
            frame.OpCode = (WebSocketFrameOpCode)(offsettedBytes[readIndex++] & 0x0F);
            bool isMasked = (offsettedBytes[readIndex] & 0x80) != 0;
            int length = (offsettedBytes[readIndex++] & 0x7F);
            if (length == 126)
            {
                length = 0;
                for (int i = 0; i < 2; i++)
                {
                    length <<= 8;
                    length |= offsettedBytes[readIndex++];
                }
            }
            else if (length == 127)
            {
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length <<= 8;
                    length |= offsettedBytes[readIndex++];
                }
            }
            if (length > offsettedBytes.Count - readIndex)
            {
                Console.WriteLine($"BUFFER NOT COMPLETE. DIFFERENCE: {length - offsettedBytes.Count + readIndex}");
                _endOfPreviousBuffer = offsettedBytes;
                offset = -1;
                return null;
            }
            frame.Length = length;
            if (isMasked)
            {
                int mask = 0;
                for (int i = 0; i < 4; i++)
                {
                    mask <<= 8;
                    mask |= offsettedBytes[readIndex++];
                }
                frame.Mask = mask;
            }
            frame.MaskedBytes = offsettedBytes.Skip(readIndex).Take(length).ToArray();
            offset += readIndex + length;
            if (offset >= bytes.Count()) offset = -1;
#if DEBUG
            Console.WriteLine("===============");
            Console.WriteLine($"FIN: {(frame.IsFinal ? "1" : "0")} RSV1: {(frame.ReservedBit1 ? "1" : "0")} RSV2: {(frame.ReservedBit2 ? "1" : "0")} RSV3: {(frame.ReservedBit3 ? "1" : "0")}");
            Console.WriteLine($"Opcode: {frame.OpCode.ToString()}");
            Console.WriteLine($"Mask: {(frame.IsMasked ? "1" : 0)} Length: {frame.Length}");
            //Console.Write("Orig Bytes: ");
            //foreach (var b in frame.OriginalBytes)
            //    Console.Write($"{b} ");
            Console.WriteLine($"Text: {(frame.OpCode == WebSocketFrameOpCode.TEXT ? Encoding.UTF8.GetString(frame.OriginalBytes) : "")}");
            Console.WriteLine("===============");
#endif
            return frame;
        }

        public IEnumerable<byte> ToBytes(IEnumerable<WebSocketFrame> frames)
        {
            List<byte> bytes = new();
            foreach(var frame in frames)
            {
                bytes.AddRange(frame.FrameBytes);
            }
            return bytes;
        }

        public IEnumerable<WebSocketFrame> EncodeTextData(string text, bool isMasked)
        {
            byte[] originalDataBytes = Encoding.UTF8.GetBytes(text);
            return encodeData(originalDataBytes, isMasked, WebSocketFrameOpCode.TEXT);
        }

        public IEnumerable<WebSocketFrame> EncodeBinData(IEnumerable<byte> data, bool isMasked)
        {
            return encodeData(data, isMasked, WebSocketFrameOpCode.BIN);
        }

        private IEnumerable<WebSocketFrame> encodeData(IEnumerable<byte> data, bool isMasked, WebSocketFrameOpCode opCode)
        {
            List<WebSocketFrame> encodedFrames = new();
            int offset = 0;
            while(offset != -1)
            {
                encodedFrames.Add(encodeFrame(data, isMasked, opCode, ref offset));
                opCode = WebSocketFrameOpCode.CONTINIOUS;
            }
            return encodedFrames;
        }

        private WebSocketFrame encodeFrame(IEnumerable<byte> data, bool isMasked, WebSocketFrameOpCode opCode, ref int offset)
        {
            List<byte> dataToEncode = data.Skip(offset).ToList();
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = true;
            if (dataToEncode.Count > WebSocketLimits.MAX_LEN_127)
            {
                offset += WebSocketLimits.MAX_LEN_127;
                dataToEncode = dataToEncode.Take(WebSocketLimits.MAX_LEN_127).ToList();
                frame.IsFinal = false;
            }
            else
            {
                offset = -1;
            }
            frame.OpCode = opCode;
            if (isMasked) frame.Mask = createMask();
            frame.Length = dataToEncode.Count;
            frame.OriginalBytes = dataToEncode.ToArray();
            return frame;
        }

        public IEnumerable<string> DecodeTextData(IEnumerable<WebSocketFrame> textFrames)
        {
            List<string> texts = new List<string>();
            StringBuilder stringBuilder = new StringBuilder();
            for(int i = 0; i < textFrames.Count(); i++)
            {
                if (textFrames.ElementAt(i).OpCode != WebSocketFrameOpCode.TEXT) continue;
                stringBuilder.Append(Encoding.UTF8.GetString(textFrames.ElementAt(i).OriginalBytes));
                if (textFrames.ElementAt(i).IsFinal)
                {
                    texts.Add(stringBuilder.ToString());
                    stringBuilder = new();
                }
            }
            return texts;
        }

        public IEnumerable<IEnumerable<byte>> DecodeBinData(IEnumerable<WebSocketFrame> binFrames)
        {
            List<IEnumerable<byte>> binData = new List<IEnumerable<byte>>();
            List<byte> binSequence = new();
            for(int i = 0; i < binFrames.Count(); i++)
            {
                if (binFrames.ElementAt(i).OpCode != WebSocketFrameOpCode.BIN) continue;
                binSequence.AddRange(binFrames.ElementAt(i).OriginalBytes);
                if(binFrames.ElementAt(i).IsFinal)
                {
                    binData.Add(binSequence);
                    binSequence = new();
                }
            }
            return binData;
        }

        private int createMask()
        {
            return _random_.Next();
        }
    }

    public enum WebSocketFrameType
    {
        TEXT_DATA   = 0,
        BIN_DATA    = 1,
        CONTROL     = 2,
    }

    public enum WebSocketFrameOpCode
    {
        CONTINIOUS  = 0x00,
        TEXT        = 0x01,
        BIN         = 0x02,
        CLOSE       = 0x08,
        PING        = 0x09,
        PONG        = 0x0A,
    }

    public class WebSocketFrame
    {
        private byte[] originalBytes = null;
        private byte[] maskedBytes = null;
        private WebSocketFrameOpCode opCode;
        private int length;
        public bool IsFinal { get; set; }
        public bool ReservedBit1 { get; set; } = false;
        public bool ReservedBit2 { get; set; } = false;
        public bool ReservedBit3 { get; set; } = false;
        public WebSocketFrameOpCode OpCode
        {
            get => opCode;
            set
            {
                if (value < (byte)0x00 || (byte)value > 0x0F) throw new InvalidOperationException();
                opCode = value;
            }
        }
        public bool IsMasked => Mask is not null;
        public int Length
        {
            get => length;
            set
            {
                if (value < 0 || value > WebSocketLimits.MAX_LEN_127) throw new InvalidOperationException();
                length = value;
            }
        }
        public int? Mask { get; set; }

        public byte LengthByte => getLengthByte();
        public bool IsLengthExtended => Length > WebSocketLimits.MAX_LEN_125;
        public byte[] ExtendedLengthBytes => getExtendedLengthBytes();
        public byte[] MaskBytes => getMaskBytes();

        private byte getLengthByte()
        {
            if(Length <= WebSocketLimits.MAX_LEN_125)
            {
                return (byte)Length;
            }
            else if(Length <= WebSocketLimits.MAX_LEN_126)
            {
                return 126;
            }
            else
            {
                return 127;
            }
        }

        private byte[] getExtendedLengthBytes()
        {
            if (Length < WebSocketLimits.MAX_LEN_125) return null;
            if (Length < WebSocketLimits.MAX_LEN_126)
            {
                byte[] buffer126 = new byte[2];
                for(int i = 0; i < buffer126.Length; i++)
                {
                    buffer126[i] |= (byte)((Length & (0xFF << (8 * (1 - (i % 2))))) >> (8 * (1 - (i % 2))));
                }
                return buffer126;
            }
            byte[] buffer127 = new byte[8];
            for(int i = 0; i < buffer127.Length; i++)
            {
                buffer127[i] |= (byte)(((long)Length & (0xFF << (8 * (7 - (i % 8))))) >> (8 * (7 - (i % 8))));
            }
            return buffer127;
        }

        private byte[] getMaskBytes()
        {
            if (!IsMasked) return null;
            byte[] buffer = new byte[4];
            for(int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)((Mask & (0xFF << (8 * (3 - (i % 4))))) >> (8 * (3 - (i % 4))));
            }
            return buffer;
        }

        public byte[] MaskedBytes
        {
            get => maskedBytes;
            set => setMaskedBytes(value);
        }

        public byte[] OriginalBytes
        {
            get => originalBytes;
            set => setOriginalBytes(value);
        }

        public byte[] FrameBytes => getFrameBytes();

        private void setMaskedBytes(byte[] newBytes)
        {
            if (newBytes.Length != Length) throw new InvalidOperationException($"Uncompatible lengths: {newBytes.Length} != {Length}");
            maskedBytes = new byte[Length];
            newBytes.CopyTo(maskedBytes, 0);
            if(IsMasked)
            {
                originalBytes = applyMask(maskedBytes, MaskBytes);
            }
            else
            {
                originalBytes = new byte[maskedBytes.Length];
                maskedBytes.CopyTo(originalBytes, 0);
            }
        }

        private void setOriginalBytes(byte[] newBytes)
        {
            if (newBytes.Length != Length) throw new InvalidOperationException("Uncompatible lengths");
            originalBytes = new byte[newBytes.Length];
            newBytes.CopyTo(originalBytes, 0);
            if(IsMasked)
            {
                maskedBytes = applyMask(originalBytes, MaskBytes);
            }
            else
            {
                maskedBytes = new byte[originalBytes.Length];
                originalBytes.CopyTo(maskedBytes, 0);
            }
        }

        private byte[] applyMask(byte[] data, byte[] maskBytes)
        {
            if (maskBytes.Length != 4) throw new InvalidOperationException();
            byte[] maskedData = new byte[data.Length];
            for(int i = 0; i < data.Length; i++)
            {
                maskedData[i] = (byte)(data[i] ^ maskBytes[i % 4]);
            }
            return maskedData;
        }

        private byte[] getFrameBytes()
        {
            byte fByte = (byte)((IsFinal ? 0x80 : 0) | (ReservedBit1 ? 0x40 : 0) | (ReservedBit2 ? 0x20 : 0) | (ReservedBit1 ? 0x10 : 0) | (byte)OpCode);
            byte sByte = (byte)((IsMasked ? 0x80 : 0) | LengthByte);
            List<byte> frameBytesList = new List<byte>();
            frameBytesList.Add(fByte);
            frameBytesList.Add(sByte);
            if(IsLengthExtended) frameBytesList.AddRange(ExtendedLengthBytes);
            if(IsMasked) frameBytesList.AddRange(MaskBytes);
            frameBytesList.AddRange(MaskedBytes ?? new byte[0]);
            return frameBytesList.ToArray();
        }
    }
}

