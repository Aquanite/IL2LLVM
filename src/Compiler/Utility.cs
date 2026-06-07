using System.Globalization;
using System.Numerics;

namespace IL2LLVM.Compiler
{
    public static class Utility
    {
        public static string GetNativeZero(string type)
        {
            if (string.IsNullOrEmpty(type))
                throw new InvalidDataException("Invalid data provided to GetNativeZero(): (null or empty)");

            char first = type[0];
            return first switch
            {
                'i' => "0",
                'f' => "0",
                'p' => "null",
                _   => throw new InvalidDataException($"Invalid data provided to GetNativeZero(): {type}")
            };
        }

        public static string GetNativeFalse(string type)
        {
            if (string.IsNullOrEmpty(type))
                throw new InvalidDataException("Invalid data provided to GetNativeFalse(): (null or empty)");

            char first = type[0];
            return first switch
            {
                'i' => "0",
                'f' => "false",
                'p' => "null",
                _   => throw new InvalidDataException($"Invalid data provided to GetNativeFalse(): {type}")
            };
        }

        public static string ToHex(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "00000000";
                
            input = input.Trim();
            if (input.Contains('.'))
            {
                if (float.TryParse(input, CultureInfo.InvariantCulture, out float f))
                {
                    return BitConverter.SingleToInt32Bits(f).ToString("X8");
                }
                if (double.TryParse(input, CultureInfo.InvariantCulture, out double d))
                {
                    return BitConverter.DoubleToInt64Bits(d).ToString("X16");
                }
            }

            if (int.TryParse(input, CultureInfo.InvariantCulture, out int i))
            {
                return i.ToString("X8");
            }
            if (long.TryParse(input, CultureInfo.InvariantCulture, out long l))
            {
                return l.ToString("X16"); 
            }

            return input; // var or sum
        }
        public static uint PowerOf8(uint value)
        {
            if (value <= 1) return 1;

            int leadingZeros = BitOperations.LeadingZeroCount(value - 1);
            int nextPowerOf2Exponent = 32 - leadingZeros;

            int nextPowerOf8Exponent = (nextPowerOf2Exponent + 2) / 3;

            return 1U << (nextPowerOf8Exponent * 3);
        }
    }
}