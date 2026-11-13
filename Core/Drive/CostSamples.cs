using System;

namespace DrivenRides.Core.Drive
{
    public sealed class CostSamples
    {
        public const int Capacity = 32768;
        private readonly float[] values = new float[Capacity];
        private readonly float[] sorted = new float[Capacity];
        private int next;

        public int Total { get; private set; }
        public int Count => Math.Min(Total, Capacity);
        public float Max { get; private set; }

        public void Add(float value)
        {
            values[next] = value;
            next = (next + 1) % Capacity;
            Total++;
            if (Total == 1 || value > Max) Max = value;
        }

        public void Clear() { next = 0; Total = 0; Max = 0f; }

        public float Percentile(float p)
        {
            var n = Count;
            if (n == 0) return 0f;
            Array.Copy(values, sorted, n);
            Array.Sort(sorted, 0, n);
            var rank = (int)Math.Ceiling(p / 100f * n) - 1;
            return sorted[Math.Max(0, Math.Min(n - 1, rank))];
        }
    }
}
