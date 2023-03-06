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
        private byte[] _endOfPreviousBuffer = new byte[0];

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

        public WebSocketFrame[] FromBytes(byte[] bytes)
        {
            List<WebSocketFrame> restoredFromBytes = new();
            int offset = 0;
            while(offset != -1)
            {
                WebSocketFrame singleFrame = singleFromBytes(_endOfPreviousBuffer.Concat(bytes).ToArray(), ref offset);
                if (singleFrame is not null) restoredFromBytes.Add(singleFrame);
            }
            return restoredFromBytes.ToArray();
        }

        private WebSocketFrame singleFromBytes(byte[] bytes, ref int offset)
        {
            byte[] offsettedBytes = bytes.Skip(offset).ToArray();
            int readIndex = 0;
            if (offsettedBytes.Length < 2) return null;
            try
            {
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
                        length *= (1 << 8);
                        length += offsettedBytes[readIndex++];
                    }
                }
                else if (length == 127)
                {
                    length = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        length *= (1 << 8);
                        length += offsettedBytes[readIndex++];
                    }
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
                if (offset >= bytes.Length) offset = -1;
                return frame;
            }
            catch(InvalidOperationException ex)
            {
                offset = -1;
                _endOfPreviousBuffer = offsettedBytes;
                return null;
            }
            catch
            {
                throw;
            }
        }

        public byte[] ToBytes(WebSocketFrame[] frames)
        {
            List<byte> bytes = new();
            foreach(var frame in frames)
            {
                bytes.AddRange(frame.FrameBytes);
            }
            return bytes.ToArray();
        }

        public WebSocketFrame[] EncodeTextData(string text, bool isMasked)
        {
            byte[] originalDataBytes = Encoding.UTF8.GetBytes(text);
            return encodeData(originalDataBytes, isMasked, WebSocketFrameOpCode.TEXT);
        }

        public WebSocketFrame[] EncodeBinData(byte[] data, bool isMasked)
        {
            return encodeData(data, isMasked, WebSocketFrameOpCode.BIN);
        }

        private WebSocketFrame[] encodeData(byte[] data, bool isMasked, WebSocketFrameOpCode opCode)
        {
            List<WebSocketFrame> encodedFrames = new();
            int offset = 0;
            while(offset != -1)
            {
                encodedFrames.Add(encodeFrame(data, isMasked, opCode, ref offset));
            }
            return encodedFrames.ToArray();
        }

        private WebSocketFrame encodeFrame(byte[] data, bool isMasked, WebSocketFrameOpCode opCode, ref int offset)
        {
            byte[] dataToEncode = data.Skip(offset).ToArray();
            WebSocketFrame frame = new WebSocketFrame();
            frame.IsFinal = true;
            if (dataToEncode.Length > WebSocketLimits.MAX_LEN_127)
            {
                offset += WebSocketLimits.MAX_LEN_127;
                dataToEncode = dataToEncode.Take(WebSocketLimits.MAX_LEN_127).ToArray();
                frame.IsFinal = false;
            }
            else
            {
                offset = -1;
            }
            frame.OpCode = opCode;
            if (isMasked) frame.Mask = createMask();
            frame.Length = dataToEncode.Length;
            frame.OriginalBytes = dataToEncode;
            return frame;
        }

        public string[] DecodeTextData(WebSocketFrame[] textFrames)
        {
            List<string> texts = new List<string>();
            StringBuilder stringBuilder = new StringBuilder();
            for(int i = 0; i < textFrames.Length; i++)
            {
                stringBuilder.Append(Encoding.UTF8.GetString(textFrames[i].OriginalBytes));
                if (textFrames[i].IsFinal)
                {
                    texts.Add(stringBuilder.ToString());
                    stringBuilder = new();
                }
            }
            return texts.ToArray();
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
                    buffer126[i] += (byte)((Length & (0xFF << (8 * (1 - (i % 2))))) >> (8 * (1 - (i % 2))));
                }
                return buffer126;
            }
            byte[] buffer127 = new byte[8];
            for(int i = 0; i < buffer127.Length; i++)
            {
                buffer127[i] += (byte)((Length & (0xFF << (8 * (7 - (i % 8))))) >> (8 * (7 - (i % 8))));
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

