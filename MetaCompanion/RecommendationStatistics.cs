using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaCompanion
{
	internal static class RecommendationStatistics
	{
		internal const double DefaultMatchupPriorGames = 50.0;
		internal const double DefaultRemotePriorGames = 30.0;
		internal const int DefaultPosteriorDraws = 2000;
		private const double NinetyPercentZ = 1.64485362695147;

		internal static double Clamp(double value, double minimum, double maximum)
		{
			return Math.Max(minimum, Math.Min(maximum, value));
		}

		internal static double CalculatePosteriorMeanPercent(
			double priorMeanPercent,
			double observedWinRatePercent,
			double observedGames,
			double priorGames)
		{
			var priorMean = Clamp(priorMeanPercent, 0.0, 100.0);
			var games = Math.Max(0.0, observedGames);
			var strength = Math.Max(0.0, priorGames);
			if (games <= 0.0 || double.IsNaN(observedWinRatePercent) ||
				double.IsInfinity(observedWinRatePercent))
			{
				return priorMean;
			}

			var observed = Clamp(observedWinRatePercent, 0.0, 100.0);
			var denominator = games + strength;
			return denominator <= 0.0
				? priorMean
				: (games * observed + strength * priorMean) / denominator;
		}

		internal static double CalculatePosteriorVariancePercentSquared(
			double priorMeanPercent,
			double observedWinRatePercent,
			double observedGames,
			double priorGames)
		{
			var priorMean = Clamp(priorMeanPercent / 100.0, 0.000001, 0.999999);
			var games = Math.Max(0.0, observedGames);
			var strength = Math.Max(0.000001, priorGames);
			var observed = games > 0.0 && !double.IsNaN(observedWinRatePercent) &&
				!double.IsInfinity(observedWinRatePercent)
				? Clamp(observedWinRatePercent / 100.0, 0.0, 1.0)
				: priorMean;
			var alpha = Math.Max(0.000001, priorMean * strength + observed * games);
			var beta = Math.Max(0.000001,
				(1.0 - priorMean) * strength + (1.0 - observed) * games);
			var total = alpha + beta;
			return alpha * beta / (total * total * (total + 1.0)) * 10000.0;
		}

		internal static double CalculateDataShare(double observedGames, double priorGames)
		{
			var games = Math.Max(0.0, observedGames);
			var strength = Math.Max(0.0, priorGames);
			return games <= 0.0 ? 0.0 : games / (games + strength);
		}

		internal static double CalculateAdaptiveLocalWeight(
			double localEvidence,
			double remotePriorGames)
		{
			var local = Math.Max(0.0, localEvidence);
			var remote = Math.Max(0.0, remotePriorGames);
			return local <= 0.0 ? 0.0 : local / Math.Max(0.000001, local + remote);
		}

		internal static double CalculateKishEffectiveSampleSize(IEnumerable<double> weights)
		{
			var values = (weights ?? Enumerable.Empty<double>())
				.Where(value => value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value))
				.ToList();
			var sum = values.Sum();
			var squared = values.Sum(value => value * value);
			return squared <= 0.0 ? 0.0 : sum * sum / squared;
		}

		internal static void PopulateApproximateRanking(
			IList<RecommendationDistribution> candidates,
			int requestedDraws,
			int seed = 1729)
		{
			if (candidates == null || candidates.Count == 0)
			{
				return;
			}

			var draws = Math.Max(200, Math.Min(5000, requestedDraws));
			var random = new Random(seed);
			var samples = candidates.Select(candidate => new double[draws]).ToArray();
			var bestCounts = new int[candidates.Count];
			for (var draw = 0; draw < draws; draw++)
			{
				var bestIndex = 0;
				var bestValue = double.MinValue;
				for (var index = 0; index < candidates.Count; index++)
				{
					var candidate = candidates[index];
					var sampled = Clamp(
						candidate.Mean + Math.Sqrt(Math.Max(0.0, candidate.Variance)) *
						NextStandardNormal(random),
						0.0,
						100.0);
					samples[index][draw] = sampled;
					if (sampled > bestValue)
					{
						bestValue = sampled;
						bestIndex = index;
					}
				}
				bestCounts[bestIndex]++;
			}

			for (var index = 0; index < candidates.Count; index++)
			{
				var candidate = candidates[index];
				var deviation = Math.Sqrt(Math.Max(0.0, candidate.Variance));
				candidate.Lower90 = Clamp(candidate.Mean - NinetyPercentZ * deviation, 0.0, 100.0);
				candidate.Upper90 = Clamp(candidate.Mean + NinetyPercentZ * deviation, 0.0, 100.0);
				candidate.ProbabilityBest = bestCounts[index] / (double)draws;
			}

			var ordered = Enumerable.Range(0, candidates.Count)
				.OrderByDescending(index => candidates[index].Mean)
				.ToList();
			var tier = 1;
			var tierLeader = ordered[0];
			candidates[tierLeader].Tier = tier;
			foreach (var index in ordered.Skip(1))
			{
				var beatsLeader = 0;
				for (var draw = 0; draw < draws; draw++)
				{
					if (samples[index][draw] >= samples[tierLeader][draw])
					{
						beatsLeader++;
					}
				}
				if (beatsLeader / (double)draws < 0.20)
				{
					tier++;
					tierLeader = index;
				}
				candidates[index].Tier = tier;
			}
		}

		private static double NextStandardNormal(Random random)
		{
			var first = Math.Max(double.Epsilon, random.NextDouble());
			var second = random.NextDouble();
			return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
		}
	}

	internal class RecommendationDistribution
	{
		public int ArchetypeId { get; set; }
		public double Mean { get; set; }
		public double Variance { get; set; }
		public double Lower90 { get; set; }
		public double Upper90 { get; set; }
		public double ProbabilityBest { get; set; }
		public int Tier { get; set; }
	}
}
