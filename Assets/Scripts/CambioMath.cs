using System;
using System.Collections.Generic;

/// <summary>
/// Stateless numeric helpers shared by the search, the Bayesian cambio guard and the
/// belief reporting. Everything here is a pure function over value-bucket vectors
/// (index = Card.Value + 1, covering values -1..10 in buckets 0..11).
/// </summary>
public static class CambioMath
{
    /// <summary>Map a card id to its value bucket index (-1..10 -> 0..11).</summary>
    public static int ValueIdx(int cardId) => new Card(cardId).Value + 1;

    /// <summary>Peak-to-trough span of a log-likelihood vector — 0 means "flat / no signal".</summary>
    public static double Spread(double[] logL)
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        for (int i = 0; i < logL.Length; i++)
        {
            if (logL[i] < mn) mn = logL[i];
            if (logL[i] > mx) mx = logL[i];
        }
        return mx - mn;
    }

    /// <summary>Believed mean and variance of a slot's value under the deck-coherent posterior
    /// P(v) ∝ poolHist[v] · exp(logL[v]). Values outside the current pool get zero weight, so
    /// beliefs stay consistent with what is physically left in the deck. A max-subtract keeps
    /// exp() from under/overflowing for peaked likelihoods.</summary>
    public static (double mean, double variance) MomentsOf(double[] logL, double[] poolHist)
    {
        double maxLog = double.NegativeInfinity;
        for (int b = 0; b < 12; b++)
            if (poolHist[b] > 0 && logL[b] > maxLog) maxLog = logL[b];
        if (double.IsNegativeInfinity(maxLog)) return (0.0, 0.0);   // empty pool

        double num = 0, num2 = 0, den = 0;
        for (int v = -1; v <= 10; v++)
        {
            double w = poolHist[v + 1] * Math.Exp(logL[v + 1] - maxLog);
            num  += v * w;
            num2 += (double)v * v * w;
            den  += w;
        }
        if (den <= 0) return (0.0, 0.0);
        double mean = num / den;
        double variance = num2 / den - mean * mean;
        return (mean, variance < 0 ? 0.0 : variance);   // clamp tiny negative from rounding
    }

    /// <summary>E[value] for a slot; thin wrapper over MomentsOf for telemetry.</summary>
    public static double ExpectedValue(double[] logL, double[] poolHist) => MomentsOf(logL, poolHist).mean;

    /// <summary>Standard normal CDF (Zelen &amp; Severo / A&amp;S 26.2.17, |error| &lt; 7.5e-8).</summary>
    public static double NormalCdf(double z)
    {
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2.0);
        double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 +
                   t * (-1.821255978 + t * 1.330274429))));
        return z >= 0 ? 1.0 - p : p;
    }

    /// <summary>Fill a 12-bucket histogram of the pool's value distribution and return the
    /// pool's mean value. Buckets are indexed Value+1.</summary>
    public static double PoolHistogram(List<int> pool, double[] hist12)
    {
        Array.Clear(hist12, 0, hist12.Length);
        double sum = 0;
        foreach (int id in pool)
        {
            int val = new Card(id).Value;
            hist12[val + 1] += 1.0;
            sum += val;
        }
        return pool.Count > 0 ? sum / pool.Count : 0.0;
    }
}
