﻿namespace MHServerEmu.Core.Collections
{
    public class GBitArray
    {
        public const int Invalid = -1;
        private const int BitsPerWord = 8 * sizeof(ulong); // bits in ulong

        protected ulong[] _bits; // ulong array of words
        private int _size; // size in words

        public int Size => _size * BitsPerWord;
        public int Bytes => _size * sizeof(ulong);

        public GBitArray()
        {
            Free();
        }

        public bool this[int index]
        {
            get => Get(index);
            set => Set(index, value);
        }

        public ulong Test(int index)
        {
            if (index >= Size)
                return 0;
            return _bits[index / BitsPerWord] & (1UL << (index % BitsPerWord));
        }

        public bool Get(int index)
        {
            return Test(index) != 0;
        }

        public void Set(int index, bool value)
        {
            if (value) Set(index);
            else Reset(index);
        }

        public void Reset(int index)
        {
            Expand(index);
            _bits[index / BitsPerWord] &= ~(1UL << (index % BitsPerWord));
        }

        public void Set(int index)
        {
            Expand(index);
            _bits[index / BitsPerWord] |= (1UL << (index % BitsPerWord));
        }

        private void Expand(int index)
        {
            if (index >= Size)
            {
                int newSize = (int)Math.Max(NextPowerOfTwo((ulong)index / BitsPerWord + 1), sizeof(ulong));
                Reserve(newSize * BitsPerWord);
            }
        }

        private static ulong NextPowerOfTwo(ulong n)
        {
            n--;
            n |= n >> 1;
            n |= n >> 2;
            n |= n >> 4;
            n |= n >> 8;
            n |= n >> 16;
            n |= n >> 32;
            return ++n;
        }

        public bool Any()
        {
            for (int i = 0; i < _size; i++)
                if (_bits[i] != 0) return true;
            return false;
        }

        public void Clear()
        {
            for (int i = 0; i < _size; i++)
                _bits[i] = 0;
        }

        public T Copy<T>() where T : GBitArray, new()
        {
            T result = new (); 
            result.Reserve(Size);
            for (int i = 0; i < _size; i++)
                result._bits[i] = _bits[i];
            return result;
        }

        private void Free()
        {
            _bits = null;
            _size = 0;
        }

        public void Resize(int len)
        {
            int words = (len + BitsPerWord - 1) / BitsPerWord;
            if (_size != words)
            {
                if (words > 0)
                {
                    Array.Resize(ref _bits, words);
                    _size = words;
                } 
                else Free();
            }
        }

        private void Reserve(int len)
        {
            int words = (len + BitsPerWord - 1) / BitsPerWord;
            if (words > _size)
            {
                Array.Resize(ref _bits, words);
                _size = words;
            }
        }

        public int FirstUnset()
        {
            int index = ScanForwardUnset(_bits, _size, 0);
            return index < Size ? index : Invalid;
        }

        private int ScanForwardUnset(ulong[] data, int size, int startIndex)
        {
            for (int i = startIndex; i < size * BitsPerWord; i++)
                if ((data[i / BitsPerWord] & (1UL << (i % BitsPerWord))) == 0)
                    return i;

            return Invalid;
        }

        public static T And<T>(T left, T right) where T: GBitArray
        {
            left.Reserve(right.Size);
            for (int i = 0; i < right._size; i++)
                left._bits[i] &= right._bits[i];

            return left;
        }

        public static GBitArray operator &(GBitArray left, GBitArray right)
        {
            return And(left, right);
        }

        public static T Or<T>(T left, T right) where T : GBitArray
        {
            left.Reserve(right.Size);
            for (int i = 0; i < right._size; i++)
                left._bits[i] |= right._bits[i];

            return left;
        }

        public static GBitArray operator |(GBitArray left, GBitArray right)
        {
            return Or(left, right);
        }

        public static T Xor<T>(T left, T right) where T : GBitArray
        {
            left.Reserve(right.Size);
            for (int i = 0; i < right._size; i++)
                left._bits[i] ^= right._bits[i];

            return left;
        }

        public static GBitArray operator ^(GBitArray left, GBitArray right)
        {
            return Xor(left, right);
        }
    }
}
