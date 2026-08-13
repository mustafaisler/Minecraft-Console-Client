using System;
using System.Collections.Generic;

namespace MinecraftClient.Protocol.Handlers
{
    /// <summary>
    /// Java DataInput/DataOutput Modified UTF-8 codec used by NBT strings.
    /// Supplementary characters are encoded as their two UTF-16 surrogate code units.
    /// </summary>
    internal static class MinecraftModifiedUtf8
    {
        public static string Decode(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var chars = new List<char>(bytes.Length);

            for (int index = 0; index < bytes.Length;)
            {
                int first = bytes[index];
                if ((first & 0x80) == 0)
                {
                    chars.Add((char)first);
                    index++;
                    continue;
                }

                if ((first & 0xE0) == 0xC0 && HasContinuation(bytes, index, 1))
                {
                    int value = ((first & 0x1F) << 6) | (bytes[index + 1] & 0x3F);
                    if (value == 0 || value >= 0x80)
                    {
                        chars.Add((char)value);
                        index += 2;
                        continue;
                    }
                }
                else if ((first & 0xF0) == 0xE0 && HasContinuation(bytes, index, 2))
                {
                    int value = ((first & 0x0F) << 12)
                        | ((bytes[index + 1] & 0x3F) << 6)
                        | (bytes[index + 2] & 0x3F);
                    if (value >= 0x800)
                    {
                        chars.Add((char)value);
                        index += 3;
                        continue;
                    }
                }
                else if ((first & 0xF8) == 0xF0 && HasContinuation(bytes, index, 3))
                {
                    int scalar = ((first & 0x07) << 18)
                        | ((bytes[index + 1] & 0x3F) << 12)
                        | ((bytes[index + 2] & 0x3F) << 6)
                        | (bytes[index + 3] & 0x3F);
                    if (scalar is >= 0x10000 and <= 0x10FFFF)
                    {
                        scalar -= 0x10000;
                        chars.Add((char)(0xD800 + (scalar >> 10)));
                        chars.Add((char)(0xDC00 + (scalar & 0x3FF)));
                        index += 4;
                        continue;
                    }
                }

                chars.Add('\uFFFD');
                index++;
            }

            return new string(chars.ToArray());
        }

        public static byte[] Encode(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = new List<byte>(value.Length * 3);
            foreach (char character in value)
            {
                int codeUnit = character;
                if (codeUnit is >= 1 and <= 0x7F)
                {
                    bytes.Add((byte)codeUnit);
                }
                else if (codeUnit <= 0x7FF)
                {
                    bytes.Add((byte)(0xC0 | (codeUnit >> 6)));
                    bytes.Add((byte)(0x80 | (codeUnit & 0x3F)));
                }
                else
                {
                    bytes.Add((byte)(0xE0 | (codeUnit >> 12)));
                    bytes.Add((byte)(0x80 | ((codeUnit >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (codeUnit & 0x3F)));
                }
            }
            return bytes.ToArray();
        }

        private static bool HasContinuation(byte[] bytes, int index, int count)
        {
            if (index + count >= bytes.Length)
                return false;
            for (int offset = 1; offset <= count; offset++)
            {
                if ((bytes[index + offset] & 0xC0) != 0x80)
                    return false;
            }
            return true;
        }
    }
}
