using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AuroraProxy
{
    public static class WebSocketFrameDecoder
    {
        private static readonly Random __random__ = new Random();
        private static readonly int __maxLen126__ = (int)Math.Pow(2, 16);
        private static readonly int __maxLen127__ = (int)Math.Pow(2, 30);
        public static string[] Decode(byte[] message)
        {
            List<string> decodedFrames = new List<string>();
            int offset = 0;
            while(offset != -1)
            {
                string decodedMessage = decodeSingleFrame(message, ref offset);
                if(decodedMessage is not null)
                    decodedFrames.Add(decodedMessage);
            }
            return decodedFrames.ToArray();
        }

        private static string decodeSingleFrame(byte[] fullMessage, ref int offset)
        {
            byte[] message = fullMessage.Skip(offset).ToArray();
            if (message.Length < 2) return null;
            bool FIN = (message[0] & 0B10000000) != 0;
            bool RSV1 = (message[0] & 0B01000000) != 0;
            bool RSV2 = (message[0] & 0B00100000) != 0;
            bool RSV3 = (message[0] & 0B00010000) != 0;
            byte opCode = (byte)(message[0] & 0B00001111);
            bool isMask = (message[1] & 0B10000000) != 0;
            int readIndex = 2;
            long length = (message[1] & 0B01111111);
            if (length == 126) //2 bytes for payload length
            {
                readIndex += 2;
                length = (message[2] * (1 << 8)) + message[3];
            }
            else if (length == 127) //8 bytes for payload length
            {
                readIndex += 8;
                length = 0;
                for (int i = 2; i < 10; i++)
                {
                    length *= (1 << 8);
                    length += message[i];
                }
            }
            int Mask = 0;
            if (isMask)
            {
                for (int i = readIndex; i < readIndex + 4; i++)
                {
                    Mask *= (1 << 8);
                    Mask += message[i];
                }
                readIndex += 4;
            }
            byte[] maskedBytes = message.Skip(readIndex).Take((int)length).ToArray();
            offset += readIndex + (int)length;
            if (offset >= fullMessage.Length) offset = -1;
            byte[] originalBytes;
            if (isMask)
            {
                originalBytes = new byte[maskedBytes.Length];
                for (int i = 0; i < maskedBytes.Length; i++)
                {
                    byte maskedByte = (byte)((Mask & (0B11111111 << 8 * (3 - (i % 4)))) >> (8 * (3 - (i % 4))));
                    originalBytes[i] = (byte)(maskedBytes[i] ^ maskedByte);
                }
            }
            else
            {
                originalBytes = maskedBytes;
            }
#if DEBUG
            //Console.WriteLine("FIN: " + (FIN ? "Yes" : "No"));
            //Console.WriteLine("RSV1: " + (RSV1 ? "Yes" : "No"));
            //Console.WriteLine("RSV2: " + (RSV2 ? "Yes" : "No"));
            //Console.WriteLine("RSV3: " + (RSV3 ? "Yes" : "No"));
            //Console.WriteLine("OpCode: " + opCode);
            //Console.WriteLine("IsMask: " + (isMask ? "Yes" : "No"));
            Console.WriteLine("Length: " + length);
            //Console.WriteLine("Mask: " + Mask);
            //Console.WriteLine("Masked bytes:");
            //foreach (var b in maskedBytes)
            //{
            //    Console.Write(b + " ");
            //}
            //Console.WriteLine();
            //Console.WriteLine("Original bytes:");
            //foreach (var b in originalBytes)
            //{
            //    Console.Write(b + " ");
            //}
            //Console.WriteLine();
#endif
            if (opCode != 1) return null;
            return Encoding.UTF8.GetString(originalBytes);
        }

        public static byte[] Encode(string message, bool isMaskRequired)
        {
            List<byte> messageBytes = new List<byte>();
            int offset = 0;
            while(offset != -1)
            {
                messageBytes.AddRange(createSingleFrame(message, ref offset, isMaskRequired));
            }
            return messageBytes.ToArray();
        }

        private static int createMask()
        {
            int mask = __random__.Next();
            return mask;
        }

        private static byte createFirstByte(FrameInfo info)
        {
            byte fByte = 0;
            fByte = (byte)(fByte | (info.IsFinal ? 0B10000000 : 0));
            fByte = (byte)(fByte | 0B00000001);
            return fByte;
        }

        private static byte createSecondByte(FrameInfo info)
        {
            byte sByte = (byte)(info.Mask != -1 ? 0B10000000 : 0B00000000);
            if (info.Length < 126)
            {
                sByte = (byte)(sByte | info.Length);
            }
            else if (info.Length <= __maxLen126__)
            {
                sByte = (byte)(sByte | 126);
            }
            else
            {
                sByte = (byte)(sByte | 127);
            }
            return sByte;
        }

        private static byte[] createLengthByte(FrameInfo info)
        {
            byte[] lengthBytes;
            if(info.Length < 126)
            {
                return null;
            }
            else if(info.Length < __maxLen126__)
            {
                lengthBytes = new byte[2];
                for(int i = 0; i < 2; i++)
                {
                    lengthBytes[i] = (byte)((info.Length & (0B11111111 << (8 * (1 - (i % 4))))) >> (8 * (1 - (i % 4))));
                }
                return lengthBytes;
            }
            else
            {
                lengthBytes = new byte[8];
                for (int i = 0; i < 2; i++)
                {
                    lengthBytes[i] = (byte)((info.Length & (0B11111111 << (8 * (3 - (i % 4))))) >> (8 * (3 - (i % 4))));
                }
                return lengthBytes;
            }
        }

        private static byte[] createMaskBytes(FrameInfo info)
        {
            if (info.Mask == -1) return null;
            byte[] maskBytes = new byte[4];
            for(int i = 0; i < 4; i++)
            {
                maskBytes[i] = (byte)((info.Mask & (0B11111111 << (8 * (3 - (i % 4))))) >> (8 * (3 - (i % 4))));
            }
            return maskBytes;
        }

        public static byte[] CreatePong(byte[] request)
        {
            if ((request[0] & 0B1111) == 0x9)
            {
                FrameInfo info = new FrameInfo();
                info.Mask = createMask();
                byte[] answerBytes = new byte[request.Length + 4];
                answerBytes[0] = (byte)(request[0] & 0xF0 | 0x0A);
                answerBytes[1] = (byte)(request[1] | 0x80);
                byte[] maskBytes = createMaskBytes(info);
                maskBytes.CopyTo(answerBytes, 2);
                for(int i = 0; i < request.Length - 2; i++)
                {
                    answerBytes[i + 6] = (byte)(request[i + 2] ^ maskBytes[i % 4]);
                }
                return answerBytes;
            }
            return null;
        }

        private static byte[] createSingleFrame(string message, ref int offset, bool isMask)
        {
            string offsetMessage = message.Substring(offset);
            FrameInfo info = new FrameInfo();
            info.Mask = isMask ? createMask() : -1;
            info.IsFinal = offsetMessage.Length <= __maxLen127__;
            byte[] originalBytes = Encoding.UTF8.GetBytes(info.IsFinal ? offsetMessage : offsetMessage.Substring(__maxLen127__));
            info.Length = Math.Min(originalBytes.Length, __maxLen127__);
            byte[] headerFrameBytes;
            if(offsetMessage.Length < 126)
            {
                headerFrameBytes = new byte[2];
            }
            else if(offsetMessage.Length <= __maxLen126__)
            {
                headerFrameBytes = new byte[4];
            }
            else
            {
                headerFrameBytes = new byte[10];
            }
            headerFrameBytes[0] = createFirstByte(info);
            headerFrameBytes[1] = createSecondByte(info);
            byte[] lenBytes = createLengthByte(info);
            byte[] maskBytes = createMaskBytes(info);
            int writeIndex = 2;
            if(lenBytes is not null)
            {
                for (int i = 0; i < lenBytes.Length; i++)
                {
                    headerFrameBytes[writeIndex++] = lenBytes[i];
                }
            }
            byte[] maskedBytes = new byte[originalBytes.Length];
            if(info.Mask != -1)
            {
                for (int i = 0; i < originalBytes.Length; i++)
                {
                    byte maskByte = maskBytes[i % 4];
                    maskedBytes[i] = (byte)(originalBytes[i] ^ maskByte);
                }
            }
            else
            {
                maskedBytes = originalBytes;
            }
            
            List<byte> frameBytes = new List<byte>();
            frameBytes.AddRange(headerFrameBytes);
            if(info.Mask != -1)
            {
                frameBytes.AddRange(maskBytes);
            }
            frameBytes.AddRange(maskedBytes);
            if(info.IsFinal)
            {
                offset = -1;
            }
            else
            {
                offset += __maxLen127__ + 1;
            }
            return frameBytes.ToArray();
        }

        public static byte[] CreatePing()
        {
            FrameInfo info = new FrameInfo();
            byte[] pingBytes = new byte[6];
            pingBytes[0] = 0B10001001;
            pingBytes[1] = 0B10000000;
            info.Mask = createMask();
            byte[] maskBytes = createMaskBytes(info);
            maskBytes.CopyTo(pingBytes, 2);
            return pingBytes;
        }
    }

    class FrameInfo
    {
        public bool IsFinal;
        public int Mask;
        public int Length;
    }
}

