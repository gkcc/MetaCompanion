using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class RecommendationStatisticsTest
	{
		[TestMethod]
		public void PosteriorMean_UsesContinuousBetaShrinkage()
		{
			Assert.AreEqual(
				67.857142857,
				RecommendationStatistics.CalculatePosteriorMeanPercent(55.0, 70.0, 300.0, 50.0),
				0.000001);
			Assert.AreEqual(
				57.991967871,
				RecommendationStatistics.CalculatePosteriorMeanPercent(50.0, 60.0, 199.0, 50.0),
				0.000001);
			Assert.AreEqual(
				58.0,
				RecommendationStatistics.CalculatePosteriorMeanPercent(50.0, 60.0, 200.0, 50.0),
				0.000001);
		}

		[TestMethod]
		public void AdaptiveLocalWeight_GrowsWithEvidence()
		{
			Assert.AreEqual(0.0,
				RecommendationStatistics.CalculateAdaptiveLocalWeight(0.0, 30.0), 0.000001);
			Assert.AreEqual(0.25,
				RecommendationStatistics.CalculateAdaptiveLocalWeight(10.0, 30.0), 0.000001);
			Assert.AreEqual(0.5,
				RecommendationStatistics.CalculateAdaptiveLocalWeight(30.0, 30.0), 0.000001);
		}

		[TestMethod]
		public void KishEffectiveSampleSize_ReflectsUnequalWeights()
		{
			Assert.AreEqual(
				2.7777777778,
				RecommendationStatistics.CalculateKishEffectiveSampleSize(
					new[] { 1.0, 1.0, 0.5 }),
				0.000001);
		}

		[TestMethod]
		public void ApproximateRanking_AssignsBestProbabilityAndTiers()
		{
			var candidates = new List<RecommendationDistribution>
			{
				new RecommendationDistribution { ArchetypeId = 1, Mean = 60.0, Variance = 0.01 },
				new RecommendationDistribution { ArchetypeId = 2, Mean = 59.95, Variance = 0.01 },
				new RecommendationDistribution { ArchetypeId = 3, Mean = 50.0, Variance = 0.01 }
			};

			RecommendationStatistics.PopulateApproximateRanking(candidates, 2000);

			Assert.IsTrue(candidates[0].ProbabilityBest > candidates[1].ProbabilityBest);
			Assert.AreEqual(1, candidates[0].Tier);
			Assert.AreEqual(1, candidates[1].Tier);
			Assert.IsTrue(candidates[2].Tier > 1);
		}
	}
}
