using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;

namespace DrivenRides.Core.Parking
{
    // Bounded hybrid search in pose space. Expansions are car-model rollouts, swept at <= 0.1 m, including
    // steering slew. Forward and reverse are equally available; changing direction costs more than driving on.
    // Step has an expansion budget so the game can spread the work over frames.
    public sealed class ManoeuvrePlanner
    {
        private sealed class Node
        {
            public CarPose Pose;
            public int Direction, Parent;
            public float Cost, Rank;
            public List<ManoeuvrePoint> Trace = new List<ManoeuvrePoint>();
        }
        private readonly CarSpec car;
        private readonly ManoeuvreSpace space;
        private CarPose goal;
        private readonly CarPose finalGoal, initialStart;
        private readonly bool deferredHeading;
        private ManoeuvrePlanner? fallback;
        private readonly List<ManoeuvrePoint> prefix = new List<ManoeuvrePoint>();
        private bool staging;
        private readonly bool roadEntry;
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<int> heap = new List<int>();
        private readonly Dictionary<(int, int, int, int, int), float> seen = new Dictionary<(int, int, int, int, int), float>();
        public ManoeuvrePlan? Result { get; private set; }
        public bool Done { get; private set; }
        public int Expanded { get; private set; }
        public string Reason { get; private set; } = "";
        public const int MaxNodes = 40000, MaxExpanded = 18000;
        public const float PositionTolerance = 0.18f, HeadingTolerance = 0.03f;
        private const float Speed = 0.8f, Dt = 0.1f;

        public ManoeuvrePlanner(CarSpec car, ManoeuvreSpace space, CarPose start, CarPose goal, bool stageForParking = false, bool roadEntry = false)
            : this(car,space,start,goal,stageForParking,roadEntry,false) { }

        private ManoeuvrePlanner(CarSpec car, ManoeuvreSpace space, CarPose start, CarPose goal,
            bool stageForParking, bool roadEntry, bool deferredHeading)
        {
            this.car = car; this.space = space; this.goal = finalGoal = goal; initialStart = start;
            this.roadEntry = roadEntry; this.deferredHeading = deferredHeading;
            space.PrepareObstacles();
            if (!space.Clear(car, start) || !space.Clear(car, goal)) { Finish("start or target is obstructed"); return; }
            var dx = start.X - goal.X; var dz = start.Z - goal.Z;
            var s = (float)Math.Sin(goal.Heading); var c = (float)Math.Cos(goal.Heading);
            var ahead = dx * s + dz * c; var side = dx * c - dz * s;
            // Beside a space, first pull forward along the current heading. This local staging pose avoids
            // spending the bounded search on sideways shuffles and still checks the entire approach for obstacles.
            if (stageForParking && ahead >= -8f && ahead < car.FrontAhead + 3f && Math.Abs(side) > 1f
                && Math.Abs(ManoeuvrePlan.Angle(start.Heading - goal.Heading)) < 0.2f)
            {
                var advance = car.FrontAhead + 3f - ahead;
                this.goal = new CarPose(start.X + s * advance, start.Z + c * advance, goal.Heading);
                staging = true;
                if (!space.Clear(car, this.goal)) { Finish("no clear staging pose beside the space"); return; }
            }
            // Leave a perpendicular bay along its axis before searching the turn into the aisle. This avoids
            // spending the pose-space budget steering between adjacent parked cars. Only use a fully clear
            // straight staging leg; an obstructed leg leaves the general search available.
            if (roadEntry && ManoeuvrePlan.Distance(start, goal) > 8f
                && Math.Abs(ManoeuvrePlan.Angle(start.Heading - goal.Heading)) > 0.6f)
            {
                var sx = (float)Math.Sin(start.Heading); var sz = (float)Math.Cos(start.Heading);
                var direction = (goal.X - start.X) * sx + (goal.Z - start.Z) * sz >= 0 ? 1 : -1;
                var advance = Math.Min(car.Length + 1f, ManoeuvrePlan.Distance(start, goal) * 0.5f);
                var safe = true;
                for (var travel = 0.1f; travel <= advance + 0.1f; travel += 0.1f)
                    if (!space.Clear(car, new CarPose(start.X + sx * direction * Math.Min(travel, advance),
                        start.Z + sz * direction * Math.Min(travel, advance), start.Heading))) { safe = false; break; }
                if (safe)
                {
                    this.goal = new CarPose(start.X + sx * direction * advance, start.Z + sz * direction * advance, start.Heading);
                    staging = true;
                }
            }
            Add(new Node { Pose = start, Parent = -1, Rank = Estimate(start) });
        }

