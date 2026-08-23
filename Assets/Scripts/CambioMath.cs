using System;
using System.Collections.Generic;


// format looks like index = Card.Value + 1, covering values -1 to 10 in buckets 0 to 11
public static class CambioMath
{
    // card id = its value bucket index
    public static int ValueIdx(int cardId) => new Card(cardId).Value + 1;

    // spread of a log-likelihood vector; 0 means no signal
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

    /* believed mean and variance of a slot's value under the deck-coherent posterior,
       weighting each value by what is physically still left in the pool */
    public static (double mean, double variance) MomentsOf(double[] logL, double[] poolHist)
    {
        double maxLog = double.NegativeInfinity;
        for (int b = 0; b < 12; b++)
            if (poolHist[b] > 0 && logL[b] > maxLog) maxLog = logL[b];
        if (double.IsNegativeInfinity(maxLog)) return (0.0, 0.0);

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
        return (mean, variance < 0 ? 0.0 : variance);
    }

    // expected value of a slot; thin wrapper over MomentsOf
    public static double ExpectedValue(double[] logL, double[] poolHist) => MomentsOf(logL, poolHist).mean;

    // standard normal CDF approximation
    public static double NormalCdf(double z)
    {
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2.0);
        double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 +
                   t * (-1.821255978 + t * 1.330274429))));
        return z >= 0 ? 1.0 - p : p;
    }

    /* fill a 12-bucket histogram of the pool's value distribution and
       return the pool's mean value */
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
