using System;
using System.Security.Cryptography;
using System.Text;

namespace System.Runtime.CompilerServices
{
#if !NET5_0_OR_GREATER
    internal static class IsExternalInit { }
#endif
}

namespace ParrotBoost
{
    internal static class Compatibility
    {
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

#if !NET5_0_OR_GREATER
        public static string ToHexString(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        public static byte[] HashData(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(data);
            }
        }
#endif
    }
}
