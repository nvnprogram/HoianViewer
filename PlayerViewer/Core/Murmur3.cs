using System.Text;

namespace PlayerViewer.Core
{
    /// <summary>
    /// MurmurHash3 x86 32 bit, seed 0, of a name's UTF-8 bytes: how Raiders tables store enum
    /// values and hash-keyed BYML nodes their keys.
    /// </summary>
    public static class Murmur3
    {
        public static uint Hash(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            const uint c1 = 0xcc9e2d51,
                c2 = 0x1b873593;
            uint h = 0;
            int blocks = data.Length / 4;
            for (int i = 0; i < blocks; i++)
            {
                uint k = (uint)(
                    data[i * 4] | data[i * 4 + 1] << 8 | data[i * 4 + 2] << 16 | data[i * 4 + 3] << 24
                );
                k *= c1;
                k = (k << 15) | (k >> 17);
                k *= c2;
                h ^= k;
                h = (h << 13) | (h >> 19);
                h = h * 5 + 0xe6546b64;
            }
            uint tail = 0;
            int rest = data.Length & 3;
            int at = blocks * 4;
            if (rest >= 3)
                tail ^= (uint)data[at + 2] << 16;
            if (rest >= 2)
                tail ^= (uint)data[at + 1] << 8;
            if (rest >= 1)
            {
                tail ^= data[at];
                tail *= c1;
                tail = (tail << 15) | (tail >> 17);
                tail *= c2;
                h ^= tail;
            }
            h ^= (uint)data.Length;
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }
    }
}
