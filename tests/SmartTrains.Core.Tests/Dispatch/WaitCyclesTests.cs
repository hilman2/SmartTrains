using System.Collections.Generic;
using System.Linq;
using SmartTrains.Core.Dispatch;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class WaitCyclesTests
    {
        private static Dictionary<long, long> Waits(params (long, long)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

        [Fact]
        public void AQueueIsNoCircle()
        {
            Assert.Empty(WaitCycles.Find(Waits((1, 2), (2, 3), (3, 4))));
        }

        [Fact]
        public void ACircleIsFoundOnceWhereverTheWalkStarts()
        {
            List<List<long>> cycles = WaitCycles.Find(Waits((1, 2), (2, 3), (3, 1)));
            List<long> cycle = Assert.Single(cycles);
            Assert.Equal(new long[] { 1, 2, 3 }, cycle.OrderBy(x => x));
        }

        [Fact]
        public void TrainsQueuedBehindACircleAreNotPartOfIt()
        {
            // 5 and 6 wait behind the circle 2-3-4; they are stuck, but
            // breaking the circle frees them, so only 2, 3 and 4 count.
            List<List<long>> cycles = WaitCycles.Find(Waits((5, 6), (6, 2), (2, 3), (3, 4), (4, 2)));
            Assert.Equal(new long[] { 2, 3, 4 }, Assert.Single(cycles).OrderBy(x => x));
        }

        [Fact]
        public void TwoSeparateCirclesAreBothFound()
        {
            List<List<long>> cycles = WaitCycles.Find(Waits((1, 2), (2, 1), (7, 8), (8, 9), (9, 7), (10, 7)));
            Assert.Equal(2, cycles.Count);
        }

        [Fact]
        public void TwoTrainsWaitingForEachOtherAreACircle()
        {
            Assert.Single(WaitCycles.Find(Waits((1, 2), (2, 1))));
        }
    }
}
