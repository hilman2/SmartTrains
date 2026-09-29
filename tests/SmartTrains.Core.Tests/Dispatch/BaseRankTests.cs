using SmartTrains.Core.Dispatch;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class BaseRankTests
    {
        [Fact]
        public void AFullerTrainGoesBeforeAnEmptierOneOfItsKind()
        {
            Assert.True(BaseRank.Of(TrainKind.Passenger, 0.8f) > BaseRank.Of(TrainKind.Passenger, 0.2f));
            Assert.True(BaseRank.Of(TrainKind.Cargo, 0.8f) > BaseRank.Of(TrainKind.Cargo, 0.2f));
        }

        [Fact]
        public void AFullFreightTrainGoesBeforeAnEmptyPassengerTrain()
        {
            Assert.True(BaseRank.Of(TrainKind.Cargo, 1f) > BaseRank.Of(TrainKind.Passenger, 0f));
        }

        [Fact]
        public void OfTwoTrainsAsFullAPassengerTrainGoesFirst()
        {
            Assert.True(BaseRank.Of(TrainKind.Passenger, 0.5f) > BaseRank.Of(TrainKind.Cargo, 0.5f));
        }

        [Theory]
        [InlineData(TrainKind.ThroughPassenger)]
        [InlineData(TrainKind.ThroughCargo)]
        [InlineData(TrainKind.Returning)]
        public void WhatTheCityDoesNotUseGainsNothingFromItsLoad(TrainKind kind)
        {
            // Through traffic runs between outside connections; a train on
            // its way back to the depot carries nobody.
            Assert.Equal(BaseRank.Of(kind, 0f), BaseRank.Of(kind, 1f));
        }

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(-0.5f, 0f)]
        [InlineData(3f, 1f)]
        public void TheFillCountsFromEmptyToFullOnly(float fill, float counted)
        {
            // A train without capacity has no fill: 0 by 0 is NaN.
            Assert.Equal(BaseRank.Of(TrainKind.Cargo, counted), BaseRank.Of(TrainKind.Cargo, fill));
        }
    }
}