        public void Step(int expansions = 32)
        {
            if (fallback != null)
            {
                var before = fallback.Expanded; fallback.Step(expansions); Expanded += fallback.Expanded - before;
                Result = fallback.Result; Done = fallback.Done; Reason = fallback.Reason;
                return;
            }
            for (var n = 0; n < expansions && !Done; n++)
            {
                if (heap.Count == 0 || Expanded >= MaxExpanded || nodes.Count >= MaxNodes) {
                    if (roadEntry && !deferredHeading)
                    {
                        nodes.Clear(); heap.Clear(); seen.Clear(); prefix.Clear();
                        fallback = new ManoeuvrePlanner(car,space,initialStart,finalGoal,false,true,true);
                    }
                    else Finish("no manoeuvre within the search budget");
                    break;
                }
                var index = Pop(); var node = nodes[index]; Expanded++;
                if (AtGoal(node.Pose))
                {
                    var chain = new List<int>();
                    for (var at = index; at >= 0; at = nodes[at].Parent) chain.Add(at);
                    chain.Reverse();
                    var points = new List<ManoeuvrePoint> { new ManoeuvrePoint(nodes[0].Pose, chain.Count > 1 ? nodes[chain[1]].Direction : 1) };
                    foreach (var at in chain) points.AddRange(nodes[at].Trace);
                    if (staging)
                    {
                        prefix.AddRange(points);
                        staging = false; goal = finalGoal;
                        nodes.Clear(); heap.Clear(); seen.Clear();
                        Add(new Node { Pose = node.Pose, Parent = -1, Rank = Estimate(node.Pose) });
                        continue;
                    }
                    if (prefix.Count > 0) { prefix.AddRange(points.GetRange(1, points.Count - 1)); points = prefix; }
                    Result = new ManoeuvrePlan(points, finalGoal, roadEntry); Done = true; break;
                }
                foreach (var direction in new[] { 1, -1 })
                    for (var steering = -2; steering <= 2; steering++) Expand(index, node, direction, steering * 0.5f);
            }
        }

        private void Expand(int parent, Node from, int direction, float steering)
        {
            if (nodes.Count >= MaxNodes) return;
            var model = car.Model(from.Pose.X, from.Pose.Z, from.Pose.Heading, direction * Speed);
            model.SetWheel(from.Pose.Wheel);
            var trace = new List<ManoeuvrePoint>();
            var wheel = steering * car.LockAt(Speed);
            // Shorter steps near the goal allow accurate final placement without snapping the car.
            var count = ManoeuvrePlan.Distance(from.Pose, goal) < 2f ? 5 : 12;
            for (var i = 0; i < count; i++)
            {
                model.Step(new DriveOutput { Desired = direction * Speed, SteerDegrees = wheel }, Dt);
                var p = new CarPose((float)model.X, (float)model.Z, ManoeuvrePlan.Angle((float)model.Heading), 0, (float)model.Steer);
                if (!space.Clear(car, p))
                {
                    // A tight space can allow a short shunt but not the whole 0.96 m primitive. Keep its
                    // checked prefix so the next leg can change steering/direction before the obstacle.
                    // Two samples avoid expanding almost-identical poses from a single clearance sliver.
                    if (trace.Count < 2) return;
                    break;
                }
                trace.Add(new ManoeuvrePoint(p, direction));
                // A rollout can pass the target before its last sample. Keep that clear, aligned pose rather
                // than driving past it and forcing the search to find another short forward/reverse shuffle.
                if (AtGoal(p)) break;
            }
            var pose = trace[trace.Count - 1].Pose;
            var cost = from.Cost + trace.Count * Dt * Speed * (direction < 0 ? 1.15f : 1f)
                + (from.Direction != 0 && from.Direction != direction ? 2.5f : 0f) + Math.Abs(wheel - from.Pose.Wheel) * 0.008f;
            var key = ((int)Math.Round(pose.X / 0.25f), (int)Math.Round(pose.Z / 0.25f),
                (int)Math.Round(pose.Heading / 0.07f), direction, (int)Math.Round(pose.Wheel / 15f));
            if (seen.TryGetValue(key, out var previous) && previous <= cost) return;
            seen[key] = cost;
            Add(new Node { Pose = pose, Direction = direction, Cost = cost, Rank = cost + 1.8f * Estimate(pose), Parent = parent, Trace = trace });
        }
        private bool AtGoal(CarPose pose) => ManoeuvrePlan.Distance(pose, goal) <= (roadEntry ? 0.3f : PositionTolerance)
            && Math.Abs(ManoeuvrePlan.Angle(pose.Heading - goal.Heading)) <= (roadEntry ? 0.06f : HeadingTolerance) && Math.Abs(pose.Wheel) < 5f;
        // A second bounded attempt lets the driveway guide control long departures. Matching the final road heading too early traps a
        // nose-in car behind neighbours instead of allowing it to reverse into the aisle first.
        private float Estimate(CarPose p) => Math.Max(ManoeuvrePlan.Distance(p, goal), staging ? 0 : space.Estimate?.Invoke(p) ?? 0)
            + 3f * (deferredHeading && !staging ? Math.Max(0,1-ManoeuvrePlan.Distance(p,goal)/15f) : 1) * Math.Abs(ManoeuvrePlan.Angle(p.Heading - goal.Heading));
        private void Finish(string reason) { Reason = reason; Done = true; }
        private void Add(Node node)
        {
            var id = nodes.Count; nodes.Add(node); heap.Add(id); var i = heap.Count - 1;
            while (i > 0) { var parent = (i - 1) / 2; if (nodes[heap[parent]].Rank <= node.Rank) break; heap[i] = heap[parent]; i = parent; }
            heap[i] = id;
        }
        private int Pop()
        {
            var first = heap[0]; var last = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return first;
            var i = 0;
            while (i * 2 + 1 < heap.Count)
            {
                var child = i * 2 + 1;
                if (child + 1 < heap.Count && nodes[heap[child + 1]].Rank < nodes[heap[child]].Rank) child++;
                if (nodes[last].Rank <= nodes[heap[child]].Rank) break;
                heap[i] = heap[child]; i = child;
            }
            heap[i] = last; return first;
        }
    }
}
