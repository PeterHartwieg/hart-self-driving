using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DrivenRides.Core.Drive
{
    // Planning spread over physics steps (review round 2: a replan ran whole inside one FixedUpdate, several starts
    // of up to eight rounds of route search and simulation): Step runs it for a time budget and says whether it
    // is done. The car drives on meanwhile, or holds short of a step known bad (HoldPosition).
    public sealed class WayOnJob
    {
        private readonly IEnumerator<object?> work;
        private readonly Func<double> clock;
        private double budget = double.PositiveInfinity, sliceStart;

        // clockMs: milliseconds from any start (a stopwatch's by default; tests give their own).
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

        // "replanned" (a step blocked on the way) or "ahead" (the look-ahead), and the path position the car's
        // front had reached when the job began, on the path it drove then.
        public readonly string Kind;
        public readonly int Position;
        public bool Done { get; private set; }
        // The way on: its checked plan, its route, a note; null if there is none (or, for a look-ahead, none needed).
        public (DrivePlan Plan, List<int> Route, string Note)? Found { get; set; }
        // The look-ahead found something changed ahead, whether or not a way on was found.
        public bool Changed { get; set; }
        // The first step ahead the car must not enter (a path position of the path it drove when the job began),
        // and why; -1 for none. Known before the way on is planned, so the car can hold short meanwhile.
        public int HoldPosition { get; set; } = -1;
        public string HoldWhat { get; set; } = "";
        public string Note { get; set; } = "";
        public double Milliseconds { get; private set; }
        // The longest slice so far, ms: a slice ends at the first yield past the budget.
        public double LongestSlice { get; private set; }
        public int Slices { get; private set; }
        public bool OverBudget => clock() - sliceStart >= budget;

        public bool Step(double budgetMs)
        {
            budget = budgetMs;
            sliceStart = clock();
            Slices++;
            while (!Done && !OverBudget)
                if (!work.MoveNext()) Done = true;
            var spent = clock() - sliceStart;
            Milliseconds += spent;
            LongestSlice = Math.Max(LongestSlice, spent);
            return Done;
        }
    }
}
