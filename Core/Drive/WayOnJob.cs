using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DrivenRides.Core.Drive
{
    public sealed class WayOnJob
    {
        private readonly IEnumerator<object?> work;
        private readonly Func<double> clock;
        private double budget = double.PositiveInfinity, sliceStart;

        public WayOnJob(string kind, int position, Func<WayOnJob, IEnumerable<object?>> body, Func<double>? clockMs = null)
        {
            Kind = kind;
            Position = position;
            if (clockMs == null)
            {
                var watch = Stopwatch.StartNew();
                clockMs = () => watch.Elapsed.TotalMilliseconds;
            }
            clock = clockMs;
            work = body(this).GetEnumerator();
        }

        public readonly string Kind;
        public readonly int Position;
        public bool Done { get; private set; }
        public (DrivePlan Plan, List<int> Route, string Note)? Found { get; set; }
        public bool Changed { get; set; }
        public int HoldPosition { get; set; } = -1;
        public float HoldOffset { get; set; }
        public string HoldWhat { get; set; } = "";
        public CarPose? JoinedFrom { get; set; }
        public string Note { get; set; } = "";
        public double Milliseconds { get; private set; }
        public double LongestSlice { get; private set; }
        public int Slices { get; private set; }
        public static readonly object NextStep = new object();

        public bool OverBudget => clock() - sliceStart >= budget;

        public bool Step(double budgetMs)
        {
            budget = budgetMs;
            sliceStart = clock();
            Slices++;
            while (!Done && !OverBudget)
            {
                if (!work.MoveNext()) Done = true;
                else if (ReferenceEquals(work.Current, NextStep)) break;
            }
            var spent = clock() - sliceStart;
            Milliseconds += spent;
            LongestSlice = Math.Max(LongestSlice, spent);
            return Done;
        }
    }
}
