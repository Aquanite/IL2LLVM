using System.Globalization;
using System.Numerics;
using Mono.Cecil;

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

        public static string GetBiggerType(string a, string b, out byte biggerOne)
        {
            if (a == b)
            {
                biggerOne = 0;
                return a;
            }

            if (a[0] != b[0])
                throw new InvalidDataException($"Cannot compare types '{a}' and '{b}' as they are of different categories.");

            char typeCategory = a[0];
            if (typeCategory == 'i')
            {
                int aBits = int.Parse(a.Substring(1));
                int bBits = int.Parse(b.Substring(1));

                if (aBits > bBits)
                {
                    biggerOne = 1;
                    return a;
                }
                else
                {
                    biggerOne = 2;
                    return b;
                }
            }
            else if (typeCategory == 'f')
            {
                int aBits = int.Parse(a.Substring(1));
                int bBits = int.Parse(b.Substring(1));

                if (aBits > bBits)
                {
                    biggerOne = 1;
                    return a;
                }
                else
                {
                    biggerOne = 2;
                    return b;
                }
            }
            else
            {
                throw new InvalidDataException($"Unsupported type category '{typeCategory}' for comparison.");
            }
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

        public static string GetLLVMConvention(MethodCallingConvention conv)
        {
            return conv switch
            {
                MethodCallingConvention.Default => "",
                MethodCallingConvention.C => "ccc",
                MethodCallingConvention.StdCall => "x86_stdcallcc",
                MethodCallingConvention.ThisCall => "x86_thiscallcc",
                MethodCallingConvention.FastCall => "fastcc",
                MethodCallingConvention.VarArg => "",
                _ => throw new NotSupportedException($"Unsupported calling convention: {conv}")
            };
        }
    }
}